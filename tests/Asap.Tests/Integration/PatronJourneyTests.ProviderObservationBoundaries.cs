using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow("home", false)]
    [DataRow("code", false)]
    [DataRow("native", false)]
    [DataRow("home", true)]
    [DataRow("code", true)]
    [DataRow("native", true)]
    public async Task ProviderRegistrationChangedDuringReadinessIsRejectedAtFinalAcceptance(
        string change, bool needsPickupWrite)
    {
        // Characterize the current observation boundary, including its non-atomic
        // remote gap. A deterministic provider does not establish deployed PAPI
        // barcode reassignment semantics or conditional-write support.
        var provider = new PickupJournalProvider(36580);
        var originalPatronId = provider.PatronId;
        var sender = new EmailRecipientAuthorityBarrierSender(true, false, "observation-boundary");
        await using var scoped = CreatePickupJournalFactory(provider).WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(sender);
            }));
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await SeedPickupJournalRequestAsync(contexts, provider);
        var session = await IssueTestPatronSessionAsync(provider.Barcode, provider.OrganizationId, originalPatronId);
        var beforeOutbox = await OutboxCountAsync(contexts, provider.OrganizationId);
        var title = "Provider observation " + Guid.NewGuid().ToString("N");
        Task<PatronSuggestionResult>? submission = null;
        try
        {
            await ExecuteNonQueryAsync("""
                INSERT INTO [asap].[WorkflowSettings]
                    ([OrganizationId], [PatronCodeEligibilityEnabled], [AllowAnyRegisteredCardLogin], [UpdatedUtc])
                VALUES (@org, 1, 0, SYSUTCDATETIME());
                INSERT INTO [asap].[PatronCodeEligibilitySet] ([OrganizationId]) VALUES (@org);
                INSERT INTO [asap].[PatronCodeEligibilityMember] ([OrganizationId], [PatronCodeId]) VALUES (@org, 1);
                """, ("@org", provider.OrganizationId));
            submission = scoped.Services.GetRequiredService<PatronSuggestionService>().CreateAsync(
                session,
                Suggestion(title) with
                {
                    PreferredPickupBranchId = needsPickupWrite ? provider.SecondBranch : provider.FirstBranch
                }, CancellationToken.None);
            await sender.ReadinessEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, provider.Reads);
            switch (change)
            {
                case "home":
                    provider.HomeLibraryOrganizationId = 1;
                    break;
                case "code":
                    provider.PatronCodeId = 999;
                    break;
                case "native":
                    provider.PatronId = originalPatronId + 1;
                    break;
                default:
                    Assert.Fail("Unknown provider observation change.");
                    break;
            }
            sender.ReleaseReadiness();
            var failure = await Assert.ThrowsAsync<PatronFlowException>(() => submission);
            Assert.AreEqual(needsPickupWrite ? 409 : change == "native" ? 401 : 403, failure.StatusCode);
            Assert.AreEqual(2, provider.Reads, "Final acceptance must use a new provider observation.");
            Assert.AreEqual(needsPickupWrite ? 1 : 0, provider.Writes);
            Assert.AreEqual(needsPickupWrite ? 1 : 0,
                await PickupJournalCountAsync(provider.Barcode, state: null));
            if (needsPickupWrite)
            {
                var partial = failure.Response as PatronSuggestionPickupChangedFailure;
                Assert.IsNotNull(partial);
                Assert.IsTrue(partial.PickupPreferenceChanged);
                Assert.AreEqual(1, await PickupJournalCountAsync(provider.Barcode, state: 2));
                await using var verifyConnection = new SqlConnection(databaseConnectionString);
                await verifyConnection.OpenAsync();
                await using var operationRead = new SqlCommand("""
                    SELECT [PatronId], [CompletedUtc] FROM [asap].[PickupPreferenceOperation]
                    WHERE [LibraryOrganizationId] = @org;
                    """, verifyConnection);
                operationRead.Parameters.AddWithValue("@org", provider.OrganizationId);
                await using var operation = await operationRead.ExecuteReaderAsync();
                Assert.IsTrue(await operation.ReadAsync());
                Assert.AreEqual(originalPatronId, operation.GetInt32(0),
                    "The journal preserves the identity actually observed at acquisition.");
                Assert.IsTrue(operation.IsDBNull(1));
            }
            await AssertNoSuggestionSideEffectsAsync(contexts, provider, title, beforeOutbox, 1);
        }
        finally
        {
            sender.ReleaseAll();
            if (submission is not null)
            {
                try
                {
                    await submission;
                }
                catch (PatronFlowException)
                {
                }
            }
            await DeleteTestPatronSessionAsync(session.Id);
            await CleanupPickupPolicyAsync(provider.OrganizationId);
            await CleanupPickupJournalLibraryAsync(provider.OrganizationId);
        }
    }
}
