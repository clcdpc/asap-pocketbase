using System.Net;
using System.Net.Http;
using System.Text;
using Asap.Web.Features.Email;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task AcceptedPostmarkResultIsRecordedAfterCallerCancellationAndNeverRetried()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var seeded = await SeedSensitiveOutboxAsync($"accepted-cancel-{Guid.NewGuid():N}");
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var credentialProtector = factory.Services.GetRequiredService<IntegrationCredentialProtector>();
        EmailOutcomeSystemSettingsSnapshot? settings = null;
        var postCount = 0;

        using var client = new HttpClient(new EmailOutcomePostmarkHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/server")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"ID\":42,\"DeliveryType\":\"Live\"}", Encoding.UTF8, "application/json")
                });
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/email")
            {
                Interlocked.Increment(ref postCount);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"ErrorCode\":0,\"MessageID\":\"accepted-email-368\"}",
                        Encoding.UTF8,
                        "application/json")
                });
            }

            return Task.FromException<HttpResponseMessage>(
                new InvalidOperationException(
                    $"Unexpected Postmark request: {request.Method} {request.RequestUri?.AbsolutePath}."));
        }));

        using var cancellation = new CancellationTokenSource();
        try
        {
            settings = await ConfigureEmailOutcomePostmarkAsync(contextFactory, credentialProtector);
            var postmarkSender = new PostmarkEmailSender(
                contextFactory,
                credentialProtector,
                new EmailOutcomeHttpClientFactory(client));
            var sender = new EmailOutcomeCancelAfterAcceptedSender(postmarkSender, cancellation);
            var jobs = CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default);
            var before = await ReadEmailOutcomeSnapshotAsync(seeded.OutboxId);
            Assert.AreEqual("pending", before.Status);
            Assert.AreEqual(0, before.AttemptCount);
            Assert.IsNull(before.LeaseId);

            await jobs.DeliverAsync(seeded.OutboxId, cancellation.Token);
            Assert.IsTrue(cancellation.IsCancellationRequested);
            Assert.AreEqual(1, Volatile.Read(ref postCount));

            var sent = await ReadEmailOutcomeSnapshotAsync(seeded.OutboxId);
            Assert.AreEqual("sent", sent.Status);
            Assert.AreEqual("accepted-email-368", sent.ProviderMessageId);
            Assert.AreEqual(1, sent.AttemptCount);
            Assert.IsNull(sent.LastErrorCode);
            Assert.IsNull(sent.SendingStartedUtc);
            Assert.IsNull(sent.LeaseId);
            Assert.IsNull(sent.LeaseExpiresUtc);
            Assert.AreNotEqual(Convert.ToHexString(before.RowVersion), Convert.ToHexString(sent.RowVersion));

            await jobs.SweepAsync(CancellationToken.None);
            CollectionAssert.DoesNotContain(dispatcher!.EnqueuedIds, seeded.OutboxId);

            var operations = new EmailOperationsService(
                contextFactory,
                dispatcher,
                sender,
                factory.Services.GetRequiredService<RecipientDomainPolicy>(),
                timeProvider!,
                factory.Services.GetRequiredService<StaffEligibilityService>());
            var retry = await operations.RetryAsync(
                actor,
                seeded.OutboxId,
                StaffVersion.Encode(sent.RowVersion),
                CancellationToken.None);
            Assert.AreEqual("email_not_retryable", retry.Code);

            await jobs.DeliverAsync(seeded.OutboxId, CancellationToken.None);
            Assert.AreEqual(1, Volatile.Read(ref postCount));
            var unchanged = await ReadEmailOutcomeSnapshotAsync(seeded.OutboxId);
            Assert.AreEqual("sent", unchanged.Status);
            Assert.AreEqual("accepted-email-368", unchanged.ProviderMessageId);
            CollectionAssert.AreEqual(sent.RowVersion, unchanged.RowVersion);
        }
        finally
        {
            try
            {
                await DeleteEmailOutcomeFixtureAsync(seeded);
            }
            finally
            {
                if (settings is not null)
                {
                    await RestoreEmailOutcomePostmarkAsync(contextFactory, settings);
                }
            }
        }
    }

    [TestMethod]
    public async Task AcceptedPostmarkResultWithLostSqlFenceRemainsQuarantinedAndCannotBeReplayed()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var seeded = await SeedSensitiveOutboxAsync($"accepted-fence-loss-{Guid.NewGuid():N}");
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var credentialProtector = factory.Services.GetRequiredService<IntegrationCredentialProtector>();
        EmailOutcomeSystemSettingsSnapshot? settings = null;
        using var cancellation = new CancellationTokenSource();
        var postCount = 0;

        using var client = new HttpClient(new EmailOutcomePostmarkHandler(async (request, _) =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/server")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"ID\":42,\"DeliveryType\":\"Live\"}", Encoding.UTF8, "application/json")
                };
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/email")
            {
                Interlocked.Increment(ref postCount);
                // Compete with the accepted-response recorder by expiring and rowversioning its live lease.
                await SetLeaseExpiryAsync(seeded.OutboxId, expired: true);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"ErrorCode\":0,\"MessageID\":\"accepted-before-fence-loss\"}",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            throw new InvalidOperationException(
                $"Unexpected Postmark request: {request.Method} {request.RequestUri?.AbsolutePath}.");
        }));

        try
        {
            settings = await ConfigureEmailOutcomePostmarkAsync(contextFactory, credentialProtector);
            var postmarkSender = new PostmarkEmailSender(
                contextFactory,
                credentialProtector,
                new EmailOutcomeHttpClientFactory(client));
            var sender = new EmailOutcomeCancelAfterAcceptedSender(postmarkSender, cancellation);
            var jobs = CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default);
            var before = await ReadEmailOutcomeDispatchSnapshotAsync(seeded.OutboxId);
            Assert.AreEqual("pending", before.Status);
            Assert.AreEqual(0, before.AttemptCount);
            Assert.IsNull(before.LeaseId);
            var beforeCounts = await ReadEmailOutcomeRelatedCountsAsync(
                contextFactory, seeded.OutboxId, before.BusinessKey!);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => jobs.DeliverAsync(seeded.OutboxId, cancellation.Token));

            Assert.IsTrue(cancellation.IsCancellationRequested);
            Assert.AreEqual(1, Volatile.Read(ref postCount));
            var sending = await ReadEmailOutcomeDispatchSnapshotAsync(seeded.OutboxId);
            Assert.AreEqual("sending", sending.Status);
            Assert.IsNull(sending.LastErrorCode);
            Assert.IsNull(sending.ProviderMessageId,
                "A provider ID is not durable until the fenced SQL completion succeeds.");
            Assert.AreEqual(1, sending.AttemptCount);
            Assert.IsNotNull(sending.LastAttemptUtc);
            Assert.IsNotNull(sending.SendingStartedUtc);
            Assert.IsNotNull(sending.LeaseId);
            Assert.IsNotNull(sending.LeaseExpiresUtc);
            Assert.AreNotEqual(Convert.ToHexString(before.RowVersion), Convert.ToHexString(sending.RowVersion));

            await jobs.SweepAsync(CancellationToken.None);
            var quarantined = await ReadEmailOutcomeDispatchSnapshotAsync(seeded.OutboxId);
            Assert.AreEqual("failed", quarantined.Status);
            Assert.AreEqual("ambiguous_expired_lease", quarantined.LastErrorCode);
            StringAssert.Contains(quarantined.LastErrorDetail!, "unknown");
            Assert.AreEqual(sending.AttemptCount, quarantined.AttemptCount);
            Assert.AreEqual(sending.LastAttemptUtc, quarantined.LastAttemptUtc);
            Assert.AreEqual(sending.SendingStartedUtc, quarantined.SendingStartedUtc);
            Assert.IsNull(quarantined.ProviderMessageId);
            Assert.IsNull(quarantined.NextAttemptUtc);
            Assert.IsNull(quarantined.LeaseId);
            Assert.IsNull(quarantined.LeaseExpiresUtc);

            var operations = new EmailOperationsService(
                contextFactory,
                dispatcher!,
                sender,
                factory.Services.GetRequiredService<RecipientDomainPolicy>(),
                timeProvider!,
                factory.Services.GetRequiredService<StaffEligibilityService>());
            var retry = await operations.RetryAsync(
                actor,
                seeded.OutboxId,
                StaffVersion.Encode(quarantined.RowVersion),
                CancellationToken.None);
            Assert.AreEqual("email_not_retryable", retry.Code);

            await jobs.DeliverAsync(seeded.OutboxId, CancellationToken.None);
            Assert.AreEqual(1, Volatile.Read(ref postCount));
            AssertEmailOutcomeDispatchSnapshotEqual(
                quarantined,
                await ReadEmailOutcomeDispatchSnapshotAsync(seeded.OutboxId));
            var afterCounts = await ReadEmailOutcomeRelatedCountsAsync(
                contextFactory, seeded.OutboxId, quarantined.BusinessKey!);
            Assert.AreEqual(beforeCounts.OutboxRows, afterCounts.OutboxRows);
            Assert.AreEqual(beforeCounts.DeliveryEvents, afterCounts.DeliveryEvents);
            CollectionAssert.DoesNotContain(dispatcher!.EnqueuedIds, seeded.OutboxId);
        }
        finally
        {
            try
            {
                await DeleteEmailOutcomeFixtureAsync(seeded);
            }
            finally
            {
                if (settings is not null)
                {
                    await RestoreEmailOutcomePostmarkAsync(contextFactory, settings);
                }
            }
        }
    }

    [TestMethod]
    public async Task PostDispatchUnknownLeaseIsQuarantinedAndCannotBeRetried()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var credentialProtector = factory.Services.GetRequiredService<IntegrationCredentialProtector>();
        using var cancellation = new CancellationTokenSource();
        var postCount = 0;

        using var client = new HttpClient(new EmailOutcomePostmarkHandler((request, requestToken) =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/server")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"ID\":42,\"DeliveryType\":\"Live\"}", Encoding.UTF8, "application/json")
                });
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/email")
            {
                Interlocked.Increment(ref postCount);
                cancellation.Cancel();
                return Task.FromException<HttpResponseMessage>(new OperationCanceledException(
                    "Postmark accepted the request for dispatch, but no response was observed.",
                    requestToken));
            }

            return Task.FromException<HttpResponseMessage>(
                new InvalidOperationException(
                    $"Unexpected Postmark request: {request.Method} {request.RequestUri?.AbsolutePath}."));
        }));

        var seeded = await SeedSensitiveOutboxAsync($"unknown-cancel-{Guid.NewGuid():N}");
        EmailOutcomeSystemSettingsSnapshot? settings = null;
        try
        {
            settings = await ConfigureEmailOutcomePostmarkAsync(contextFactory, credentialProtector);
            var sender = new PostmarkEmailSender(
                contextFactory,
                credentialProtector,
                new EmailOutcomeHttpClientFactory(client));
            var jobs = CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default);
            var before = await ReadEmailOutcomeDispatchSnapshotAsync(seeded.OutboxId);
            Assert.AreEqual("pending", before.Status);
            Assert.IsNotNull(before.BusinessKey);
            Assert.AreEqual(0, before.AttemptCount);
            Assert.IsNull(before.SendingStartedUtc);
            Assert.IsNull(before.LeaseId);
            var beforeCounts = await ReadEmailOutcomeRelatedCountsAsync(
                contextFactory, seeded.OutboxId, before.BusinessKey!);
            Assert.AreEqual(1, beforeCounts.OutboxRows);

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => jobs.DeliverAsync(seeded.OutboxId, cancellation.Token));

            Assert.IsTrue(cancellation.IsCancellationRequested);
            Assert.AreEqual(1, Volatile.Read(ref postCount));
            var sending = await ReadEmailOutcomeDispatchSnapshotAsync(seeded.OutboxId);
            Assert.AreEqual(seeded.OutboxId, sending.Id);
            Assert.AreEqual(before.BusinessKey, sending.BusinessKey);
            Assert.AreEqual("sending", sending.Status);
            Assert.IsNull(sending.LastErrorCode);
            Assert.IsNull(sending.LastErrorDetail);
            Assert.IsNull(sending.ProviderMessageId);
            Assert.AreEqual(1, sending.AttemptCount);
            Assert.IsNotNull(sending.LastAttemptUtc);
            Assert.IsNotNull(sending.SendingStartedUtc);
            Assert.IsNotNull(sending.LeaseId);
            Assert.IsNotNull(sending.LeaseExpiresUtc);
            Assert.AreNotEqual(Convert.ToHexString(before.RowVersion), Convert.ToHexString(sending.RowVersion));

            await SetLeaseExpiryAsync(seeded.OutboxId, expired: true);
            var expired = await ReadEmailOutcomeDispatchSnapshotAsync(seeded.OutboxId);
            Assert.AreEqual("sending", expired.Status);
            Assert.AreEqual(sending.BusinessKey, expired.BusinessKey);
            Assert.AreEqual(sending.AttemptCount, expired.AttemptCount);
            Assert.AreEqual(sending.SendingStartedUtc, expired.SendingStartedUtc);
            Assert.AreEqual(sending.LeaseId, expired.LeaseId);

            await jobs.SweepAsync(CancellationToken.None);
            var quarantined = await ReadEmailOutcomeDispatchSnapshotAsync(seeded.OutboxId);
            Assert.AreEqual("failed", quarantined.Status);
            Assert.AreEqual("ambiguous_expired_lease", quarantined.LastErrorCode);
            Assert.IsNotNull(quarantined.LastErrorDetail);
            StringAssert.Contains(quarantined.LastErrorDetail, "unknown");
            Assert.AreEqual(sending.BusinessKey, quarantined.BusinessKey);
            Assert.AreEqual(sending.AttemptCount, quarantined.AttemptCount);
            Assert.AreEqual(sending.LastAttemptUtc, quarantined.LastAttemptUtc);
            Assert.AreEqual(sending.SendingStartedUtc, quarantined.SendingStartedUtc);
            Assert.IsNull(quarantined.ProviderMessageId);
            Assert.IsNull(quarantined.NextAttemptUtc);
            Assert.IsNull(quarantined.LeaseId);
            Assert.IsNull(quarantined.LeaseExpiresUtc);
            Assert.AreNotEqual(Convert.ToHexString(expired.RowVersion), Convert.ToHexString(quarantined.RowVersion));
            CollectionAssert.DoesNotContain(dispatcher!.EnqueuedIds, seeded.OutboxId);

            var beforeRetryCounts = await ReadEmailOutcomeRelatedCountsAsync(
                contextFactory, seeded.OutboxId, quarantined.BusinessKey!);
            Assert.AreEqual(1, beforeRetryCounts.OutboxRows);
            var operations = new EmailOperationsService(
                contextFactory,
                dispatcher,
                sender,
                factory.Services.GetRequiredService<RecipientDomainPolicy>(),
                timeProvider!,
                factory.Services.GetRequiredService<StaffEligibilityService>());
            var retry = await operations.RetryAsync(
                actor,
                seeded.OutboxId,
                StaffVersion.Encode(quarantined.RowVersion),
                CancellationToken.None);
            Assert.AreEqual("email_not_retryable", retry.Code);

            var afterRetry = await ReadEmailOutcomeDispatchSnapshotAsync(seeded.OutboxId);
            AssertEmailOutcomeDispatchSnapshotEqual(quarantined, afterRetry);
            var afterRetryCounts = await ReadEmailOutcomeRelatedCountsAsync(
                contextFactory, seeded.OutboxId, quarantined.BusinessKey!);
            Assert.AreEqual(beforeRetryCounts.OutboxRows, afterRetryCounts.OutboxRows);
            Assert.AreEqual(beforeRetryCounts.DeliveryEvents, afterRetryCounts.DeliveryEvents);
            CollectionAssert.DoesNotContain(dispatcher.EnqueuedIds, seeded.OutboxId);

            await jobs.DeliverAsync(seeded.OutboxId, CancellationToken.None);
            Assert.AreEqual(1, Volatile.Read(ref postCount));
            var afterStaleDelivery = await ReadEmailOutcomeDispatchSnapshotAsync(seeded.OutboxId);
            AssertEmailOutcomeDispatchSnapshotEqual(quarantined, afterStaleDelivery);
        }
        finally
        {
            try
            {
                await DeleteEmailOutcomeFixtureAsync(seeded);
            }
            finally
            {
                if (settings is not null)
                {
                    await RestoreEmailOutcomePostmarkAsync(contextFactory, settings);
                }
            }
        }
    }

    [TestMethod]
    [DataRow("provider_failed")]
    [DataRow("provider_timeout")]
    [DataRow("transport_failure")]
    [DataRow("ambiguous_expired_lease")]
    public async Task UncertifiedProviderFailureCannotBeManuallyRetried(string errorCode)
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var seeded = await SeedSensitiveOutboxAsync($"retry-denied-{Guid.NewGuid():N}");
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var localDispatcher = new RecordingOutboxDispatcher();

        try
        {
            await using (var connection = new SqlConnection(databaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new SqlCommand(
                    "UPDATE [asap].[EmailOutbox] SET [Status] = N'failed', [AttemptCount] = 1, " +
                    "[LastAttemptUtc] = DATEADD(minute, -1, SYSUTCDATETIME()), " +
                    "[SendingStartedUtc] = CASE WHEN @errorCode IN (N'provider_timeout', N'ambiguous_expired_lease') " +
                    "THEN DATEADD(minute, -1, SYSUTCDATETIME()) ELSE NULL END, " +
                    "[LeaseId] = NULL, [LeaseExpiresUtc] = NULL, [NextAttemptUtc] = NULL, " +
                    "[LastErrorCode] = @errorCode, " +
                    "[LastErrorDetail] = N'The provider outcome is not certified as no-send.' " +
                    "WHERE [Id] = @id AND [Status] = N'pending';",
                    connection);
                command.Parameters.Add("@errorCode", System.Data.SqlDbType.NVarChar, 100).Value = errorCode;
                command.Parameters.Add("@id", System.Data.SqlDbType.BigInt).Value = seeded.OutboxId;
                Assert.AreEqual(1, await command.ExecuteNonQueryAsync());
            }

            var before = await ReadEmailOutcomeDispatchSnapshotAsync(seeded.OutboxId);
            Assert.AreEqual("failed", before.Status);
            Assert.AreEqual(errorCode, before.LastErrorCode);
            Assert.AreEqual(1, before.AttemptCount);
            Assert.IsNotNull(before.LastAttemptUtc);
            if (errorCode is "provider_timeout" or "ambiguous_expired_lease")
            {
                Assert.IsNotNull(before.SendingStartedUtc);
            }
            else
            {
                Assert.IsNull(before.SendingStartedUtc,
                    "An untrusted failed row may lack send-start evidence; refusal cannot depend on that marker.");
            }
            Assert.IsNull(before.LeaseId);
            Assert.IsNull(before.LeaseExpiresUtc);
            var beforeCounts = await ReadEmailOutcomeRelatedCountsAsync(
                contextFactory, seeded.OutboxId, before.BusinessKey!);

            var operations = new EmailOperationsService(
                contextFactory,
                localDispatcher,
                new RecordingEmailSender(),
                factory!.Services.GetRequiredService<RecipientDomainPolicy>(),
                timeProvider!,
                factory.Services.GetRequiredService<StaffEligibilityService>());
            var retry = await operations.RetryAsync(
                actor,
                seeded.OutboxId,
                StaffVersion.Encode(before.RowVersion),
                CancellationToken.None);
            Assert.AreEqual("email_not_retryable", retry.Code);

            AssertEmailOutcomeDispatchSnapshotEqual(
                before,
                await ReadEmailOutcomeDispatchSnapshotAsync(seeded.OutboxId));
            var afterCounts = await ReadEmailOutcomeRelatedCountsAsync(
                contextFactory, seeded.OutboxId, before.BusinessKey!);
            Assert.AreEqual(beforeCounts.OutboxRows, afterCounts.OutboxRows);
            Assert.AreEqual(beforeCounts.DeliveryEvents, afterCounts.DeliveryEvents);
            Assert.AreEqual(0, localDispatcher.EnqueuedIds.Count);
        }
        finally
        {
            await DeleteEmailOutcomeFixtureAsync(seeded);
        }
    }

    private static async Task<EmailOutcomeSystemSettingsSnapshot> ConfigureEmailOutcomePostmarkAsync(
        IDbContextFactory<AsapDbContext> contextFactory,
        IntegrationCredentialProtector credentialProtector)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var system = await context.EmailSettings.SingleAsync(item => item.OrganizationId == 1);
        var snapshot = new EmailOutcomeSystemSettingsSnapshot(system.ProtectedServerToken, system.FromAddress);
        system.ProtectedServerToken = credentialProtector.Protect($"email-outcome-{Guid.NewGuid():N}");
        system.FromAddress = "sender@example.org";
        await context.SaveChangesAsync();
        return snapshot;
    }

    private static async Task RestoreEmailOutcomePostmarkAsync(
        IDbContextFactory<AsapDbContext> contextFactory,
        EmailOutcomeSystemSettingsSnapshot snapshot)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var system = await context.EmailSettings.SingleAsync(item => item.OrganizationId == 1);
        system.ProtectedServerToken = snapshot.ProtectedServerToken;
        system.FromAddress = snapshot.FromAddress;
        await context.SaveChangesAsync();
    }

    private static async Task<EmailOutcomeOutboxSnapshot> ReadEmailOutcomeSnapshotAsync(long outboxId)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT [Status], [LastErrorCode], [ProviderMessageId], [AttemptCount], [SendingStartedUtc], " +
            "[LeaseId], [LeaseExpiresUtc], [RowVersion] FROM [asap].[EmailOutbox] WHERE [Id] = @id;",
            connection);
        command.Parameters.Add("@id", System.Data.SqlDbType.BigInt).Value = outboxId;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        return new EmailOutcomeOutboxSnapshot(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetDateTime(4),
            reader.IsDBNull(5) ? null : reader.GetGuid(5),
            reader.IsDBNull(6) ? null : reader.GetDateTime(6),
            (byte[])reader[7]);
    }

    private static async Task<EmailOutcomeDispatchSnapshot> ReadEmailOutcomeDispatchSnapshotAsync(long outboxId)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT [Id], [BusinessKey], [Status], [LastErrorCode], [LastErrorDetail], [ProviderMessageId], " +
            "[AttemptCount], [NextAttemptUtc], [LastAttemptUtc], [SendingStartedUtc], [LeaseId], " +
            "[LeaseExpiresUtc], [RowVersion] FROM [asap].[EmailOutbox] WHERE [Id] = @id;",
            connection);
        command.Parameters.Add("@id", System.Data.SqlDbType.BigInt).Value = outboxId;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        return new EmailOutcomeDispatchSnapshot(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetDateTime(7),
            reader.IsDBNull(8) ? null : reader.GetDateTime(8),
            reader.IsDBNull(9) ? null : reader.GetDateTime(9),
            reader.IsDBNull(10) ? null : reader.GetGuid(10),
            reader.IsDBNull(11) ? null : reader.GetDateTime(11),
            (byte[])reader[12]);
    }

    private static async Task<(int OutboxRows, int DeliveryEvents)> ReadEmailOutcomeRelatedCountsAsync(
        IDbContextFactory<AsapDbContext> contextFactory,
        long outboxId,
        string businessKey)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var outboxRows = await context.EmailOutbox.AsNoTracking()
            .CountAsync(item => item.BusinessKey == businessKey);
        var deliveryEvents = await context.EmailDeliveryEvents.AsNoTracking()
            .CountAsync(item => item.EmailOutboxId == outboxId);
        return (outboxRows, deliveryEvents);
    }

    private static void AssertEmailOutcomeDispatchSnapshotEqual(
        EmailOutcomeDispatchSnapshot expected,
        EmailOutcomeDispatchSnapshot actual)
    {
        Assert.AreEqual(expected.Id, actual.Id);
        Assert.AreEqual(expected.BusinessKey, actual.BusinessKey);
        Assert.AreEqual(expected.Status, actual.Status);
        Assert.AreEqual(expected.LastErrorCode, actual.LastErrorCode);
        Assert.AreEqual(expected.LastErrorDetail, actual.LastErrorDetail);
        Assert.AreEqual(expected.ProviderMessageId, actual.ProviderMessageId);
        Assert.AreEqual(expected.AttemptCount, actual.AttemptCount);
        Assert.AreEqual(expected.NextAttemptUtc, actual.NextAttemptUtc);
        Assert.AreEqual(expected.LastAttemptUtc, actual.LastAttemptUtc);
        Assert.AreEqual(expected.SendingStartedUtc, actual.SendingStartedUtc);
        Assert.AreEqual(expected.LeaseId, actual.LeaseId);
        Assert.AreEqual(expected.LeaseExpiresUtc, actual.LeaseExpiresUtc);
        CollectionAssert.AreEqual(expected.RowVersion, actual.RowVersion);
    }

    private static async Task DeleteEmailOutcomeFixtureAsync(SeededSensitiveOutbox seeded)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using (var deleteOutbox = new SqlCommand(
            "DELETE FROM [asap].[EmailOutbox] WHERE [Id] = @id;", connection))
        {
            deleteOutbox.Parameters.Add("@id", System.Data.SqlDbType.BigInt).Value = seeded.OutboxId;
            await deleteOutbox.ExecuteNonQueryAsync();
        }

        await using (var deleteStaff = new SqlCommand(
            "DELETE FROM [asap].[StaffUser] WHERE [Id] = @id;", connection))
        {
            deleteStaff.Parameters.Add("@id", System.Data.SqlDbType.BigInt).Value = seeded.StaffUserId;
            await deleteStaff.ExecuteNonQueryAsync();
        }
    }

    private sealed record EmailOutcomeSystemSettingsSnapshot(string? ProtectedServerToken, string? FromAddress);

    private sealed record EmailOutcomeOutboxSnapshot(
        string Status,
        string? LastErrorCode,
        string? ProviderMessageId,
        int AttemptCount,
        DateTime? SendingStartedUtc,
        Guid? LeaseId,
        DateTime? LeaseExpiresUtc,
        byte[] RowVersion);

    private sealed record EmailOutcomeDispatchSnapshot(
        long Id,
        string? BusinessKey,
        string Status,
        string? LastErrorCode,
        string? LastErrorDetail,
        string? ProviderMessageId,
        int AttemptCount,
        DateTime? NextAttemptUtc,
        DateTime? LastAttemptUtc,
        DateTime? SendingStartedUtc,
        Guid? LeaseId,
        DateTime? LeaseExpiresUtc,
        byte[] RowVersion);

    private sealed class EmailOutcomePostmarkHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => respond(request, cancellationToken);
    }

    private sealed class EmailOutcomeHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class EmailOutcomeCancelAfterAcceptedSender(
        IEmailSender inner,
        CancellationTokenSource cancellation) : IEmailSender
    {
        public Task<EmailTransportReadiness> CheckReadinessAsync(
            int organizationId,
            CancellationToken cancellationToken) =>
            inner.CheckReadinessAsync(organizationId, cancellationToken);

        public async Task<EmailSendResult> SendAsync(EmailEnvelope envelope, CancellationToken cancellationToken)
        {
            var result = await inner.SendAsync(envelope, cancellationToken);
            if (result.Outcome == EmailSendOutcome.Sent && !string.IsNullOrWhiteSpace(result.ProviderMessageId))
            {
                cancellation.Cancel();
            }

            return result;
        }
    }
}
