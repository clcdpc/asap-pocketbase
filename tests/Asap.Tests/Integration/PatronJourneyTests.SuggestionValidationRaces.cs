using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Features.Staff.Compatibility;
using Asap.Web.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow("unknown_format")]
    [DataRow("informational_format")]
    [DataRow("disabled_format")]
    [DataRow("invalid_publication")]
    [DataRow("missing_title")]
    public async Task InvalidPublicSuggestionDoesNotMutatePreferredPickup(string failure)
    {
        var provider = new StaffPortPatronProvider();
        await using var app = WithStaffPortProviders(provider, provider);
        using var client = await PublicStaffPortSuggestionClientAsync(app, "invalid-" + Guid.NewGuid().ToString("N"));

        long? disabledFormatOverride = failure == "disabled_format"
            ? await DisableBookFormatForStaffPortLibraryAsync()
            : null;
        var title = "Invalid suggestion " + Guid.NewGuid().ToString("N");
        var before = await ReadStaffSuggestionSideEffectsAsync(
            factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>());

        try
        {
            using var response = await client.PostAsJsonAsync(
                "/api/asap/patron/suggestions",
                new
                {
                    format = failure switch
                    {
                        "unknown_format" => "not-a-format",
                        "informational_format" => "ebook",
                        _ => "book"
                    },
                    title = failure == "missing_title" ? null : title,
                    author = "Validation test author",
                    isbn = (string?)null,
                    publication = failure == "invalid_publication" ? "Invalid publication option" : "Coming soon",
                    preferredPickupBranchId = 102,
                    autohold = true,
                    customFields = new Dictionary<string, string?>()
                });

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.AreEqual(0, provider.PickupUpdates,
                "An invalid suggestion must fail before changing the patron's preferred pickup branch.");
            await AssertNoStaffSuggestionSideEffectsAsync(
                factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>(),
                title,
                before);
        }
        finally
        {
            if (disabledFormatOverride is not null)
            {
                await RemoveMaterialFormatOverrideAsync(disabledFormatOverride.Value);
            }
        }
    }

    [TestMethod]
    public async Task InvalidRequiredCustomFieldDoesNotMutatePreferredPickup()
    {
        var provider = new StaffPortPatronProvider();
        await using var app = WithStaffPortProviders(provider, provider);
        var fieldKey = "required_select_" + Guid.NewGuid().ToString("N");
        var fieldId = await SeedRequiredSelectFieldAsync(fieldKey);
        using var client = await PublicStaffPortSuggestionClientAsync(app, "invalid-field-" + Guid.NewGuid().ToString("N"));
        var title = "Invalid custom field " + Guid.NewGuid().ToString("N");
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var before = await ReadStaffSuggestionSideEffectsAsync(contexts);

        try
        {
            using var response = await client.PostAsJsonAsync(
                "/api/asap/patron/suggestions",
                new
                {
                    format = "book",
                    title,
                    author = "Validation test author",
                    isbn = (string?)null,
                    publication = "Coming soon",
                    preferredPickupBranchId = 102,
                    autohold = true,
                    customFields = new Dictionary<string, string?> { [fieldKey] = "not-an-option" }
                });

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.AreEqual(0, provider.PickupUpdates,
                "An invalid required custom-field selection must fail before the pickup mutation.");
            await AssertNoStaffSuggestionSideEffectsAsync(contexts, title, before);
        }
        finally
        {
            await RemovePatronCustomFieldAsync(fieldId);
        }
    }

    [TestMethod]
    public async Task PublicationOptionRemovedWhileSubmissionWaitsIsRejectedBeforePersistence()
    {
        const int organizationId = 91801;
        var provider = new StaffPortPatronProvider(organizationId);
        var email = new GatedReadinessEmailSender();
        await using var app = WithStaffPortProviders(provider, provider, email);
        var optionId = await SeedLibraryPublicationOptionAsync(organizationId, "Coming soon");
        using var client = await PublicStaffPortSuggestionClientAsync(
            app,
            "publication-race-" + Guid.NewGuid().ToString("N"),
            organizationId);
        var title = "Publication race " + Guid.NewGuid().ToString("N");
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var before = await ReadStaffSuggestionSideEffectsAsync(contexts);
        var submission = client.PostAsJsonAsync(
            "/api/asap/patron/suggestions",
            ValidSuggestionPayload(title) with { Publication = "Coming soon" });
        HttpResponseMessage? response = null;

        try
        {
            await email.Entered.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(1, provider.PickupUpdates,
                "The initial complete validation passed before the readiness gate was reached.");
            await DisablePublicationOptionAsync(optionId);
            email.Release();

            response = await submission.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.AreEqual(1, provider.PickupUpdates);
            await AssertNoStaffSuggestionSideEffectsAsync(contexts, title, before);
        }
        finally
        {
            email.Release();
            if (!submission.IsCompleted)
            {
                response = await submission.WaitAsync(TimeSpan.FromSeconds(15));
            }
            response?.Dispose();
            await CleanupSuggestionRaceLibraryAsync(organizationId);
        }
    }

    [TestMethod]
    public async Task StaffPublicationOptionChangedDuringActorRevalidationIsRejectedBeforePickupMutation()
    {
        const int organizationId = 91805;
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        using (var setupClient = await StaffPortClientAsync(factory!))
        {
            await ConfigureStaffPortLibraryAsync(
                setupClient,
                crossLibrary: false,
                organizationId,
                allowedPatronCodeIds: ["1"]);
        }

        var actorRow = await CreateCorrectiveStaffAsync(superAdmin, "admin", organizationId);
        var actor = await ReadCorrectiveStaffAsync(actorRow);
        var optionId = await SeedLibraryPublicationOptionAsync(organizationId, "Coming soon");
        var provider = new GatedStaffPortPatronProvider(homeLibraryOrganizationId: organizationId);
        var eligibilityGate = new StaffEligibilityCommandGate(actor.Id);
        await using var app = factory!.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton<IStaffPolarisProvider>(new StaffPortPatronProvider());
            services.RemoveAll<IPatronProvider>();
            services.AddSingleton<IPatronProvider>(provider);
            services.RemoveAll<IDbContextFactory<AsapDbContext>>();
            services.AddDbContextFactory<AsapDbContext>(options =>
                options.UseSqlServer(databaseConnectionString).AddInterceptors(eligibilityGate));
        }));
        app.UseKestrel(0);
        using var client = await StaffPortClientAsync(app, actor);
        var input = new StaffSuggestionInput(
            "staff-publication-race-" + Guid.NewGuid().ToString("N"),
            "Staff publication race " + Guid.NewGuid().ToString("N"),
            "Validation test author",
            null,
            "book",
            "Coming soon",
            null,
            "102",
            true,
            organizationId.ToString());
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var before = await ReadStaffSuggestionSideEffectsAsync(contexts);
        var submission = client.PostAsJsonAsync("/api/asap/staff/suggestions", input);
        HttpResponseMessage? response = null;

        try
        {
            await provider.Entered.WaitAsync(TimeSpan.FromSeconds(15));
            eligibilityGate.Arm();
            provider.Release();
            await eligibilityGate.Entered.WaitAsync(TimeSpan.FromSeconds(15));
            await DisablePublicationOptionAsync(optionId);
            eligibilityGate.Release();

            response = await submission.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.AreEqual(0, provider.PickupUpdates,
                "A publication option removed during actor revalidation must be rejected before Polaris is mutated.");
            await AssertNoStaffSuggestionSideEffectsAsync(contexts, input.Title!, before);
        }
        finally
        {
            provider.Release();
            eligibilityGate.Release();
            if (!submission.IsCompleted)
            {
                response = await submission.WaitAsync(TimeSpan.FromSeconds(15));
            }
            response?.Dispose();
            await DeactivateCorrectiveStaffAsync(actorRow.Id);
            await CleanupSuggestionRaceLibraryAsync(organizationId);
        }
    }

    [TestMethod]
    [DataRow("deactivate")]
    [DataRow("move_out_of_scope")]
    [DataRow("unchanged")]
    public async Task StaffSuggestionUsesCoherentAuthorizationAndConfigurationBeforePickupMutation(string transition)
    {
        const int organizationId = 91806;
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        using (var setupClient = await StaffPortClientAsync(factory!))
        {
            await ConfigureStaffPortLibraryAsync(setupClient, crossLibrary: false, organizationId);
        }

        var actorRow = await CreateCorrectiveStaffAsync(superAdmin, "admin", organizationId);
        var actor = await ReadCorrectiveStaffAsync(actorRow);
        var provider = new GatedStaffPortPatronProvider(homeLibraryOrganizationId: organizationId);
        var gate = new StaffSuggestionOrganizationLockGate(organizationId);
        await using var app = factory!.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton<IStaffPolarisProvider>(new StaffPortPatronProvider());
            services.RemoveAll<IPatronProvider>();
            services.AddSingleton<IPatronProvider>(provider);
            services.RemoveAll<IDbContextFactory<AsapDbContext>>();
            services.AddDbContextFactory<AsapDbContext>(options =>
                options.UseSqlServer(databaseConnectionString).AddInterceptors(gate));
        }));
        app.UseKestrel(0);
        using var client = await StaffPortClientAsync(app, actor);
        var input = StaffPortSuggestion("pre-provider-gate-" + Guid.NewGuid().ToString("N")) with
        {
            LibraryOrgId = organizationId.ToString()
        };
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var before = await ReadStaffSuggestionSideEffectsAsync(contexts);
        var submission = client.PostAsJsonAsync("/api/asap/staff/suggestions", input);
        HttpResponseMessage? response = null;

        try
        {
            await provider.Entered.WaitAsync(TimeSpan.FromSeconds(15));
            gate.Arm();
            provider.Release();
            await gate.Entered.WaitAsync(TimeSpan.FromSeconds(15));

            if (transition != "unchanged")
            {
                var lifecycle = factory.Services.GetRequiredService<StaffLifecycleService>();
                var changed = transition switch
                {
                    "deactivate" => await lifecycle.DeactivateAsync(
                        superAdmin,
                        actorRow.Id,
                        new StaffDeactivateInput(StaffVersion.Encode(actorRow.RowVersion)),
                        CancellationToken.None),
                    "move_out_of_scope" => await lifecycle.ChangeRoleAsync(
                        superAdmin,
                        actorRow.Id,
                        new StaffRoleInput(StaffVersion.Encode(actorRow.RowVersion), "admin", 2),
                        CancellationToken.None),
                    _ => throw new InvalidOperationException($"Unknown actor transition {transition}.")
                };
                Assert.AreEqual("updated", changed.Code, transition);
            }

            gate.Release();
            response = await submission.WaitAsync(TimeSpan.FromSeconds(15));
            var responseText = await response.Content.ReadAsStringAsync();
            if (transition == "unchanged")
            {
                Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, responseText);
                Assert.AreEqual(1, provider.PickupUpdates);
                await using var after = await contexts.CreateDbContextAsync();
                Assert.IsTrue(await after.TitleRequests.AnyAsync(item => item.Title == input.Title));
                Assert.AreEqual(before.Requests + 1, await after.TitleRequests.CountAsync());
                Assert.AreEqual(before.Events + 1, await after.TitleRequestEvents.CountAsync());
                Assert.AreEqual(before.Outbox + 1, await after.EmailOutbox.CountAsync());
            }
            else
            {
                Assert.IsTrue(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                    $"{transition}: {response.StatusCode} {responseText}");
                using var body = JsonDocument.Parse(responseText);
                Assert.IsTrue(body.RootElement.GetProperty("code").GetString() is
                    "staff_session_invalid" or "staff_scope_forbidden");
                Assert.AreEqual(0, provider.PickupUpdates,
                    "A staff actor whose authority changed during the coherent database gate must not mutate Polaris.");
                await AssertNoStaffSuggestionSideEffectsAsync(contexts, input.Title!, before);
            }
        }
        finally
        {
            provider.Release();
            gate.Release();
            if (!submission.IsCompleted)
            {
                response = await submission.WaitAsync(TimeSpan.FromSeconds(15));
            }
            response?.Dispose();
            await RestoreSuggestionRaceStaffAsync(actorRow.Id, organizationId);
            await CleanupSuggestionRaceLibraryAsync(organizationId);
        }
    }

    [TestMethod]
    public async Task AutoholdOptOutPolicyChangeWhileSubmissionWaitsUsesCurrentValue()
    {
        const int organizationId = 91802;
        var provider = new StaffPortPatronProvider(organizationId);
        var email = new GatedReadinessEmailSender();
        await using var app = WithStaffPortProviders(provider, provider, email);
        using var client = await PublicStaffPortSuggestionClientAsync(
            app,
            "autohold-race-" + Guid.NewGuid().ToString("N"),
            organizationId);
        await SetLibraryAutoholdOptOutAsync(organizationId, allowed: true);
        var title = "Autohold race " + Guid.NewGuid().ToString("N");
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var before = await ReadStaffSuggestionSideEffectsAsync(contexts);
        var submission = client.PostAsJsonAsync(
            "/api/asap/patron/suggestions",
            ValidSuggestionPayload(title) with { Autohold = false });
        HttpResponseMessage? response = null;

        try
        {
            await email.Entered.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(1, provider.PickupUpdates);
            await SetLibraryAutoholdOptOutAsync(organizationId, allowed: false);
            email.Release();

            response = await submission.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
            await using var context = await contexts.CreateDbContextAsync();
            var saved = await context.TitleRequests.SingleAsync(item => item.Title == title);
            Assert.IsTrue(saved.AutoHold,
                "Disabling autohold opt-out before the transaction must override the earlier false selection.");
            Assert.AreEqual(before.Requests + 1, await context.TitleRequests.CountAsync());
            Assert.AreEqual(before.Events + 1, await context.TitleRequestEvents.CountAsync());
            Assert.AreEqual(before.Outbox + 1, await context.EmailOutbox.CountAsync());
        }
        finally
        {
            email.Release();
            if (!submission.IsCompleted)
            {
                response = await submission.WaitAsync(TimeSpan.FromSeconds(15));
            }
            response?.Dispose();
            await CleanupSuggestionRaceLibraryAsync(organizationId);
        }
    }

    [TestMethod]
    public async Task CustomSelectOptionRemovedWhileSubmissionWaitsIsRejectedBeforePersistence()
    {
        const int organizationId = 91803;
        var provider = new StaffPortPatronProvider(organizationId);
        var email = new GatedReadinessEmailSender();
        await using var app = WithStaffPortProviders(provider, provider, email);
        var fieldKey = "race_select_" + Guid.NewGuid().ToString("N");
        var fieldId = await SeedRequiredSelectFieldAsync(fieldKey, organizationId);
        using var client = await PublicStaffPortSuggestionClientAsync(
            app,
            "custom-field-race-" + Guid.NewGuid().ToString("N"),
            organizationId);
        var title = "Custom field race " + Guid.NewGuid().ToString("N");
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var before = await ReadStaffSuggestionSideEffectsAsync(contexts);
        var submission = client.PostAsJsonAsync(
            "/api/asap/patron/suggestions",
            ValidSuggestionPayload(title) with
            {
                CustomFields = new Dictionary<string, string?> { [fieldKey] = "allowed" }
            });
        HttpResponseMessage? response = null;

        try
        {
            await email.Entered.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(1, provider.PickupUpdates);
            await DisableCustomFieldOptionsAsync(fieldId);
            email.Release();

            response = await submission.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
            await AssertNoStaffSuggestionSideEffectsAsync(contexts, title, before);
        }
        finally
        {
            email.Release();
            if (!submission.IsCompleted)
            {
                response = await submission.WaitAsync(TimeSpan.FromSeconds(15));
            }
            response?.Dispose();
            await RemovePatronCustomFieldAsync(fieldId);
            await CleanupSuggestionRaceLibraryAsync(organizationId);
        }
    }

    [TestMethod]
    public async Task EffectiveFormatIdentityChangeWhileSubmissionWaitsIsRejectedAndDoesNotAutoClaimStaleFormat()
    {
        const int organizationId = 91804;
        var provider = new StaffPortPatronProvider(organizationId);
        var email = new GatedReadinessEmailSender();
        await using var app = WithStaffPortProviders(provider, provider, email);
        using var client = await PublicStaffPortSuggestionClientAsync(
            app,
            "format-identity-race-" + Guid.NewGuid().ToString("N"),
            organizationId);
        var title = "Format identity race " + Guid.NewGuid().ToString("N");
        var (originalFormatId, ruleId) = await SeedAutoClaimForCurrentStaffPortLibraryAsync(organizationId);
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var before = await ReadStaffSuggestionSideEffectsAsync(contexts);
        var submission = client.PostAsJsonAsync(
            "/api/asap/patron/suggestions",
            ValidSuggestionPayload(title));
        HttpResponseMessage? response = null;

        try
        {
            await email.Entered.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(1, provider.PickupUpdates);
            var replacementFormatId = await AddLibraryBookFormatShadowAsync(organizationId);
            Assert.AreNotEqual(originalFormatId, replacementFormatId);
            email.Release();

            response = await submission.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode, await response.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("material_format_changed", body.RootElement.GetProperty("code").GetString());
            await AssertNoStaffSuggestionSideEffectsAsync(contexts, title, before);
            await using var verify = await contexts.CreateDbContextAsync();
            Assert.IsTrue(await verify.FormatAutoClaimRules.AnyAsync(item => item.Id == ruleId));
        }
        finally
        {
            email.Release();
            if (!submission.IsCompleted)
            {
                response = await submission.WaitAsync(TimeSpan.FromSeconds(15));
            }
            response?.Dispose();
            await CleanupSuggestionRaceLibraryAsync(organizationId);
        }
    }

    [TestMethod]
    public async Task AutoClaimRuleChangedAfterCandidateLookupRetriesWithCurrentClaimant()
    {
        const int organizationId = 91805;
        var provider = new StaffPortPatronProvider(organizationId);
        var email = new GatedReadinessEmailSender();
        await using var app = WithStaffPortProviders(provider, provider, email);
        using var client = await PublicStaffPortSuggestionClientAsync(
            app,
            "autoclaim-rule-race-" + Guid.NewGuid().ToString("N"),
            organizationId);
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var initialClaimant = await CreateCorrectiveStaffAsync(superAdmin, "staff", organizationId);
        var currentClaimant = await CreateCorrectiveStaffAsync(superAdmin, "staff", organizationId);
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        long ruleId;
        await using (var context = await contexts.CreateDbContextAsync())
        {
            var formatId = await context.MaterialFormats.AsNoTracking()
                .Where(item => item.OwnerOrganizationId == 1 && item.Code == "book")
                .Select(item => item.Id)
                .SingleAsync();
            var rule = new FormatAutoClaimRule
            {
                LibraryOrganizationId = organizationId,
                MaterialFormatId = formatId,
                StaffUserId = initialClaimant.Id,
                IsActive = true,
                CreatedUtc = DateTime.UtcNow
            };
            context.FormatAutoClaimRules.Add(rule);
            await context.SaveChangesAsync();
            ruleId = rule.Id;
        }

        var title = "Auto-claim rule race " + Guid.NewGuid().ToString("N");
        var before = await ReadStaffSuggestionSideEffectsAsync(contexts);
        var submission = client.PostAsJsonAsync(
            "/api/asap/patron/suggestions",
            ValidSuggestionPayload(title));
        HttpResponseMessage? response = null;

        try
        {
            await email.Entered.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(1, provider.PickupUpdates);

            await using (var blockerConnection = new SqlConnection(databaseConnectionString))
            {
                await blockerConnection.OpenAsync();
                await using var blockerTransaction = (SqlTransaction)await blockerConnection.BeginTransactionAsync();
                await using (var blockClaimant = new SqlCommand(
                                 "SELECT [Id] FROM [asap].[StaffUser] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = @id;",
                                 blockerConnection,
                                 blockerTransaction))
                {
                    blockClaimant.Parameters.AddWithValue("@id", initialClaimant.Id);
                    Assert.AreEqual(initialClaimant.Id, Convert.ToInt64(await blockClaimant.ExecuteScalarAsync()));
                }

                email.Release();
                await WaitForOrganizationUpdateLockAsync(organizationId);
                await ChangeAutoClaimRuleClaimantAsync(ruleId, currentClaimant.Id);
                await blockerTransaction.CommitAsync();
            }

            response = await submission.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
            await using var verify = await contexts.CreateDbContextAsync();
            var saved = await verify.TitleRequests.SingleAsync(item => item.Title == title);
            Assert.AreEqual(currentClaimant.Id, saved.ClaimedByStaffUserId,
                "The retry must use the claimant from the current rule, not the stale candidate.");
            Assert.AreEqual(ruleId, saved.ClaimRuleId);
            Assert.AreEqual(1, await verify.TitleRequestEvents.CountAsync(item =>
                item.TitleRequestId == saved.Id && item.EventType == "claim_auto_assigned"));
            Assert.AreEqual(before.Requests + 1, await verify.TitleRequests.CountAsync());
            Assert.AreEqual(before.Events + 2, await verify.TitleRequestEvents.CountAsync());
            Assert.AreEqual(before.Outbox + 1, await verify.EmailOutbox.CountAsync());
        }
        finally
        {
            email.Release();
            if (!submission.IsCompleted)
            {
                response = await submission.WaitAsync(TimeSpan.FromSeconds(15));
            }
            response?.Dispose();
            await CleanupSuggestionRaceLibraryAsync(organizationId);
        }
    }

    private async Task<HttpClient> PublicStaffPortSuggestionClientAsync(
        WebApplicationFactory<Program> app,
        string barcode,
        int organizationId = 91632)
    {
        using var staffClient = await StaffPortClientAsync(app);
        await ConfigureStaffPortLibraryAsync(staffClient, crossLibrary: false, organizationId: organizationId);

        var client = app.CreateClient();
        using var login = await client.PostAsJsonAsync(
            "/api/asap/patron/login",
            new { barcode, pin = "1234", libraryOrgId = organizationId });
        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());
        using var loginBody = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            loginBody.RootElement.GetProperty("token").GetString());
        return client;
    }

    private static PatronSuggestionInput ValidSuggestionPayload(string title) => new(
        "book",
        title,
        "Validation test author",
        null,
        "Coming soon",
        102,
        true,
        new Dictionary<string, string?>());

    private async Task<long> DisableBookFormatForStaffPortLibraryAsync()
    {
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contexts.CreateDbContextAsync();
        var formatId = await context.MaterialFormats.AsNoTracking()
            .Where(item => item.OwnerOrganizationId == 1 && item.Code == "book")
            .Select(item => item.Id)
            .SingleAsync();
        context.MaterialFormatOverrides.Add(new MaterialFormatOverride
        {
            LibraryOrganizationId = 91632,
            MaterialFormatId = formatId,
            IsEnabled = false
        });
        await context.SaveChangesAsync();
        return formatId;
    }

    private async Task RemoveMaterialFormatOverrideAsync(long formatId)
    {
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contexts.CreateDbContextAsync();
        var rule = await context.MaterialFormatOverrides.SingleAsync(item =>
            item.LibraryOrganizationId == 91632 && item.MaterialFormatId == formatId);
        context.MaterialFormatOverrides.Remove(rule);
        await context.SaveChangesAsync();
    }

    private async Task<long> SeedRequiredSelectFieldAsync(string fieldKey, int organizationId = 91632)
    {
        await UpsertTestOrganizationAsync(organizationId, "Suggestion custom-field race", "SCF");
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contexts.CreateDbContextAsync();
        var formatId = await context.MaterialFormats.AsNoTracking()
            .Where(item => item.OwnerOrganizationId == 1 && item.Code == "book")
            .Select(item => item.Id)
            .SingleAsync();
        var field = new PatronCustomField
        {
            LibraryOrganizationId = organizationId,
            FieldKey = fieldKey,
            FieldType = "select",
            Label = "Required selection",
            IsEnabled = true,
            SortOrder = 90
        };
        context.PatronCustomFields.Add(field);
        await context.SaveChangesAsync();
        context.PatronCustomFieldOptions.Add(new PatronCustomFieldOption
        {
            PatronCustomFieldId = field.Id,
            OptionKey = "allowed",
            Label = "Allowed option",
            IsEnabled = true,
            SortOrder = 1
        });
        context.MaterialFormatCustomFieldRules.Add(new MaterialFormatCustomFieldRule
        {
            LibraryOrganizationId = organizationId,
            MaterialFormatId = formatId,
            PatronCustomFieldId = field.Id,
            Mode = "required"
        });
        await context.SaveChangesAsync();
        return field.Id;
    }

    private async Task<long> SeedLibraryPublicationOptionAsync(int organizationId, string label)
    {
        await UpsertTestOrganizationAsync(organizationId, "Suggestion validation race", "SVR");
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contexts.CreateDbContextAsync();
        context.PublicationOptionSets.Add(new PublicationOptionSet { OrganizationId = organizationId });
        await context.SaveChangesAsync();
        var option = new PublicationOption
        {
            OrganizationId = organizationId,
            OptionKey = "race-coming-soon",
            Label = label,
            IsEnabled = true,
            SortOrder = 10
        };
        context.PublicationOptions.Add(option);
        await context.SaveChangesAsync();
        return option.Id;
    }

    private async Task DisablePublicationOptionAsync(long optionId)
    {
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contexts.CreateDbContextAsync();
        var option = await context.PublicationOptions.SingleAsync(item => item.Id == optionId);
        option.IsEnabled = false;
        await context.SaveChangesAsync();
    }

    private async Task ChangeAutoClaimRuleClaimantAsync(long ruleId, long staffId)
    {
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contexts.CreateDbContextAsync();
        var rule = await context.FormatAutoClaimRules.SingleAsync(item => item.Id == ruleId);
        rule.StaffUserId = staffId;
        await context.SaveChangesAsync();
    }


    private sealed class StaffEligibilityCommandGate(long staffUserId) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<bool> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int armed;

        public Task Entered => entered.Task;

        public void Arm() => Interlocked.Exchange(ref armed, 1);

        public void Release() => released.TrySetResult(true);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            var isActorQuery = command.CommandText.Contains("StaffUser", StringComparison.Ordinal) &&
                               command.Parameters.Cast<DbParameter>().Any(parameter =>
                                   parameter.Value is not null and not DBNull &&
                                   Convert.ToInt64(parameter.Value) == staffUserId);
            if (isActorQuery && Interlocked.Exchange(ref armed, 0) == 1)
            {
                entered.TrySetResult(true);
                await released.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class StaffSuggestionOrganizationLockGate(int organizationId) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<bool> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int armed;

        public Task Entered => entered.Task;

        public void Arm() => Interlocked.Exchange(ref armed, 1);

        public void Release() => released.TrySetResult(true);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            var isOrganizationLock = command.CommandText.Contains("[asap].[Organization]", StringComparison.Ordinal) &&
                                     command.CommandText.Contains("UPDLOCK,HOLDLOCK", StringComparison.Ordinal) &&
                                     command.Parameters.Cast<DbParameter>().Any(parameter =>
                                         parameter.Value is not null and not DBNull &&
                                         Convert.ToInt32(parameter.Value) == organizationId);
            if (isOrganizationLock && Interlocked.Exchange(ref armed, 0) == 1)
            {
                entered.TrySetResult(true);
                await released.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    private async Task RestoreSuggestionRaceStaffAsync(long staffUserId, int organizationId)
    {
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contexts.CreateDbContextAsync();
        var staff = await context.StaffUsers.SingleOrDefaultAsync(item => item.Id == staffUserId);
        if (staff is null)
        {
            return;
        }

        staff.OrganizationId = organizationId;
        staff.Role = "admin";
        staff.IsActive = true;
        await context.SaveChangesAsync();
    }

    private async Task SetLibraryAutoholdOptOutAsync(int organizationId, bool allowed)
    {
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contexts.CreateDbContextAsync();
        var workflow = await context.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == organizationId);
        if (workflow is null)
        {
            workflow = new WorkflowSettings
            {
                OrganizationId = organizationId,
                AllowPatronAutoholdOptOut = allowed,
                UpdatedUtc = DateTime.UtcNow
            };
            context.WorkflowSettings.Add(workflow);
        }
        else
        {
            workflow.AllowPatronAutoholdOptOut = allowed;
            workflow.UpdatedUtc = DateTime.UtcNow;
        }

        await context.SaveChangesAsync();
    }

    private async Task<(long FormatId, long RuleId)> SeedAutoClaimForCurrentStaffPortLibraryAsync(int organizationId)
    {
        await UpsertTestOrganizationAsync(organizationId, "Suggestion format identity race", "SFI");
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contexts.CreateDbContextAsync();
        var formatId = await context.MaterialFormats.AsNoTracking()
            .Where(item => item.OwnerOrganizationId == 1 && item.Code == "book")
            .Select(item => item.Id)
            .SingleAsync();
        var claimantId = await context.StaffUsers.AsNoTracking()
            .Where(item => item.Role == "super_admin" && item.OrganizationId == 1 && item.IsActive)
            .Select(item => item.Id)
            .FirstAsync();
        var rule = new FormatAutoClaimRule
        {
            LibraryOrganizationId = organizationId,
            MaterialFormatId = formatId,
            StaffUserId = claimantId,
            IsActive = true,
            CreatedUtc = DateTime.UtcNow
        };
        context.FormatAutoClaimRules.Add(rule);
        await context.SaveChangesAsync();
        return (formatId, rule.Id);
    }

    private async Task<long> AddLibraryBookFormatShadowAsync(int organizationId)
    {
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contexts.CreateDbContextAsync();
        var format = new MaterialFormat
        {
            OwnerOrganizationId = organizationId,
            Code = "book",
            Label = "Library Book",
            SortOrder = 1,
            IsEnabled = true,
            MessageBehavior = "none",
            TitleMode = "required",
            TitleLabel = "Title",
            AuthorMode = "optional",
            AuthorLabel = "Author",
            IdentifierMode = "optional",
            IdentifierLabel = "Identifier number",
            PublicationMode = "optional",
            PublicationLabel = "Publication Timing",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        context.MaterialFormats.Add(format);
        await context.SaveChangesAsync();
        return format.Id;
    }

    private async Task DisableCustomFieldOptionsAsync(long fieldId)
    {
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contexts.CreateDbContextAsync();
        var options = await context.PatronCustomFieldOptions
            .Where(item => item.PatronCustomFieldId == fieldId)
            .ToListAsync();
        foreach (var option in options)
        {
            option.IsEnabled = false;
        }

        await context.SaveChangesAsync();
    }

    private static async Task CleanupSuggestionRaceLibraryAsync(int organizationId)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM [asap].[EmailOutbox]
            WHERE [BusinessKey] IN
            (
                SELECT CONCAT(N'patron-submission:', CONVERT(nvarchar(20), [Id]))
                FROM [asap].[TitleRequest]
                WHERE [LibraryOrganizationId] = @organizationId
            );
            DELETE FROM [asap].[TitleRequestEvent]
            WHERE [TitleRequestId] IN
            (
                SELECT [Id] FROM [asap].[TitleRequest] WHERE [LibraryOrganizationId] = @organizationId
            );
            DELETE FROM [asap].[TitleRequest] WHERE [LibraryOrganizationId] = @organizationId;
            DELETE FROM [asap].[FormatAutoClaimRule] WHERE [LibraryOrganizationId] = @organizationId;
            DELETE FROM [asap].[MaterialFormatCustomFieldRule] WHERE [LibraryOrganizationId] = @organizationId;
            DELETE optionRow
            FROM [asap].[PatronCustomFieldOption] AS optionRow
            JOIN [asap].[PatronCustomField] AS fieldRow ON fieldRow.[Id] = optionRow.[PatronCustomFieldId]
            WHERE fieldRow.[LibraryOrganizationId] = @organizationId;
            DELETE FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = @organizationId;
            DELETE FROM [asap].[MaterialFormatOverride] WHERE [LibraryOrganizationId] = @organizationId;
            DELETE FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = @organizationId;
            DELETE FROM [asap].[PublicationOption] WHERE [OrganizationId] = @organizationId;
            DELETE FROM [asap].[PublicationOptionSet] WHERE [OrganizationId] = @organizationId;
            DELETE FROM [asap].[PatronCodeEligibilityMember] WHERE [OrganizationId] = @organizationId;
            DELETE FROM [asap].[PatronCodeEligibilitySet] WHERE [OrganizationId] = @organizationId;
            DELETE FROM [asap].[CommonCreatorTerm] WHERE [OrganizationId] = @organizationId;
            DELETE FROM [asap].[CommonCreatorSet] WHERE [OrganizationId] = @organizationId;
            DELETE FROM [asap].[ExternalSearchProviderOverride] WHERE [LibraryOrganizationId] = @organizationId;
            DELETE FROM [asap].[WorkflowSettings] WHERE [OrganizationId] = @organizationId;
            DELETE FROM [asap].[PatronSettings] WHERE [OrganizationId] = @organizationId;
            DELETE FROM [asap].[EmailSettings] WHERE [OrganizationId] = @organizationId;
            DELETE FROM [asap].[Branding] WHERE [OrganizationId] = @organizationId;
            DELETE FROM [asap].[AdministrativeAudit] WHERE [OrganizationId] = @organizationId;
            DELETE FROM [asap].[PatronSession] WHERE [EffectiveOrganizationId] = @organizationId;
            DELETE FROM [asap].[StaffUser] WHERE [OrganizationId] = @organizationId;
            DELETE FROM [asap].[Organization] WHERE [Id] = @organizationId;
            """;
        command.Parameters.AddWithValue("@organizationId", organizationId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task RemovePatronCustomFieldAsync(long fieldId)
    {
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contexts.CreateDbContextAsync();
        context.MaterialFormatCustomFieldRules.RemoveRange(await context.MaterialFormatCustomFieldRules
            .Where(item => item.PatronCustomFieldId == fieldId)
            .ToListAsync());
        context.PatronCustomFieldOptions.RemoveRange(await context.PatronCustomFieldOptions
            .Where(item => item.PatronCustomFieldId == fieldId)
            .ToListAsync());
        context.PatronCustomFields.Remove(await context.PatronCustomFields.SingleAsync(item => item.Id == fieldId));
        await context.SaveChangesAsync();
    }
}
