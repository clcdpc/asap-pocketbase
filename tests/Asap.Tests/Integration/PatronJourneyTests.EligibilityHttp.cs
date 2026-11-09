using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow("initial")]
    [DataRow("before-intent")]
    [DataRow("final")]
    [DataRow("fresh-receipt")]
    [DataRow("recovered-receipt")]
    public async Task PublicEligibilityHttpReportsCurrentExplanationAndRetainsPickupTruth(string boundary)
    {
        var provider = new PickupJournalProvider(38210);
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await SeedNativeIdentityPickupLibraryAsync(contexts, provider);
        var issued = await IssueNativePatronSessionAsync(scoped.Services, provider.Barcode,
            provider.PatronId, provider.OrganizationId);
        using var client = scoped.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.Token);
        var before = await ReadNativeIdentitySideEffectsAsync(contexts, provider.OrganizationId);
        var title = "Eligibility HTTP " + Guid.NewGuid().ToString("N");
        const string explanation = "Contact your library about <eligibility> & access.";
        var mutation = scoped.Services.GetRequiredService<PickupPreferenceMutationService>();
        async Task ChangePolicyAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            await EnableIneligiblePatronCodePolicyAsync(provider.OrganizationId);
            await ExecuteNonQueryAsync("""
                UPDATE [asap].[WorkflowSettings] SET [PatronCodeEligibilityMessage]=@message
                WHERE [OrganizationId]=@org;
                """, ("@message", explanation), ("@org", provider.OrganizationId));
        }
        Guid? recoveredOperationId = null;
        try
        {
            if (boundary == "recovered-receipt")
            {
                provider.AfterEffect = _ => throw new PolarisOperationalException(
                    "testing_uncertain_pickup", "Connection lost after effect.");
                using var uncertain = await PostPatronSuggestionAsync(client, title, provider.SecondBranch);
                Assert.AreEqual(HttpStatusCode.Conflict, uncertain.StatusCode, await uncertain.Content.ReadAsStringAsync());
                using var uncertainBody = JsonDocument.Parse(await uncertain.Content.ReadAsStringAsync());
                recoveredOperationId = uncertainBody.RootElement.GetProperty("operationId").GetGuid();
                Assert.AreEqual(1, provider.Writes);
                Assert.AreEqual(1, await PickupJournalCountAsync(provider.Barcode, state: 1));
                provider.AfterEffect = null;
            }
            if (boundary == "initial")
            {
                await ChangePolicyAsync(CancellationToken.None);
            }
            else if (boundary == "before-intent")
            {
                mutation.BeforeIntentValidationForTesting = ChangePolicyAsync;
            }
            else if (boundary == "fresh-receipt")
            {
                provider.AfterEffect = ChangePolicyAsync;
            }
            else
            {
                var finalRead = provider.Reads + 2;
                provider.AfterRefresh = token => provider.Reads == finalRead
                    ? ChangePolicyAsync(token) : Task.CompletedTask;
            }
            using var rejection = await PostPatronSuggestionAsync(client, title,
                boundary == "final" ? provider.FirstBranch : provider.SecondBranch);
            var raw = await rejection.Content.ReadAsStringAsync();
            using var body = JsonDocument.Parse(raw);
            var receipt = boundary.EndsWith("receipt", StringComparison.Ordinal);
            Assert.AreEqual(receipt ? HttpStatusCode.Conflict : HttpStatusCode.Forbidden, rejection.StatusCode, raw);
            Assert.AreEqual(receipt ? "request_not_created_pickup_changed" : "patron_ineligible",
                body.RootElement.GetProperty("code").GetString());
            if (receipt)
            {
                StringAssert.Contains(body.RootElement.GetProperty("message").GetString()!, explanation);
                Assert.IsTrue(body.RootElement.GetProperty("pickupPreferenceChanged").GetBoolean());
                var operationId = body.RootElement.GetProperty("operationId").GetGuid();
                Assert.AreNotEqual(Guid.Empty, operationId);
                if (recoveredOperationId.HasValue)
                {
                    Assert.AreEqual(recoveredOperationId.Value, operationId);
                }
                Assert.AreEqual(1, provider.Writes, "Recovery must not repeat the accepted pickup PUT.");
                Assert.AreEqual(provider.SecondBranch, provider.Current);
                await using var connection = new SqlConnection(databaseConnectionString);
                await connection.OpenAsync();
                await using var command = new SqlCommand("""
                    SELECT [State], [ConfirmedByRead], [TitleRequestId], [CompletedUtc]
                    FROM [asap].[PickupPreferenceOperation] WHERE [Id]=@id;
                    """, connection);
                command.Parameters.AddWithValue("@id", operationId);
                await using var rows = await command.ExecuteReaderAsync();
                Assert.IsTrue(await rows.ReadAsync());
                Assert.AreEqual(2, rows.GetInt32(0));
                Assert.AreEqual(recoveredOperationId.HasValue, rows.GetBoolean(1));
                Assert.IsTrue(rows.IsDBNull(2));
                Assert.IsTrue(rows.IsDBNull(3));
                Assert.IsFalse(await rows.ReadAsync());
            }
            else
            {
                Assert.AreEqual(explanation, body.RootElement.GetProperty("message").GetString());
                Assert.AreEqual(0, provider.Writes);
                Assert.AreEqual(0, await PickupJournalCountAsync(provider.Barcode, state: null));
            }
            Assert.AreEqual(before, await ReadNativeIdentitySideEffectsAsync(contexts, provider.OrganizationId),
                "Eligibility rejection cannot persist a request, creation event or submission outbox.");
        }
        finally
        {
            mutation.BeforeIntentValidationForTesting = null;
            provider.AfterRefresh = null;
            provider.AfterEffect = null;
            await DeleteTestPatronSessionAsync(issued.Context.Id);
            await CleanupPickupPolicyAsync(provider.OrganizationId);
            await CleanupNativeIdentityPickupLibraryAsync(provider.OrganizationId);
        }
    }
}
