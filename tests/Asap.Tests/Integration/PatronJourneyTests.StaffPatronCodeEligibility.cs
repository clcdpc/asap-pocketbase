using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow("allowed", true)]
    [DataRow("excluded", false)]
    [DataRow("disabled", true)]
    [DataRow("empty", true)]
    [DataRow("missing", true)]
    public async Task StaffPatronCodeLookupAndCreationPreserveEffectivePolicy(string mode, bool allowed)
    {
        var provider = new PickupJournalProvider(39601) { PatronCodeId = mode == "missing" ? null : mode == "allowed" ? 999 : 1 };
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await SeedPickupJournalRequestAsync(contexts, provider);
        var actor = await ReadConfiguredSuperAdminAsync();
        var title = "Staff code policy " + Guid.NewGuid().ToString("N");
        try
        {
            await EnableIneligiblePatronCodePolicyAsync(provider.OrganizationId);
            await ExecuteNonQueryAsync("""
                UPDATE [asap].[WorkflowSettings] SET [PatronCodeEligibilityMessage]=N'Please contact the library.'
                WHERE [OrganizationId]=@org;
                """, ("@org", provider.OrganizationId));
            if (mode == "disabled")
            {
                await ExecuteNonQueryAsync("UPDATE [asap].[WorkflowSettings] SET [PatronCodeEligibilityEnabled]=0 WHERE [OrganizationId]=@org;",
                    ("@org", provider.OrganizationId));
            }
            if (mode == "empty")
            {
                await ExecuteNonQueryAsync("DELETE FROM [asap].[PatronCodeEligibilityMember] WHERE [OrganizationId]=@org;",
                    ("@org", provider.OrganizationId));
            }
            using var client = scoped.CreateClient();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
            foreach (var explicitBarcode in new[] { false, true })
            {
                using var response = await client.PostAsJsonAsync("/api/asap/staff/patron-lookup",
                    new StaffPatronLookupInput(explicitBarcode ? null : provider.Barcode,
                        explicitBarcode ? provider.Barcode : null, provider.OrganizationId));
                Assert.AreEqual(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode,
                    await response.Content.ReadAsStringAsync());
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.AreEqual(allowed ? "verified" : "ineligible", body.RootElement.GetProperty("status").GetString());
                if (!allowed)
                {
                    Assert.AreEqual("patron_ineligible", body.RootElement.GetProperty("code").GetString());
                    AssertStaffPatronCodeMessage(body.RootElement.GetProperty("message").GetString()!);
                    Assert.IsFalse(body.RootElement.TryGetProperty("patron", out _));
                }
            }
            using var created = await client.PostAsJsonAsync("/api/asap/staff/suggestions", StaffPatronCodeInput(provider, title));
            Assert.AreEqual(allowed ? HttpStatusCode.Created : HttpStatusCode.Forbidden, created.StatusCode,
                await created.Content.ReadAsStringAsync());
            Assert.AreEqual(0, provider.Writes);
            if (!allowed)
            {
                using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
                Assert.AreEqual("patron_ineligible", body.RootElement.GetProperty("code").GetString());
                AssertStaffPatronCodeMessage(body.RootElement.GetProperty("message").GetString()!);
                await AssertNoSuggestionSideEffectsAsync(contexts, provider, title, 0, expectedRequestCount: 1);
                Assert.AreEqual(0, await PickupJournalCountAsync(provider.Barcode, null));
            }
        }
        finally
        {
            await CleanupPickupPolicyAsync(provider.OrganizationId);
            await CleanupPickupJournalLibraryAsync(provider.OrganizationId);
        }
    }

    [TestMethod]
    [DataRow("code", "patron_ineligible", 0)]
    [DataRow("library", "patron_library_forbidden", 0)]
    [DataRow("mixed", "patron_selection_ineligible", 0)]
    [DataRow("single", null, 1)]
    [DataRow("multiple", null, 2)]
    public async Task StaffPatronNameSearchExplainsOnlyAuthorizedExclusionsAndReverifiesSelection(
        string mode, string? expectedCode, int eligibleCount)
    {
        var seed = new PickupJournalProvider(39602);
        var provider = new DeterministicTestingPatronProvider();
        var codeDenied = new PatronSnapshot(396201, "code-denied-card", "private@example.org", "Hidden", "Code Candidate",
            1, "Private code description", seed.FirstBranch, seed.OrganizationId, "Home", seed.FirstBranch);
        var libraryDenied = codeDenied with { Barcode = "library-denied-card", PatronId = 396202, HomeLibraryOrganizationId = 2 };
        var eligible = codeDenied with { Barcode = "eligible-card", PatronId = 396203, PatronCodeId = 999,
            NameFirst = "Eligible", Email = "eligible@example.org" };
        var second = eligible with { Barcode = "second-eligible-card", PatronId = 396204 };
        var candidates = mode switch
        {
            "code" => new[] { codeDenied },
            "library" => new[] { libraryDenied },
            "mixed" => new[] { codeDenied, libraryDenied },
            "single" => new[] { codeDenied, libraryDenied, eligible },
            _ => new[] { codeDenied, libraryDenied, eligible, second }
        };
        foreach (var patron in candidates)
        {
            provider.AddPatron(patron, [new(seed.FirstBranch, "Current pickup")], seed.OrganizationId);
        }
        provider.SetPatronSearch("Candidate Name", seed.OrganizationId, candidates);
        await using var scoped = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton<IPatronProvider>(provider);
            services.AddSingleton<IStaffPolarisProvider>(provider);
        }));
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await SeedPickupJournalRequestAsync(contexts, seed);
        try
        {
            await EnableIneligiblePatronCodePolicyAsync(seed.OrganizationId);
            var actor = await ReadConfiguredSuperAdminAsync();
            using var client = scoped.CreateClient();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
            using var response = await client.PostAsJsonAsync("/api/asap/staff/patron-lookup",
                new { query = "Candidate Name", libraryOrgId = seed.OrganizationId });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var json = await response.Content.ReadAsStringAsync();
            using var body = JsonDocument.Parse(json);
            Assert.AreEqual(expectedCode, body.RootElement.GetProperty("code").GetString());
            Assert.AreEqual(eligibleCount == 0 ? "ineligible" : eligibleCount == 1 ? "verified" : "multiple_matches",
                body.RootElement.GetProperty("status").GetString());
            foreach (var secret in new[] { codeDenied.Barcode, libraryDenied.Barcode, codeDenied.Email!, codeDenied.PatronCodeDescription! })
            {
                Assert.IsFalse(json.Contains(secret, StringComparison.Ordinal), "Excluded candidates must not cross the response boundary.");
            }
            if (mode == "code")
            {
                AssertStaffPatronCodeMessage(body.RootElement.GetProperty("message").GetString()!);
            }
            if (mode == "library")
            {
                Assert.IsFalse(json.Contains("patron code", StringComparison.OrdinalIgnoreCase),
                    "Code policy for a library-excluded patron must not be disclosed.");
            }
            if (mode == "mixed")
            {
                StringAssert.Contains(body.RootElement.GetProperty("message").GetString()!, "patron-code eligibility");
                Assert.AreEqual(0, body.RootElement.GetProperty("matches").GetArrayLength());
            }
            if (eligibleCount > 0)
            {
                Assert.AreEqual(eligibleCount == 1 ? 2 : 1,
                    provider.Calls.Count(call => call.Operation == TestingPolarisOperation.Refresh && call.Key == eligible.Barcode));
                provider.AddPatron(eligible with { PatronCodeId = 1 }, [new(seed.FirstBranch, "Current pickup")], seed.OrganizationId);
                using var selected = await client.PostAsJsonAsync("/api/asap/staff/patron-lookup",
                    new { barcode = eligible.Barcode, libraryOrgId = seed.OrganizationId });
                Assert.AreEqual(HttpStatusCode.Forbidden, selected.StatusCode, await selected.Content.ReadAsStringAsync());
                using var rejection = JsonDocument.Parse(await selected.Content.ReadAsStringAsync());
                Assert.AreEqual("patron_ineligible", rejection.RootElement.GetProperty("code").GetString());
                AssertStaffPatronCodeMessage(rejection.RootElement.GetProperty("message").GetString()!);
            }
            Assert.AreEqual(1, provider.Calls.Count(call => call.Operation == TestingPolarisOperation.PatronSearch),
                "An eligibility denial must never trigger a name-search fallback.");
            Assert.AreEqual(0, provider.Calls.Count(call => call.Operation == TestingPolarisOperation.PickupUpdate));
        }
        finally
        {
            await CleanupPickupPolicyAsync(seed.OrganizationId);
            await CleanupPickupJournalLibraryAsync(seed.OrganizationId);
        }
    }

    [TestMethod]
    [DataRow("lookup")]
    [DataRow("authoritative_refresh")]
    [DataRow("preflight")]
    [DataRow("pickup_intent")]
    [DataRow("post_mutation")]
    [DataRow("final")]
    [DataRow("final_after_pickup")]
    public async Task StaffPatronCodeChangesAreExplainedAtEveryCreationBoundary(string boundary)
    {
        var provider = new PickupJournalProvider(39603);
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
        var mutation = scoped.Services.GetRequiredService<PickupPreferenceMutationService>();
        var service = scoped.Services.GetRequiredService<StaffSuggestionService>();
        var actor = await ReadConfiguredSuperAdminAsync();
        var title = "Staff boundary " + boundary + Guid.NewGuid().ToString("N");
        var changedPickup = boundary is "pickup_intent" or "post_mutation" or "final_after_pickup";
        try
        {
            var lookup = await service.LookupAsync(actor, new(null, provider.Barcode, provider.OrganizationId), CancellationToken.None);
            Assert.AreEqual("verified", lookup.Status);
            provider.AfterRefresh = null;
            var initialReads = provider.Reads;
            if (boundary == "lookup")
            {
                await EnableIneligiblePatronCodePolicyAsync(provider.OrganizationId);
            }
            if (boundary is "authoritative_refresh" or "post_mutation")
            {
                await EnableIneligiblePatronCodePolicyAsync(provider.OrganizationId);
                provider.PatronCodeId = 999;
                if (boundary == "authoritative_refresh")
                {
                    provider.AfterRefresh = _ =>
                    {
                        if (provider.Reads == initialReads + 2)
                        {
                            provider.PatronCodeId = 1;
                        }
                        return Task.CompletedTask;
                    };
                }
                else
                {
                    provider.AfterEffect = _ =>
                    {
                        provider.PatronCodeId = 1;
                        return Task.CompletedTask;
                    };
                }
            }
            if (boundary == "preflight")
            {
                provider.AfterRefresh = async token =>
                {
                    token.ThrowIfCancellationRequested();
                    if (provider.Reads == initialReads + 2)
                    {
                        await EnableIneligiblePatronCodePolicyAsync(provider.OrganizationId);
                    }
                };
            }
            if (boundary == "pickup_intent")
            {
                mutation.BeforeIntentValidationForTesting = async token =>
                {
                    token.ThrowIfCancellationRequested();
                    await EnableIneligiblePatronCodePolicyAsync(provider.OrganizationId);
                };
            }
            var input = StaffPatronCodeInput(provider, title) with
            {
                PreferredPickupBranchId = changedPickup ? provider.SecondBranch : provider.FirstBranch,
                EmailPatronConfirmation = boundary is "final" or "final_after_pickup"
            };
            using var client = scoped.CreateClient();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
            using var response = await client.PostAsJsonAsync("/api/asap/staff/suggestions", input);
            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, await response.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var partial = boundary is "post_mutation" or "final_after_pickup";
            Assert.AreEqual(partial ? "request_not_created_pickup_changed" : "patron_ineligible",
                body.RootElement.GetProperty("code").GetString());
            AssertStaffPatronCodeMessage(body.RootElement.GetProperty("message").GetString()!);
            if (partial)
            {
                Assert.AreEqual("patron_ineligible", body.RootElement.GetProperty("reasonCode").GetString());
                Assert.IsTrue(body.RootElement.GetProperty("pickupPreferenceChanged").GetBoolean());
                StringAssert.Contains(body.RootElement.GetProperty("message").GetString()!, "changed successfully");
            }
            Assert.AreEqual(partial ? 1 : 0, provider.Writes);
            Assert.AreEqual(partial ? 1 : 0, await PickupJournalCountAsync(provider.Barcode, null));
            Assert.AreEqual(boundary is "final" or "final_after_pickup" ? 1 : 0, sender.ReadinessCalls);
            await AssertNoSuggestionSideEffectsAsync(contexts, provider, title, 0, expectedRequestCount: 1);
        }
        finally
        {
            mutation.BeforeIntentValidationForTesting = null;
            provider.AfterRefresh = null;
            provider.AfterEffect = null;
            await CleanupPickupPolicyAsync(provider.OrganizationId);
            await CleanupPickupJournalLibraryAsync(provider.OrganizationId);
        }
    }

    private static StaffSuggestionInput StaffPatronCodeInput(PickupJournalProvider provider, string title) =>
        new(provider.OrganizationId, provider.Barcode, "book", title, "Author", null, "Coming soon", null, null,
            provider.FirstBranch, provider.FirstBranch, true, false, new Dictionary<string, string?>(),
            CurrentPreferredPickupBranchObservedAtLoad: true);

    private static void AssertStaffPatronCodeMessage(string message)
    {
        StringAssert.Contains(message, "Polaris patron code");
        StringAssert.Contains(message, "selected servicing library");
        StringAssert.Contains(message, "Staff cannot override");
    }
}
