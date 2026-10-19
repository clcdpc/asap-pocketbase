using System.Data;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow("session")]
    [DataRow("policy")]
    [DataRow("authority")]
    public async Task PublicPickupIntentRechecksCurrentSessionAndPolicyBeforeJournal(string change)
    {
        var provider = new PickupJournalProvider(change switch
        {
            "session" => 34820,
            "policy" => 34821,
            _ => 34822
        });
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await SeedPickupJournalRequestAsync(contexts, provider);
        var session = await IssueTestPatronSessionAsync(provider.Barcode, provider.OrganizationId, provider.OrganizationId * 10);
        var beforeOutbox = await OutboxCountAsync(contexts, provider.OrganizationId);
        var mutation = scoped.Services.GetRequiredService<PickupPreferenceMutationService>();
        var intentStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseIntent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mutation.BeforeIntentValidationForTesting = async token =>
        {
            intentStarted.TrySetResult();
            await releaseIntent.Task.WaitAsync(token);
        };
        var title = $"Public pickup race {Guid.NewGuid():N}";
        var submission = scoped.Services.GetRequiredService<PatronSuggestionService>().CreateAsync(
            session,
            Suggestion(title) with { PreferredPickupBranchId = provider.SecondBranch },
            CancellationToken.None);
        try
        {
            await intentStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (change == "session")
            {
                await ExecuteNonQueryAsync(
                    "UPDATE [asap].[PatronSession] SET [RevokedUtc]=SYSUTCDATETIME() WHERE [Id]=@id;",
                    ("@id", session.Id));
            }
            else if (change == "policy")
            {
                await EnableIneligiblePatronCodePolicyAsync(provider.OrganizationId);
            }
            else
            {
                await ExecuteNonQueryAsync(
                    "UPDATE [asap].[Organization] SET [OrganizationCodeId]=3 WHERE [Id]=@id;",
                    ("@id", provider.OrganizationId));
            }

            releaseIntent.TrySetResult();
            var failure = await Assert.ThrowsAsync<PatronFlowException>(() => submission);
            Assert.AreEqual(change == "session" ? 401 : 403, failure.StatusCode);
            if (change == "authority")
            {
                Assert.AreEqual(1, await CountCurrentSessionAsync(session.Id),
                    "An authority reclassification must not delete the durable patron session as a side effect.");
            }
            Assert.AreEqual(0, provider.Writes, "A rejected new intent must not dispatch a provider write.");
            Assert.AreEqual(0, await PickupJournalCountAsync(provider.Barcode, state: null));
            await AssertNoSuggestionSideEffectsAsync(contexts, provider, title, beforeOutbox, expectedRequestCount: 1);
        }
        finally
        {
            releaseIntent.TrySetResult();
            mutation.BeforeIntentValidationForTesting = null;
            try
            {
                await submission;
            }
            catch (Exception)
            {
            }
            await DeleteTestPatronSessionAsync(session.Id);
            await CleanupPickupPolicyAsync(provider.OrganizationId);
            await CleanupPickupJournalLibraryAsync(provider.OrganizationId);
        }
    }

    [TestMethod]
    [DataRow("identity")]
    [DataRow("participation")]
    public async Task StaffSuggestionPickupIntentRechecksActorAndLibraryParticipation(string change)
    {
        var provider = new PickupJournalProvider(change == "identity" ? 34920 : 34921);
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await SeedPickupJournalRequestAsync(contexts, provider);
        var actor = await ReadConfiguredSuperAdminAsync();
        var beforeOutbox = await OutboxCountAsync(contexts, provider.OrganizationId);
        var mutation = scoped.Services.GetRequiredService<PickupPreferenceMutationService>();
        var intentStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseIntent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mutation.BeforeIntentValidationForTesting = async token =>
        {
            intentStarted.TrySetResult();
            await releaseIntent.Task.WaitAsync(token);
        };
        var title = $"Staff pickup race {Guid.NewGuid():N}";
        var input = new StaffSuggestionInput(
            provider.OrganizationId,
            provider.Barcode,
            "book",
            title,
            "Test Author",
            null,
            "Coming soon",
            null,
            null,
            provider.SecondBranch,
            provider.FirstBranch,
            true,
            false,
            new Dictionary<string, string?>(),
            CurrentPreferredPickupBranchObservedAtLoad: true);
        var submission = scoped.Services.GetRequiredService<StaffSuggestionService>()
            .CreateAsync(actor, input, CancellationToken.None);
        try
        {
            var boundary = await Task.WhenAny(intentStarted.Task, submission);
            if (boundary == submission)
            {
                await submission;
                Assert.Fail("Staff suggestion completed before reaching the pickup intent barrier.");
            }
            await intentStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (change == "identity")
            {
                await ExecuteNonQueryAsync(
                    "UPDATE [asap].[StaffUser] SET [Role]=N'staff', [OrganizationId]=2 WHERE [Id]=@id;",
                    ("@id", actor.Id));
            }
            else
            {
                await ExecuteNonQueryAsync(
                    "UPDATE [asap].[Organization] SET [IsActive]=0 WHERE [Id]=@id;",
                    ("@id", provider.OrganizationId));
            }

            releaseIntent.TrySetResult();
            var failure = await Assert.ThrowsAsync<StaffSuggestionException>(() => submission);
            Assert.AreEqual(change == "identity" ? "staff_session_invalid" : "organization_inactive", failure.Code);
            Assert.AreEqual(0, provider.Writes, "A rejected staff intent must not dispatch a provider write.");
            Assert.AreEqual(0, await PickupJournalCountAsync(provider.Barcode, state: null));
            await AssertNoSuggestionSideEffectsAsync(contexts, provider, title, beforeOutbox, expectedRequestCount: 1);
        }
        finally
        {
            releaseIntent.TrySetResult();
            mutation.BeforeIntentValidationForTesting = null;
            try
            {
                await submission;
            }
            catch (Exception)
            {
            }
            await ExecuteNonQueryAsync(
                "UPDATE [asap].[StaffUser] SET [Role]=@role, [OrganizationId]=@staffOrganization, [IsActive]=1 WHERE [Id]=@id; UPDATE [asap].[Organization] SET [IsActive]=1 WHERE [Id]=@org;",
                ("@role", actor.Role), ("@staffOrganization", actor.OrganizationId),
                ("@id", actor.Id), ("@org", provider.OrganizationId));
            await CleanupPickupJournalLibraryAsync(provider.OrganizationId);
        }
    }

    [TestMethod]
    public async Task RequestPickupIntentRechecksRowVersionAndHoldAcquisitionDoesNotWaitOnPatronLock()
    {
        var provider = new PickupJournalProvider(35020);
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var request = await SeedPickupJournalRequestAsync(contexts, provider);
        var actor = await ReadConfiguredSuperAdminAsync();
        var beforeOutbox = await OutboxCountAsync(contexts, provider.OrganizationId);
        var mutation = scoped.Services.GetRequiredService<PickupPreferenceMutationService>();
        var intentStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseIntent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mutation.BeforeIntentValidationForTesting = async token =>
        {
            intentStarted.TrySetResult();
            await releaseIntent.Task.WaitAsync(token);
        };
        var expectedVersion = StaffVersion.Encode(request.RowVersion);
        var pickup = scoped.Services.GetRequiredService<StaffPickupService>();
        var update = pickup.UpdateAsync(actor, request.Id,
            new PickupPreferenceInput(expectedVersion, provider.SecondBranch, provider.FirstBranch, true),
            CancellationToken.None);
        try
        {
            await intentStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var hold = await scoped.Services.GetRequiredService<HoldPlacementService>()
                .PlaceAsync(actor, request.Id, new VersionInput(expectedVersion), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual("hold_operation_incomplete", hold.Code,
                "Hold acquisition must fail its transaction-scoped try app-lock while pickup owns it.");
            await using (var write = new SqlConnection(databaseConnectionString))
            {
                await write.OpenAsync();
                await using var command = new SqlCommand(
                    "UPDATE [asap].[TitleRequest] SET [Notes]=N'changed during pickup preflight' WHERE [Id]=@id;", write);
                command.Parameters.AddWithValue("@id", request.Id);
                await command.ExecuteNonQueryAsync();
            }

            releaseIntent.TrySetResult();
            var result = await update;
            Assert.AreEqual("stale_version", result.Code);
            Assert.AreEqual(0, provider.Writes);
            Assert.AreEqual(0, await PickupJournalCountAsync(provider.Barcode, state: null));
            await AssertNoSuggestionSideEffectsAsync(contexts, provider, null, beforeOutbox,
                expectedRequestCount: 1, expectedEvents: 0);
            await using var verify = await contexts.CreateDbContextAsync();
            Assert.AreEqual(0, await verify.HoldPlacementOperations.CountAsync(item => item.TitleRequestId == request.Id));
        }
        finally
        {
            releaseIntent.TrySetResult();
            mutation.BeforeIntentValidationForTesting = null;
            try
            {
                await update;
            }
            catch (Exception) when (releaseIntent.Task.IsCompleted)
            {
            }
            await CleanupPickupJournalLibraryAsync(provider.OrganizationId);
        }
    }

    [TestMethod]
    [DataRow("revocation")]
    [DataRow("scope")]
    [DataRow("participation")]
    public async Task StaffPickupIntentRechecksCurrentActorAndLibraryParticipation(string change)
    {
        var provider = new PickupJournalProvider(change switch
        {
            "revocation" => 35120,
            "scope" => 35121,
            _ => 35122
        });
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var request = await SeedPickupJournalRequestAsync(contexts, provider);
        var actor = await ReadConfiguredSuperAdminAsync();
        var beforeOutbox = await OutboxCountAsync(contexts, provider.OrganizationId);
        var mutation = scoped.Services.GetRequiredService<PickupPreferenceMutationService>();
        var intentStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseIntent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mutation.BeforeIntentValidationForTesting = async token =>
        {
            intentStarted.TrySetResult();
            await releaseIntent.Task.WaitAsync(token);
        };
        var expectedVersion = StaffVersion.Encode(request.RowVersion);
        var update = scoped.Services.GetRequiredService<StaffPickupService>().UpdateAsync(
            actor,
            request.Id,
            new PickupPreferenceInput(expectedVersion, provider.SecondBranch, provider.FirstBranch, true),
            CancellationToken.None);
        try
        {
            await intentStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (change == "revocation")
            {
                await ExecuteNonQueryAsync(
                    "UPDATE [asap].[StaffUser] SET [IsActive]=0 WHERE [Id]=@id;",
                    ("@id", actor.Id));
            }
            else if (change == "scope")
            {
                await ExecuteNonQueryAsync(
                    "UPDATE [asap].[StaffUser] SET [Role]=N'staff', [OrganizationId]=2 WHERE [Id]=@id;",
                    ("@id", actor.Id));
            }
            else
            {
                await ExecuteNonQueryAsync(
                    "UPDATE [asap].[Organization] SET [IsActive]=0 WHERE [Id]=@id;",
                    ("@id", provider.OrganizationId));
            }

            releaseIntent.TrySetResult();
            var result = await update;
            Assert.AreEqual(change == "participation" ? "organization_inactive" : "staff_scope_forbidden", result.Code);
            Assert.AreEqual(0, provider.Writes, "A rejected staff pickup intent must not dispatch a provider write.");
            Assert.AreEqual(0, await PickupJournalCountAsync(provider.Barcode, state: null));
            await AssertNoSuggestionSideEffectsAsync(contexts, provider, null, beforeOutbox,
                expectedRequestCount: 1, expectedEvents: 0);
        }
        finally
        {
            releaseIntent.TrySetResult();
            mutation.BeforeIntentValidationForTesting = null;
            try
            {
                await update;
            }
            catch (Exception)
            {
            }
            await ExecuteNonQueryAsync(
                "UPDATE [asap].[StaffUser] SET [Role]=@role, [OrganizationId]=@staffOrganization, [IsActive]=1 WHERE [Id]=@id; UPDATE [asap].[Organization] SET [IsActive]=1 WHERE [Id]=@org;",
                ("@role", actor.Role), ("@staffOrganization", actor.OrganizationId),
                ("@id", actor.Id), ("@org", provider.OrganizationId));
            await CleanupPickupJournalLibraryAsync(provider.OrganizationId);
        }
    }

    [TestMethod]
    public async Task PatronAppLockTryAcquireIsNonblockingWhenAnotherCallerAcquiredItFirst()
    {
        var provider = new PickupJournalProvider(35220);
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var request = await SeedPickupJournalRequestAsync(contexts, provider);
        try
        {
            var patron = await provider.RefreshAsync(provider.Barcode, provider.OrganizationId, CancellationToken.None);
            await using var lockConnection = new SqlConnection(databaseConnectionString);
            await lockConnection.OpenAsync();
            await using var lockTransaction = (SqlTransaction)await lockConnection.BeginTransactionAsync();
            Assert.IsTrue(await PatronMutationLock.TryAcquireAsync(
                lockConnection, lockTransaction, provider.Barcode, CancellationToken.None));

            var blocked = await Assert.ThrowsExactlyAsync<PickupMutationBlockedException>(() =>
                scoped.Services.GetRequiredService<PickupPreferenceMutationService>().ChangeAsync(
                    patron,
                    provider.OrganizationId,
                    new PickupBranch(provider.SecondBranch, "Second"),
                    "First",
                    "request",
                    request.Id,
                    null,
                    static (_, _, _) => Task.CompletedTask,
                    CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.AreEqual("hold_operation_incomplete", blocked.Code);
            Assert.AreEqual(0, provider.Writes);
            Assert.AreEqual(0, await PickupJournalCountAsync(provider.Barcode, state: null));
            await lockTransaction.RollbackAsync();
        }
        finally
        {
            await CleanupPickupJournalLibraryAsync(provider.OrganizationId);
        }
    }

    [TestMethod]
    public async Task PublicFinalAcceptanceRechecksPolicyWhenPickupAlreadyMatchesAndNoPutIsNeeded()
    {
        var provider = new PickupJournalProvider(35320);
        var sender = new PolicyMutatingReadinessEmailSender(provider.OrganizationId);
        await using var scoped = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.AddSingleton<IPatronProvider>(provider);
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(sender);
        }));
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await SeedPickupJournalRequestAsync(contexts, provider);
        var session = await IssueTestPatronSessionAsync(provider.Barcode, provider.OrganizationId, provider.OrganizationId * 10);
        var beforeOutbox = await OutboxCountAsync(contexts, provider.OrganizationId);
        var title = $"Public no-PUT policy {Guid.NewGuid():N}";
        try
        {
            var failure = await Assert.ThrowsAsync<PatronFlowException>(() =>
                scoped.Services.GetRequiredService<PatronSuggestionService>().CreateAsync(
                    session,
                    Suggestion(title) with { PreferredPickupBranchId = provider.FirstBranch },
                    CancellationToken.None));
            Assert.AreEqual(403, failure.StatusCode);
            Assert.AreEqual(1, sender.ReadinessCalls,
                "The policy change occurs after preflight but before final SQL acceptance.");
            Assert.AreEqual(0, provider.Writes, "A matching pickup preference must not produce a PUT.");
            Assert.AreEqual(0, await PickupJournalCountAsync(provider.Barcode, state: null));
            await AssertNoSuggestionSideEffectsAsync(contexts, provider, title, beforeOutbox,
                expectedRequestCount: 1);
        }
        finally
        {
            await DeleteTestPatronSessionAsync(session.Id);
            await CleanupPickupPolicyAsync(provider.OrganizationId);
            await CleanupPickupJournalLibraryAsync(provider.OrganizationId);
        }
    }

    private static async Task EnableIneligiblePatronCodePolicyAsync(int organizationId)
    {
        await ExecuteNonQueryAsync("""
            IF EXISTS (SELECT 1 FROM [asap].[WorkflowSettings] WHERE [OrganizationId]=@org)
                UPDATE [asap].[WorkflowSettings]
                SET [PatronCodeEligibilityEnabled]=1, [UpdatedUtc]=SYSUTCDATETIME()
                WHERE [OrganizationId]=@org;
            ELSE
                INSERT INTO [asap].[WorkflowSettings] ([OrganizationId],[PatronCodeEligibilityEnabled],[UpdatedUtc])
                VALUES (@org,1,SYSUTCDATETIME());
            IF NOT EXISTS (SELECT 1 FROM [asap].[PatronCodeEligibilitySet] WHERE [OrganizationId]=@org)
                INSERT INTO [asap].[PatronCodeEligibilitySet] ([OrganizationId]) VALUES (@org);
            IF NOT EXISTS (SELECT 1 FROM [asap].[PatronCodeEligibilityMember]
                           WHERE [OrganizationId]=@org AND [PatronCodeId]=999)
                INSERT INTO [asap].[PatronCodeEligibilityMember] ([OrganizationId],[PatronCodeId])
                VALUES (@org,999);
            """, ("@org", organizationId));
    }

    private static Task CleanupPickupPolicyAsync(int organizationId) => ExecuteNonQueryAsync("""
        DELETE FROM [asap].[PatronCodeEligibilitySet] WHERE [OrganizationId]=@org;
        DELETE FROM [asap].[WorkflowSettings] WHERE [OrganizationId]=@org;
        """, ("@org", organizationId));

    private static async Task<int> CountCurrentSessionAsync(long sessionId)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM [asap].[PatronSession] WHERE [Id]=@id AND [RevokedUtc] IS NULL;",
            connection);
        command.Parameters.AddWithValue("@id", sessionId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<int> OutboxCountAsync(IDbContextFactory<AsapDbContext> contexts, int organizationId)
    {
        await using var context = await contexts.CreateDbContextAsync();
        return await context.EmailOutbox.CountAsync(item => item.OrganizationId == organizationId);
    }

    private static async Task AssertNoSuggestionSideEffectsAsync(
        IDbContextFactory<AsapDbContext> contexts,
        PickupJournalProvider provider,
        string? title,
        int beforeOutbox,
        int expectedRequestCount,
        int expectedEvents = 0)
    {
        await using var context = await contexts.CreateDbContextAsync();
        Assert.AreEqual(expectedRequestCount,
            await context.TitleRequests.CountAsync(item => item.Barcode == provider.Barcode));
        Assert.AreEqual(expectedEvents,
            await context.TitleRequestEvents.CountAsync(item =>
                context.TitleRequests.Any(request => request.Id == item.TitleRequestId && request.Barcode == provider.Barcode)));
        Assert.AreEqual(beforeOutbox, await context.EmailOutbox.CountAsync(item => item.OrganizationId == provider.OrganizationId));
        if (title is not null)
        {
            Assert.AreEqual(0, await context.TitleRequests.CountAsync(item => item.Barcode == provider.Barcode && item.Title == title));
        }
    }

    private sealed class PolicyMutatingReadinessEmailSender(int organizationId) : IEmailSender
    {
        public int ReadinessCalls { get; private set; }

        public async Task<EmailTransportReadiness> CheckReadinessAsync(
            int requestedOrganizationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual(organizationId, requestedOrganizationId);
            ReadinessCalls++;
            await EnableIneligiblePatronCodePolicyAsync(organizationId);
            return EmailTransportReadiness.Configured;
        }

        public Task<EmailSendResult> SendAsync(EmailEnvelope envelope, CancellationToken cancellationToken) =>
            Task.FromResult(new EmailSendResult("test-message"));
    }
}
