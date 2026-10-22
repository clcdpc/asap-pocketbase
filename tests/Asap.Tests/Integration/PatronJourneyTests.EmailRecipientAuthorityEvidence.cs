using System.Data;
using Asap.Web.Features.Email;
using Microsoft.Data.SqlClient;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task SensitiveRecipientAuthorityIsRevalidatedAfterReadinessBeforeProviderIntent()
    {
        const int targetLibraryId = 91241;
        const int otherLibraryId = 91242;
        await SeedEmailRecipientAuthorityOrganizationsAsync(targetLibraryId, otherLibraryId);

        var seededOutboxes = new List<SeededSensitiveOutbox>();
        var businessEventIds = new List<long>();
        var senders = new List<EmailRecipientAuthorityBarrierSender>();
        try
        {
            var mutations = new[]
            {
                new EmailRecipientAuthorityMutation("staff deactivated", EmailRecipientMutationKind.DeactivateStaff),
                new EmailRecipientAuthorityMutation("staff owner moved", EmailRecipientMutationKind.MoveStaffOwner),
                new EmailRecipientAuthorityMutation("super-admin role and scope lost", EmailRecipientMutationKind.RemoveSuperAdminScope, "super_admin", 1),
                new EmailRecipientAuthorityMutation("authentication email changed", EmailRecipientMutationKind.ChangeAuthenticationEmail),
                new EmailRecipientAuthorityMutation("notification destination changed", EmailRecipientMutationKind.ChangeNotificationEmail),
                new EmailRecipientAuthorityMutation("authorization library deactivated", EmailRecipientMutationKind.DeactivateAuthorizationLibrary, Role: "super_admin", StaffOrganizationId: 1, AffectsOrganization: true),
                new EmailRecipientAuthorityMutation("authorization organization changed to branch", EmailRecipientMutationKind.ReclassifyAuthorizationOrganizationAsBranch, Role: "super_admin", StaffOrganizationId: 1, AffectsOrganization: true),
                new EmailRecipientAuthorityMutation("weekly summary address changed", EmailRecipientMutationKind.ChangeWeeklySummaryEmail, AddressKind: "weekly_summary", WeeklyEnabled: true, WeeklyEmail: "weekly-original@example.org"),
                new EmailRecipientAuthorityMutation("weekly summary disabled", EmailRecipientMutationKind.DisableWeeklySummary, AddressKind: "weekly_summary", WeeklyEnabled: true, WeeklyEmail: "weekly-disabled@example.org")
            };

            foreach (var mutation in mutations)
            {
                var seeded = await SeedSensitiveOutboxAsync(
                    $"authority-barrier-{Guid.NewGuid():N}",
                    role: mutation.Role,
                    staffOrganizationId: mutation.StaffOrganizationId ?? targetLibraryId,
                    authorizationOrganizationId: targetLibraryId,
                    addressKind: mutation.AddressKind,
                    weeklyEnabled: mutation.WeeklyEnabled,
                    weeklyEmail: mutation.WeeklyEmail);
                seededOutboxes.Add(seeded);

                var sender = new EmailRecipientAuthorityBarrierSender(
                    pauseReadiness: true,
                    pauseSend: false,
                    providerMessageId: $"authority-{Guid.NewGuid():N}");
                senders.Add(sender);
                var jobs = CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default);
                var before = await ReadEmailOutcomeDispatchSnapshotAsync(seeded.OutboxId);
                var beforeIdentity = await ReadEmailRecipientAuthorityIdentityAsync(seeded.OutboxId);
                Assert.AreEqual("pending", before.Status, mutation.Name);
                Assert.AreEqual(0, before.AttemptCount, mutation.Name);
                Assert.IsNull(before.LeaseId, mutation.Name);
                Assert.AreEqual("staff_authorization_sensitive", beforeIdentity.DeliveryClass, mutation.Name);
                Assert.AreEqual((long?)seeded.StaffUserId, beforeIdentity.RecipientStaffUserId, mutation.Name);
                Assert.AreEqual((int?)targetLibraryId, beforeIdentity.AuthorizationOrganizationId, mutation.Name);
                Assert.AreEqual(mutation.AddressKind, beforeIdentity.RecipientAddressKind, mutation.Name);
                Assert.AreEqual(seeded.ToAddress, beforeIdentity.ToAddress, mutation.Name);

                var delivery = jobs.DeliverAsync(seeded.OutboxId, CancellationToken.None);
                var claimedRowVersion = string.Empty;
                try
                {
                    await sender.ReadinessEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    var claimed = await ReadEmailOutcomeDispatchSnapshotAsync(seeded.OutboxId);
                    Assert.AreEqual("sending", claimed.Status, mutation.Name);
                    Assert.AreEqual("pre_send_check_pending", claimed.LastErrorCode, mutation.Name);
                    Assert.AreEqual(1, claimed.AttemptCount, mutation.Name);
                    Assert.IsNotNull(claimed.LastAttemptUtc, mutation.Name);
                    Assert.IsNotNull(claimed.SendingStartedUtc, mutation.Name);
                    Assert.IsNotNull(claimed.LeaseId, mutation.Name);
                    Assert.IsNotNull(claimed.LeaseExpiresUtc, mutation.Name);
                    Assert.AreNotEqual(Convert.ToHexString(before.RowVersion), Convert.ToHexString(claimed.RowVersion), mutation.Name);
                    Assert.IsTrue(
                        await EmailRecipientAuthorityHasLiveLeaseAsync(seeded.OutboxId, claimed.LeaseId!.Value),
                        $"{mutation.Name} must pause after a live SQL lease is acquired.");
                    claimedRowVersion = Convert.ToHexString(claimed.RowVersion);

                    await ApplyEmailRecipientAuthorityMutationAsync(
                        mutation,
                        seeded,
                        targetLibraryId,
                        otherLibraryId);
                    var stillClaimed = await ReadEmailOutcomeDispatchSnapshotAsync(seeded.OutboxId);
                    AssertEmailOutcomeDispatchSnapshotEqual(claimed, stillClaimed);
                    Assert.AreEqual(
                        beforeIdentity,
                        await ReadEmailRecipientAuthorityIdentityAsync(seeded.OutboxId),
                        mutation.Name);

                    sender.ReleaseReadiness();
                    await delivery;
                }
                finally
                {
                    sender.ReleaseAll();
                    try
                    {
                        await delivery;
                    }
                    finally
                    {
                        if (mutation.AffectsOrganization)
                        {
                            await RestoreEmailRecipientAuthorityOrganizationAsync(targetLibraryId);
                        }
                    }
                }

                var state = await ReadOutboxStateAsync(seeded.OutboxId);
                var suppressed = await ReadEmailOutcomeDispatchSnapshotAsync(seeded.OutboxId);
                Assert.AreEqual(
                    beforeIdentity,
                    await ReadEmailRecipientAuthorityIdentityAsync(seeded.OutboxId),
                    mutation.Name);
                Assert.AreEqual("suppressed", state.Status, mutation.Name);
                Assert.AreEqual("recipient_authorization_changed", state.SuppressionReason, mutation.Name);
                Assert.AreEqual(1, suppressed.AttemptCount, mutation.Name);
                Assert.IsNotNull(suppressed.LastAttemptUtc, mutation.Name);
                Assert.IsNull(suppressed.LastErrorCode, mutation.Name);
                Assert.IsNull(suppressed.ProviderMessageId, mutation.Name);
                Assert.IsNull(suppressed.NextAttemptUtc, mutation.Name);
                Assert.IsNull(suppressed.SendingStartedUtc, mutation.Name);
                Assert.IsNull(suppressed.LeaseId, mutation.Name);
                Assert.IsNull(suppressed.LeaseExpiresUtc, mutation.Name);
                Assert.AreNotEqual(Convert.ToHexString(before.RowVersion), Convert.ToHexString(suppressed.RowVersion), mutation.Name);
                Assert.AreNotEqual(claimedRowVersion, Convert.ToHexString(suppressed.RowVersion), mutation.Name);
                Assert.AreEqual(0, sender.SendCount, mutation.Name);
                Assert.AreEqual(1, await CountEmailOutboxBusinessKeyAsync(suppressed.BusinessKey!), mutation.Name);

                await jobs.DeliverAsync(seeded.OutboxId, CancellationToken.None);
                AssertEmailOutcomeDispatchSnapshotEqual(
                    suppressed,
                    await ReadEmailOutcomeDispatchSnapshotAsync(seeded.OutboxId));
                Assert.AreEqual(0, sender.SendCount, mutation.Name);
            }

            var unchanged = await SeedSensitiveOutboxAsync(
                $"authority-unchanged-{Guid.NewGuid():N}",
                staffOrganizationId: targetLibraryId,
                authorizationOrganizationId: targetLibraryId);
            seededOutboxes.Add(unchanged);
            var unchangedSender = new EmailRecipientAuthorityBarrierSender(
                pauseReadiness: true,
                pauseSend: false,
                providerMessageId: $"authority-unchanged-{Guid.NewGuid():N}");
            senders.Add(unchangedSender);
            var unchangedJobs = CreateEmailOutboxJobs(unchangedSender, EmailOutboxRuntimeOptions.Default);
            var unchangedDelivery = unchangedJobs.DeliverAsync(unchanged.OutboxId, CancellationToken.None);
            try
            {
                await unchangedSender.ReadinessEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var claimed = await ReadEmailOutcomeDispatchSnapshotAsync(unchanged.OutboxId);
                Assert.AreEqual("pre_send_check_pending", claimed.LastErrorCode);
                unchangedSender.ReleaseReadiness();
                await unchangedDelivery;
            }
            finally
            {
                unchangedSender.ReleaseAll();
                await unchangedDelivery;
            }

            var unchangedSent = await ReadEmailOutcomeDispatchSnapshotAsync(unchanged.OutboxId);
            Assert.AreEqual("sent", unchangedSent.Status);
            Assert.AreEqual(unchangedSender.ProviderMessageId, unchangedSent.ProviderMessageId);
            Assert.AreEqual(1, unchangedSender.SendCount);
            Assert.AreEqual(1, await CountEmailOutboxBusinessKeyAsync(unchangedSent.BusinessKey!));
            await unchangedJobs.DeliverAsync(unchanged.OutboxId, CancellationToken.None);
            AssertEmailOutcomeDispatchSnapshotEqual(
                unchangedSent,
                await ReadEmailOutcomeDispatchSnapshotAsync(unchanged.OutboxId));
            Assert.AreEqual(1, unchangedSender.SendCount);

            var unrelatedStaff = await SeedSensitiveOutboxAsync(
                $"authority-event-unrelated-staff-{Guid.NewGuid():N}",
                staffOrganizationId: targetLibraryId,
                authorizationOrganizationId: targetLibraryId);
            seededOutboxes.Add(unrelatedStaff);
            var businessEventId = await SeedEmailRecipientAuthorityBusinessEventAsync(
                targetLibraryId,
                $"authority-business-event-{Guid.NewGuid():N}");
            businessEventIds.Add(businessEventId);
            var eventSender = new EmailRecipientAuthorityBarrierSender(
                pauseReadiness: true,
                pauseSend: false,
                providerMessageId: $"authority-business-event-{Guid.NewGuid():N}");
            senders.Add(eventSender);
            var eventJobs = CreateEmailOutboxJobs(eventSender, EmailOutboxRuntimeOptions.Default);
            var eventBefore = await ReadEmailOutcomeDispatchSnapshotAsync(businessEventId);
            Assert.AreEqual("pending", eventBefore.Status);
            Assert.IsTrue(await EmailRecipientAuthorityBusinessEventHasNoSensitiveRecipientAsync(businessEventId));

            var eventDelivery = eventJobs.DeliverAsync(businessEventId, CancellationToken.None);
            try
            {
                await eventSender.ReadinessEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var claimed = await ReadEmailOutcomeDispatchSnapshotAsync(businessEventId);
                Assert.AreEqual("pre_send_check_pending", claimed.LastErrorCode);
                await ApplyEmailRecipientMutationSqlAsync(
                    "UPDATE [asap].[StaffUser] SET [IsActive] = 0 WHERE [Id] = @staffUserId;",
                    unrelatedStaff.StaffUserId);
                eventSender.ReleaseReadiness();
                await eventDelivery;
            }
            finally
            {
                eventSender.ReleaseAll();
                await eventDelivery;
            }

            var eventSent = await ReadEmailOutcomeDispatchSnapshotAsync(businessEventId);
            Assert.AreEqual("sent", eventSent.Status);
            Assert.AreEqual(eventSender.ProviderMessageId, eventSent.ProviderMessageId);
            Assert.AreEqual(1, eventSender.SendCount);
            Assert.AreEqual(1, await CountEmailOutboxBusinessKeyAsync(eventSent.BusinessKey!));
            Assert.IsTrue(await EmailRecipientAuthorityBusinessEventHasNoSensitiveRecipientAsync(businessEventId));
            await eventJobs.DeliverAsync(businessEventId, CancellationToken.None);
            AssertEmailOutcomeDispatchSnapshotEqual(
                eventSent,
                await ReadEmailOutcomeDispatchSnapshotAsync(businessEventId));
            Assert.AreEqual(1, eventSender.SendCount);

            var acquisitionFirst = await SeedSensitiveOutboxAsync(
                $"authority-acquired-first-{Guid.NewGuid():N}",
                staffOrganizationId: targetLibraryId,
                authorizationOrganizationId: targetLibraryId);
            seededOutboxes.Add(acquisitionFirst);
            var acquisitionSender = new EmailRecipientAuthorityBarrierSender(
                pauseReadiness: false,
                pauseSend: true,
                providerMessageId: $"authority-acquired-first-{Guid.NewGuid():N}");
            senders.Add(acquisitionSender);
            var acquisitionJobs = CreateEmailOutboxJobs(acquisitionSender, EmailOutboxRuntimeOptions.Default);
            var acquisitionDelivery = acquisitionJobs.DeliverAsync(acquisitionFirst.OutboxId, CancellationToken.None);
            try
            {
                await acquisitionSender.SendEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var dispatchStarted = await ReadEmailOutcomeDispatchSnapshotAsync(acquisitionFirst.OutboxId);
                Assert.AreEqual("sending", dispatchStarted.Status);
                Assert.IsNull(dispatchStarted.LastErrorCode, "The provider intent has cleared the safe pre-send marker.");
                Assert.AreEqual(1, dispatchStarted.AttemptCount);
                Assert.IsNotNull(dispatchStarted.SendingStartedUtc);
                Assert.IsNotNull(dispatchStarted.LeaseId);
                Assert.IsNotNull(dispatchStarted.LeaseExpiresUtc);
                Assert.IsTrue(
                    await EmailRecipientAuthorityHasLiveLeaseAsync(acquisitionFirst.OutboxId, dispatchStarted.LeaseId!.Value),
                    "The acquisition-first control must hold a live SQL lease while provider SendAsync is blocked.");

                await ApplyEmailRecipientMutationSqlAsync(
                    "UPDATE [asap].[StaffUser] SET [IsActive] = 0 WHERE [Id] = @staffUserId;",
                    acquisitionFirst.StaffUserId);
                acquisitionSender.ReleaseSend();
                await acquisitionDelivery;
            }
            finally
            {
                acquisitionSender.ReleaseAll();
                await acquisitionDelivery;
            }

            var acquisitionSent = await ReadEmailOutcomeDispatchSnapshotAsync(acquisitionFirst.OutboxId);
            Assert.AreEqual("sent", acquisitionSent.Status);
            Assert.AreEqual(acquisitionSender.ProviderMessageId, acquisitionSent.ProviderMessageId);
            Assert.AreEqual(1, acquisitionSender.SendCount);
            Assert.AreEqual(1, await CountEmailOutboxBusinessKeyAsync(acquisitionSent.BusinessKey!));
            await acquisitionJobs.DeliverAsync(acquisitionFirst.OutboxId, CancellationToken.None);
            AssertEmailOutcomeDispatchSnapshotEqual(
                acquisitionSent,
                await ReadEmailOutcomeDispatchSnapshotAsync(acquisitionFirst.OutboxId));
            Assert.AreEqual(1, acquisitionSender.SendCount);
        }
        finally
        {
            foreach (var sender in senders)
            {
                sender.ReleaseAll();
            }

            foreach (var seeded in seededOutboxes)
            {
                await DeleteEmailOutcomeFixtureAsync(seeded);
            }

            foreach (var businessEventId in businessEventIds)
            {
                await DeleteEmailRecipientAuthorityBusinessEventAsync(businessEventId);
            }

            await DeleteEmailRecipientAuthorityOrganizationsAsync(targetLibraryId, otherLibraryId);
        }
    }

    private static async Task SeedEmailRecipientAuthorityOrganizationsAsync(int targetLibraryId, int otherLibraryId)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            IF EXISTS
            (
                SELECT 1 FROM [asap].[Organization]
                WHERE [Id] IN (@targetLibraryId, @otherLibraryId)
            )
                THROW 51241, 'Email recipient authority fixture organization ID is already in use.', 1;

            INSERT INTO [asap].[Organization]
                ([Id], [DisplayName], [Abbreviation], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
            VALUES
                (@targetLibraryId, N'Email authority target library', N'EATL', 2, 1, 1),
                (@otherLibraryId, N'Email authority other library', N'EAOL', 2, 1, 1);
            """;
        command.Parameters.Add("@targetLibraryId", SqlDbType.Int).Value = targetLibraryId;
        command.Parameters.Add("@otherLibraryId", SqlDbType.Int).Value = otherLibraryId;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DeleteEmailRecipientAuthorityOrganizationsAsync(int targetLibraryId, int otherLibraryId)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "DELETE FROM [asap].[Organization] WHERE [Id] IN (@targetLibraryId, @otherLibraryId);",
            connection);
        command.Parameters.Add("@targetLibraryId", SqlDbType.Int).Value = targetLibraryId;
        command.Parameters.Add("@otherLibraryId", SqlDbType.Int).Value = otherLibraryId;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> SeedEmailRecipientAuthorityBusinessEventAsync(int organizationId, string businessKey)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO [asap].[EmailOutbox]
                ([OrganizationId], [BusinessKey], [DeliveryClass], [ToAddress], [FromAddress], [Subject], [BodyText],
                 [Status], [NextAttemptUtc], [CreatedUtc])
            OUTPUT inserted.[Id]
            VALUES
                (@organizationId, @businessKey, N'business_event', N'event-recipient@example.org',
                 N'asap@example.org', N'Immutable business event', N'Business event body',
                 N'pending', SYSUTCDATETIME(), SYSUTCDATETIME());
            """;
        command.Parameters.Add("@organizationId", SqlDbType.Int).Value = organizationId;
        command.Parameters.Add("@businessKey", SqlDbType.NVarChar, 450).Value = businessKey;
        return Convert.ToInt64(await WithFixtureClock(command).ExecuteScalarAsync());
    }

    private static async Task DeleteEmailRecipientAuthorityBusinessEventAsync(long outboxId)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "DELETE FROM [asap].[EmailOutbox] WHERE [Id] = @id;",
            connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = outboxId;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> EmailRecipientAuthorityBusinessEventHasNoSensitiveRecipientAsync(long outboxId)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT CASE WHEN [DeliveryClass] = N'business_event'
                AND [RecipientStaffUserId] IS NULL
                AND [RecipientAuthenticationEmail] IS NULL
                AND [AuthorizationOrganizationId] IS NULL
                AND [RecipientAddressKind] IS NULL
                THEN 1 ELSE 0 END
            FROM [asap].[EmailOutbox]
            WHERE [Id] = @id;
            """,
            connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = outboxId;
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }

    private static async Task<int> CountEmailOutboxBusinessKeyAsync(string businessKey)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM [asap].[EmailOutbox] WHERE [BusinessKey] = @businessKey;",
            connection);
        command.Parameters.Add("@businessKey", SqlDbType.NVarChar, 450).Value = businessKey;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> EmailRecipientAuthorityHasLiveLeaseAsync(long outboxId, Guid leaseId)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT CASE WHEN [Status] = N'sending' AND [LeaseId] = @leaseId " +
            "AND [LeaseExpiresUtc] > SYSUTCDATETIME() THEN 1 ELSE 0 END " +
            "FROM [asap].[EmailOutbox] WHERE [Id] = @id;",
            connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = outboxId;
        command.Parameters.Add("@leaseId", SqlDbType.UniqueIdentifier).Value = leaseId;
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }

    private static async Task<EmailRecipientAuthorityIdentitySnapshot> ReadEmailRecipientAuthorityIdentityAsync(long outboxId)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT [OrganizationId], [BusinessKey], [DeliveryClass], [RecipientStaffUserId], " +
            "[RecipientAuthenticationEmail], [AuthorizationOrganizationId], [RecipientAddressKind], [ToAddress] " +
            "FROM [asap].[EmailOutbox] WHERE [Id] = @id;",
            connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = outboxId;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        return new EmailRecipientAuthorityIdentitySnapshot(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    private static async Task ApplyEmailRecipientAuthorityMutationAsync(
        EmailRecipientAuthorityMutation mutation,
        SeededSensitiveOutbox seeded,
        int targetLibraryId,
        int otherLibraryId)
    {
        if (mutation.AffectsOrganization)
        {
            var sql = mutation.Kind switch
            {
                EmailRecipientMutationKind.DeactivateAuthorizationLibrary =>
                    "UPDATE [asap].[Organization] SET [IsActive] = 0 WHERE [Id] = @organizationId;",
                EmailRecipientMutationKind.ReclassifyAuthorizationOrganizationAsBranch =>
                    "UPDATE [asap].[Organization] SET [OrganizationCodeId] = 3 WHERE [Id] = @organizationId;",
                _ => throw new InvalidOperationException($"Unsupported organization mutation {mutation.Kind}.")
            };
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@organizationId", SqlDbType.Int).Value = targetLibraryId;
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync(), mutation.Name);
            return;
        }

        var staffSql = mutation.Kind switch
        {
            EmailRecipientMutationKind.DeactivateStaff =>
                "UPDATE [asap].[StaffUser] SET [IsActive] = 0 WHERE [Id] = @staffUserId;",
            EmailRecipientMutationKind.MoveStaffOwner =>
                "UPDATE [asap].[StaffUser] SET [OrganizationId] = @otherLibraryId WHERE [Id] = @staffUserId;",
            EmailRecipientMutationKind.RemoveSuperAdminScope =>
                "UPDATE [asap].[StaffUser] SET [Role] = N'admin', [OrganizationId] = @otherLibraryId WHERE [Id] = @staffUserId;",
            EmailRecipientMutationKind.ChangeAuthenticationEmail =>
                "UPDATE [asap].[StaffUser] SET [UserPrincipalName] = CONCAT(N'auth-changed-', @staffUserId, N'@example.org'), [NormalizedUserPrincipalName] = UPPER(CONCAT(N'auth-changed-', @staffUserId, N'@example.org')) WHERE [Id] = @staffUserId;",
            EmailRecipientMutationKind.ChangeNotificationEmail =>
                "UPDATE [asap].[StaffUser] SET [NotificationEmail] = CONCAT(N'notify-changed-', @staffUserId, N'@example.org') WHERE [Id] = @staffUserId;",
            EmailRecipientMutationKind.ChangeWeeklySummaryEmail =>
                "UPDATE [asap].[StaffUser] SET [WeeklyActionSummaryEmail] = CONCAT(N'weekly-changed-', @staffUserId, N'@example.org') WHERE [Id] = @staffUserId;",
            EmailRecipientMutationKind.DisableWeeklySummary =>
                "UPDATE [asap].[StaffUser] SET [WeeklyActionSummaryEnabled] = 0 WHERE [Id] = @staffUserId;",
            _ => throw new InvalidOperationException($"Unsupported staff mutation {mutation.Kind}.")
        };
        await ApplyEmailRecipientMutationSqlAsync(staffSql, seeded.StaffUserId, otherLibraryId);
    }

    private static async Task ApplyEmailRecipientMutationSqlAsync(string sql, long staffUserId, int? otherLibraryId = null)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 5;
        command.Parameters.Add("@staffUserId", SqlDbType.BigInt).Value = staffUserId;
        if (sql.Contains("@otherLibraryId", StringComparison.Ordinal))
        {
            command.Parameters.Add("@otherLibraryId", SqlDbType.Int).Value = otherLibraryId
                ?? throw new InvalidOperationException("An owner move requires a target library ID.");
        }
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync(), sql);
    }

    private static async Task RestoreEmailRecipientAuthorityOrganizationAsync(int organizationId)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "UPDATE [asap].[Organization] SET [IsActive] = 1, [OrganizationCodeId] = 2 WHERE [Id] = @organizationId;",
            connection);
        command.Parameters.Add("@organizationId", SqlDbType.Int).Value = organizationId;
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync());
    }

    private sealed record EmailRecipientAuthorityMutation(
        string Name,
        EmailRecipientMutationKind Kind,
        string Role = "staff",
        int? StaffOrganizationId = null,
        string AddressKind = "notification_email",
        bool WeeklyEnabled = false,
        string? WeeklyEmail = null,
        bool AffectsOrganization = false);

    private sealed record EmailRecipientAuthorityIdentitySnapshot(
        int OrganizationId,
        string? BusinessKey,
        string DeliveryClass,
        long? RecipientStaffUserId,
        string? RecipientAuthenticationEmail,
        int? AuthorizationOrganizationId,
        string? RecipientAddressKind,
        string? ToAddress);

    private enum EmailRecipientMutationKind
    {
        DeactivateStaff,
        MoveStaffOwner,
        RemoveSuperAdminScope,
        ChangeAuthenticationEmail,
        ChangeNotificationEmail,
        DeactivateAuthorizationLibrary,
        ReclassifyAuthorizationOrganizationAsBranch,
        ChangeWeeklySummaryEmail,
        DisableWeeklySummary
    }

    private sealed class EmailRecipientAuthorityBarrierSender(
        bool pauseReadiness,
        bool pauseSend,
        string providerMessageId) : IEmailSender
    {
        private readonly TaskCompletionSource<EmailTransportReadiness> readinessCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<EmailSendResult> sendCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int sendCount;

        public TaskCompletionSource ReadinessEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SendEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SendCount => Volatile.Read(ref sendCount);
        public string ProviderMessageId => providerMessageId;

        public Task<EmailTransportReadiness> CheckReadinessAsync(
            int organizationId,
            CancellationToken cancellationToken)
        {
            if (!pauseReadiness)
            {
                return Task.FromResult(EmailTransportReadiness.Configured);
            }

            ReadinessEntered.TrySetResult();
            return readinessCompletion.Task.WaitAsync(cancellationToken);
        }

        public Task<EmailSendResult> SendAsync(EmailEnvelope envelope, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref sendCount);
            SendEntered.TrySetResult();
            if (pauseSend)
            {
                return sendCompletion.Task.WaitAsync(cancellationToken);
            }

            return Task.FromResult(new EmailSendResult(providerMessageId));
        }

        public void ReleaseReadiness() => readinessCompletion.TrySetResult(EmailTransportReadiness.Configured);

        public void ReleaseSend() => sendCompletion.TrySetResult(new EmailSendResult(providerMessageId));

        public void ReleaseAll()
        {
            ReleaseReadiness();
            ReleaseSend();
        }
    }
}
