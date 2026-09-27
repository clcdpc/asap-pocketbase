using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
    public async Task StaffSuggestionMalformedVerifiedBibReturnsInvalidBibBeforeMutation() =>
        await AssertStaffBibFailureAsync("not-a-bib", HttpStatusCode.BadRequest, "invalid_bib", 0);

    [TestMethod]
    public async Task StaffSuggestionMissingVerifiedBibReturnsNotFoundBeforeMutation() =>
        await AssertStaffBibFailureAsync("9001", HttpStatusCode.NotFound, "bib_not_found", 1);

    [TestMethod]
    public async Task StaffSuggestionBibProviderFailureReturnsUnavailableBeforeMutation() =>
        await AssertStaffBibFailureAsync(
            "9001", HttpStatusCode.BadGateway, "bib_validation_unavailable", 1, operationalFailure: true);

    [TestMethod]
    public async Task StaffSuggestionBibValidationCancellationPropagates()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var pickup = new ControllablePickupPatronProvider();
        var bib = new RejectingBibStaffProvider { CancelValidation = true };
        await using var scopedFactory = CreateStaffBibFailureFactory(pickup, bib);
        var service = scopedFactory.Services.GetRequiredService<StaffSuggestionService>();
        var title = $"Canceled BIB {Guid.NewGuid():N}";

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await service.CreateAsync(actor, StaffBibInput(title, "9001"), CancellationToken.None));

        Assert.AreEqual(1, bib.ValidationCount);
        Assert.AreEqual(0, pickup.UpdateCount);
        await AssertNoStaffBibRequestAsync(title);
    }

    [TestMethod]
    public async Task StaffSuggestionPatronSearchCancellationPropagates()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var pickup = new ControllablePickupPatronProvider();
        var bib = new RejectingBibStaffProvider { CancelSearch = true };
        await using var scopedFactory = CreateStaffBibFailureFactory(pickup, bib);
        var service = scopedFactory.Services.GetRequiredService<StaffSuggestionService>();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await service.LookupAsync(actor, new StaffPatronLookupInput("Alex Example", null, 2),
                CancellationToken.None));

        Assert.AreEqual(0, pickup.UpdateCount);
    }

    private async Task AssertStaffBibFailureAsync(
        string verifiedBibId,
        HttpStatusCode expectedStatus,
        string expectedCode,
        int expectedValidationCount,
        bool operationalFailure = false)
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var pickup = new ControllablePickupPatronProvider();
        var bib = new RejectingBibStaffProvider { OperationalFailure = operationalFailure };
        await using var scopedFactory = CreateStaffBibFailureFactory(pickup, bib);
        using var client = scopedFactory.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        var title = $"Rejected BIB {Guid.NewGuid():N}";

        using var response = await client.PostAsJsonAsync(
            "/api/asap/staff/suggestions", StaffBibInput(title, verifiedBibId));

        Assert.AreEqual(expectedStatus, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(expectedCode, body.RootElement.GetProperty("code").GetString());
        Assert.AreEqual(expectedValidationCount, bib.ValidationCount);
        Assert.AreEqual(0, pickup.UpdateCount);
        await AssertNoStaffBibRequestAsync(title);
    }

    private WebApplicationFactory<Program> CreateStaffBibFailureFactory(
        ControllablePickupPatronProvider pickup,
        RejectingBibStaffProvider bib) => factory!.WithWebHostBuilder(builder =>
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.AddSingleton<IPatronProvider>(pickup);
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton<IStaffPolarisProvider>(bib);
        }));

    private static StaffSuggestionInput StaffBibInput(string title, string verifiedBibId) => new(
        2, "20000000001401", "book", title, "Staff author", null, null, null, null,
        102, 101, false, false, new Dictionary<string, string?>(), verifiedBibId, true);

    private async Task AssertNoStaffBibRequestAsync(string title)
    {
        await using var context = await factory!.Services
            .GetRequiredService<IDbContextFactory<AsapDbContext>>()
            .CreateDbContextAsync();
        Assert.AreEqual(0, await context.TitleRequests.AsNoTracking()
            .CountAsync(item => item.Barcode == "20000000001401" && item.Title == title));
    }

    [TestMethod]
    public async Task PolarisPatronRefreshDistinguishesDefinitiveNotFoundFromProviderFailure()
    {
        var notFound = await CreatePolarisProviderAsync(new ProtectedSequenceResponseHandler(
            (HttpStatusCode.OK,
             "{\"PAPIErrorCode\":-3000,\"ErrorMessage\":\"Patron not found\",\"PatronBasicData\":null}")));
        var notFoundFailure = await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await notFound.RefreshAsync("missing", CancellationToken.None));
        Assert.AreEqual("polaris_patron_not_found", notFoundFailure.Code, notFoundFailure.ToString());

        var unavailable = await CreatePolarisProviderAsync(new ProtectedSequenceResponseHandler(
            (HttpStatusCode.OK,
             "{\"PAPIErrorCode\":-1,\"ErrorMessage\":\"Provider reports patron not found while SQL is unavailable\",\"PatronBasicData\":null}")));
        var unavailableFailure = await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await unavailable.RefreshAsync("provider-error", CancellationToken.None));
        Assert.AreEqual("polaris_patron_refresh_failed", unavailableFailure.Code);
    }

    [TestMethod]
    public async Task PolarisPatronSearchTreatsOnlyCoherentEmptyResponsesAsNoMatch()
    {
        var empty = await CreatePolarisProviderAsync(new ProtectedSequenceResponseHandler(
            (HttpStatusCode.OK,
             "{\"PAPIErrorCode\":-1,\"ErrorMessage\":\"\",\"TotalRecordsFound\":0,\"PatronSearchRows\":[]}")));
        Assert.AreEqual(0, (await empty.SearchPatronsAsync("none", CancellationToken.None)).Count);

        var unavailable = await CreatePolarisProviderAsync(new ProtectedSequenceResponseHandler(
            (HttpStatusCode.OK,
             "{\"PAPIErrorCode\":-1,\"ErrorMessage\":\"SQL timeout\",\"TotalRecordsFound\":0,\"PatronSearchRows\":[]}")));
        var failure = await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await unavailable.SearchPatronsAsync("provider-error", CancellationToken.None));
        Assert.AreEqual("polaris_patron_search_failed", failure.Code);
    }

    [TestMethod]
    public async Task StaffPatronLookupPreservesMultipleIneligibleAndProviderFailureOutcomes()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        using var client = factory!.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

        using var multiple = await client.PostAsJsonAsync(
            "/api/asap/staff/patron-lookup",
            new { query = "MULTIPLE", barcode = (string?)null, libraryOrgId = 2 });
        Assert.AreEqual(HttpStatusCode.OK, multiple.StatusCode, await multiple.Content.ReadAsStringAsync());
        using var multipleBody = JsonDocument.Parse(await multiple.Content.ReadAsStringAsync());
        Assert.AreEqual("multiple_matches", multipleBody.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(2, multipleBody.RootElement.GetProperty("matches").GetArrayLength());

        using var spacedName = await client.PostAsJsonAsync(
            "/api/asap/staff/patron-lookup",
            new { query = "MULTIPLE NAME", barcode = (string?)null, libraryOrgId = 2 });
        Assert.AreEqual(HttpStatusCode.OK, spacedName.StatusCode, await spacedName.Content.ReadAsStringAsync());
        using var spacedBody = JsonDocument.Parse(await spacedName.Content.ReadAsStringAsync());
        Assert.AreEqual("multiple_matches", spacedBody.RootElement.GetProperty("status").GetString());

        using var ineligible = await client.PostAsJsonAsync(
            "/api/asap/staff/patron-lookup",
            new { query = "INELIGIBLE", barcode = (string?)null, libraryOrgId = 2 });
        Assert.AreEqual(HttpStatusCode.OK, ineligible.StatusCode, await ineligible.Content.ReadAsStringAsync());
        using var ineligibleBody = JsonDocument.Parse(await ineligible.Content.ReadAsStringAsync());
        Assert.AreEqual("ineligible", ineligibleBody.RootElement.GetProperty("status").GetString());

        using var unavailable = await client.PostAsJsonAsync(
            "/api/asap/staff/patron-lookup",
            new { query = "PROVIDER", barcode = (string?)null, libraryOrgId = 2 });
        Assert.AreEqual(HttpStatusCode.BadGateway, unavailable.StatusCode, await unavailable.Content.ReadAsStringAsync());
        using var unavailableBody = JsonDocument.Parse(await unavailable.Content.ReadAsStringAsync());
        Assert.AreEqual("polaris_unavailable", unavailableBody.RootElement.GetProperty("code").GetString());

        using var alphaFailure = await client.PostAsJsonAsync(
            "/api/asap/staff/patron-lookup",
            new { query = "ALPHAFAIL", barcode = (string?)null, libraryOrgId = 2 });
        Assert.AreEqual(HttpStatusCode.BadGateway, alphaFailure.StatusCode,
            await alphaFailure.Content.ReadAsStringAsync());
        using var alphaFailureBody = JsonDocument.Parse(await alphaFailure.Content.ReadAsStringAsync());
        Assert.AreEqual("polaris_unavailable", alphaFailureBody.RootElement.GetProperty("code").GetString());

        using var barcodeUnavailable = await client.PostAsJsonAsync(
            "/api/asap/staff/patron-lookup",
            new { query = (string?)null, barcode = "PROVIDER123", libraryOrgId = 2 });
        Assert.AreEqual(HttpStatusCode.BadGateway, barcodeUnavailable.StatusCode,
            await barcodeUnavailable.Content.ReadAsStringAsync());
        using var barcodeUnavailableBody = JsonDocument.Parse(await barcodeUnavailable.Content.ReadAsStringAsync());
        Assert.AreEqual("polaris_unavailable", barcodeUnavailableBody.RootElement.GetProperty("code").GetString());

        using var unresolved = await client.PostAsJsonAsync(
            "/api/asap/staff/patron-lookup",
            new { query = "UNRESOLVED", barcode = (string?)null, libraryOrgId = 2 });
        Assert.AreEqual(HttpStatusCode.BadGateway, unresolved.StatusCode, await unresolved.Content.ReadAsStringAsync());
        using var unresolvedBody = JsonDocument.Parse(await unresolved.Content.ReadAsStringAsync());
        Assert.AreEqual("polaris_unavailable", unresolvedBody.RootElement.GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task StaffDeactivatedDuringPickupBranchLookupCannotReceivePatronContext()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var provider = new ControllablePickupPatronProvider();
        var branchReadReached = false;
        bool wasActive;
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using (var context = await contextFactory.CreateDbContextAsync())
        {
            wasActive = await context.StaffUsers.Where(item => item.Id == actor.Id)
                .Select(item => item.IsActive).SingleAsync();
        }

        provider.BeforePickupBranchesReturn = () =>
        {
            branchReadReached = true;
            using var connection = new SqlConnection(databaseConnectionString);
            connection.Open();
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE [asap].[StaffUser] SET [IsActive] = 0 WHERE [Id] = @id;";
            update.Parameters.AddWithValue("@id", actor.Id);
            update.ExecuteNonQuery();
        };

        try
        {
            await using var scopedFactory = factory.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IPatronProvider>();
                    services.AddSingleton<IPatronProvider>(provider);
                }));
            using var client = scopedFactory.CreateClient();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
            using var response = await client.PostAsJsonAsync("/api/asap/staff/patron-lookup", new
            {
                query = (string?)null,
                barcode = "20000000001335",
                libraryOrgId = 2
            });
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode,
                await response.Content.ReadAsStringAsync());
            Assert.IsTrue(branchReadReached);
            Assert.IsFalse((await response.Content.ReadAsStringAsync()).Contains("20000000001335", StringComparison.Ordinal));
        }
        finally
        {
            await using var restore = await contextFactory.CreateDbContextAsync();
            var staff = await restore.StaffUsers.SingleAsync(item => item.Id == actor.Id);
            staff.IsActive = wasActive;
            await restore.SaveChangesAsync();
        }
    }

    [TestMethod]
    public async Task AlphabeticBarcodeGetsExactPatronRefreshBeforeNameSearch()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var provider = new ControllablePickupPatronProvider();
        await using var scopedFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.AddSingleton<IPatronProvider>(provider);
            }));
        using var client = scopedFactory.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

        using var response = await client.PostAsJsonAsync(
            "/api/asap/staff/patron-lookup",
            new { query = "ALPHACARD", barcode = (string?)null, libraryOrgId = 2 });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("verified", body.RootElement.GetProperty("status").GetString());
        Assert.AreEqual("ALPHACARD", body.RootElement.GetProperty("patron")
            .GetProperty("patron").GetProperty("barcode").GetString());
        Assert.AreEqual(1, provider.RefreshCount);
    }

    [TestMethod]
    public async Task OrdinaryStaffCannotForgeSuggestionLibraryScope()
    {
        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        var email = $"staff-suggestion-scope-{Guid.NewGuid():N}@example.org";
        long staffId;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO [asap].[StaffUser]
                    ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                     [DisplayName], [NotificationEmail], [Role], [OrganizationId], [IsActive])
                OUTPUT inserted.[Id]
                VALUES (@tenantId, NEWID(), @email, UPPER(@email), N'Suggestion scope staff', @email, N'staff', 2, 1);
                """;
            command.Parameters.AddWithValue("@tenantId", Guid.Parse(identity.TenantId!));
            command.Parameters.AddWithValue("@email", email);
            staffId = Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        using var client = factory!.CreateClient();
        AddTestingStaffHeaders(client, staffId, Guid.Parse(identity.TenantId!), email);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        using var response = await client.PostAsJsonAsync(
            "/api/asap/staff/patron-lookup",
            new { query = "20000000000001", barcode = (string?)null, libraryOrgId = 3 });

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("staff_scope_forbidden", body.RootElement.GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task StaffSuggestionRejectsTargetThatBecomesInactiveAfterLookup()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        using var client = factory!.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

        using var lookup = await client.PostAsJsonAsync(
            "/api/asap/staff/patron-lookup",
            new { query = (string?)null, barcode = "20000000000001", libraryOrgId = 2 });
        Assert.AreEqual(HttpStatusCode.OK, lookup.StatusCode, await lookup.Content.ReadAsStringAsync());

        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        bool wasActive;
        await using (var setup = await contextFactory.CreateDbContextAsync())
        {
            var organization = await setup.Organizations.SingleAsync(item => item.Id == 2);
            wasActive = organization.IsActive;
            organization.IsActive = false;
            await setup.SaveChangesAsync();
        }

        try
        {
            using var response = await client.PostAsJsonAsync(
                "/api/asap/staff/suggestions",
                new
                {
                    libraryOrgId = 2,
                    barcode = "20000000000001",
                    format = "book",
                    title = "Rejected after library deactivation",
                    author = "Staff author",
                    publication = "Coming soon",
                    preferredPickupBranchId = 101,
                    currentPreferredPickupBranchIdAtLoad = 101,
                    autohold = true,
                    emailPatronConfirmation = false,
                    customFields = new Dictionary<string, string?>()
                });

            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode, await response.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("organization_inactive", body.RootElement.GetProperty("code").GetString());
        }
        finally
        {
            await using var restore = await contextFactory.CreateDbContextAsync();
            var organization = await restore.Organizations.SingleAsync(item => item.Id == 2);
            organization.IsActive = wasActive;
            await restore.SaveChangesAsync();
        }
    }

    [TestMethod]
    public async Task StaffSuggestionFinalGateRejectsTargetDeactivationAfterPickupMutation()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var provider = new ControllablePickupPatronProvider();
        provider.AfterUpdate = () =>
        {
            using var connection = new SqlConnection(databaseConnectionString);
            connection.Open();
            using var command = new SqlCommand(
                "UPDATE [asap].[Organization] SET [IsActive] = 0 WHERE [Id] = 2;",
                connection);
            command.ExecuteNonQuery();
        };

        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        bool wasOrganizationActive;
        int? previousLimit;
        string? previousLimitMessage;
        await using (var setup = await contextFactory.CreateDbContextAsync())
        {
            var organization = await setup.Organizations.SingleAsync(item => item.Id == 2);
            wasOrganizationActive = organization.IsActive;
            organization.IsActive = true;
            var workflow = await setup.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == 2);
            previousLimit = workflow?.SuggestionLimit;
            previousLimitMessage = workflow?.SuggestionLimitMessage;
            if (workflow is not null)
            {
                workflow.SuggestionLimit = null;
            }

            await setup.SaveChangesAsync();
        }

        try
        {
            await using var scopedFactory = factory.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IPatronProvider>();
                    services.AddSingleton<IPatronProvider>(provider);
                }));
            using var client = scopedFactory.CreateClient();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
            using var response = await client.PostAsJsonAsync(
                "/api/asap/staff/suggestions",
                new
                {
                    libraryOrgId = 2,
                    barcode = "20000000000099",
                    format = "book",
                    title = $"Rejected after pickup race {Guid.NewGuid():N}",
                    author = "Race author",
                    publication = "Coming soon",
                    preferredPickupBranchId = 102,
                    currentPreferredPickupBranchIdAtLoad = 101,
                    autohold = true,
                    emailPatronConfirmation = false,
                    customFields = new Dictionary<string, string?>()
                });

            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode, await response.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("request_not_created_pickup_changed", body.RootElement.GetProperty("code").GetString());
            Assert.AreEqual(1, provider.UpdateCount);
        }
        finally
        {
            await using var restore = await contextFactory.CreateDbContextAsync();
            var organization = await restore.Organizations.SingleAsync(item => item.Id == 2);
            organization.IsActive = wasOrganizationActive;
            var workflow = await restore.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == 2);
            if (workflow is not null)
            {
                workflow.SuggestionLimit = previousLimit;
                workflow.SuggestionLimitMessage = previousLimitMessage;
            }

            await restore.SaveChangesAsync();
        }
    }

    [TestMethod]
    public async Task StaffBecomingInactiveBeforeCreationCannotReachPickupProvider()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var provider = new ControllablePickupPatronProvider();
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        bool wasActive;

        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.AddSingleton<IPatronProvider>(provider);
            }));
        using var client = scopedFactory.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        var antiforgery = await ReadAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", antiforgery);

        using var lookup = await client.PostAsJsonAsync(
            "/api/asap/staff/patron-lookup",
            new { query = (string?)null, barcode = "20000000000099", libraryOrgId = 2 });
        Assert.AreEqual(HttpStatusCode.OK, lookup.StatusCode, await lookup.Content.ReadAsStringAsync());

        await using (var setup = await contextFactory.CreateDbContextAsync())
        {
            var staff = await setup.StaffUsers.SingleAsync(item => item.Id == actor.Id);
            wasActive = staff.IsActive;
            staff.IsActive = false;
            await setup.SaveChangesAsync();
        }

        try
        {
            using var response = await client.PostAsJsonAsync(
                "/api/asap/staff/suggestions",
                new
                {
                    libraryOrgId = 2,
                    barcode = "20000000000099",
                    format = "book",
                    title = "Rejected after staff deactivation",
                    author = "Inactive author",
                    publication = "Coming soon",
                    preferredPickupBranchId = 102,
                    currentPreferredPickupBranchIdAtLoad = 101,
                    autohold = true,
                    emailPatronConfirmation = false,
                    customFields = new Dictionary<string, string?>()
                });

            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode,
                await response.Content.ReadAsStringAsync());
            Assert.AreEqual(0, provider.UpdateCount);
        }
        finally
        {
            await using var restore = await contextFactory.CreateDbContextAsync();
            var staff = await restore.StaffUsers.SingleAsync(item => item.Id == actor.Id);
            staff.IsActive = wasActive;
            await restore.SaveChangesAsync();
        }
    }

    [TestMethod]
    public async Task StaffSuggestionPickupRespectsLivePreferenceAndMutationFailures()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var provider = new ControllablePickupPatronProvider { IncludeEastBranch = true };
        await using var scopedFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.AddSingleton<IPatronProvider>(provider);
            }));
        using var client = scopedFactory.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        var prefix = $"Pickup concurrency {Guid.NewGuid():N}";
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        int beforeCount;
        await using (var before = await contextFactory.CreateDbContextAsync())
        {
            beforeCount = await before.TitleRequests.AsNoTracking()
                .CountAsync(item => item.LibraryOrganizationId == 2 && item.Barcode == "20000000000099");
        }

        async Task<HttpResponseMessage> SubmitAsync(string suffix, int live, int desired, bool fail = false)
        {
            provider.CurrentPickupBranchId = live;
            provider.FailUpdate = fail;
            return await client.PostAsJsonAsync("/api/asap/staff/suggestions", new
            {
                libraryOrgId = 2,
                barcode = "20000000000099",
                format = "book",
                title = $"{prefix} {suffix}",
                author = "Race author",
                publication = "Coming soon",
                preferredPickupBranchId = desired,
                currentPreferredPickupBranchIdAtLoad = 101,
                autohold = true,
                emailPatronConfirmation = false,
                customFields = new Dictionary<string, string?>()
            });
        }

        using (var stale = await SubmitAsync("stale", 103, 102))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode,
                await stale.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
            Assert.AreEqual("pickup_changed_since_load", body.RootElement.GetProperty("code").GetString());
            Assert.AreEqual(0, provider.UpdateCount);
        }

        using (var live = await SubmitAsync("live", 103, 103))
        {
            Assert.AreEqual(HttpStatusCode.Created, live.StatusCode,
                await live.Content.ReadAsStringAsync());
            Assert.AreEqual(0, provider.UpdateCount);
        }

        using (var normal = await SubmitAsync("normal", 101, 102))
        {
            Assert.AreEqual(HttpStatusCode.Created, normal.StatusCode,
                await normal.Content.ReadAsStringAsync());
            Assert.AreEqual(1, provider.UpdateCount);
        }

        using (var failed = await SubmitAsync("failed", 101, 102, fail: true))
        {
            Assert.AreEqual(HttpStatusCode.BadGateway, failed.StatusCode,
                await failed.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await failed.Content.ReadAsStringAsync());
            Assert.AreEqual("pickup_update_failed", body.RootElement.GetProperty("code").GetString());
        }

        await using var context = await contextFactory.CreateDbContextAsync();
        Assert.AreEqual(beforeCount + 2, await context.TitleRequests.AsNoTracking()
            .CountAsync(item => item.LibraryOrganizationId == 2 && item.Barcode == "20000000000099"));
    }

    [TestMethod]
    public async Task StaffSuggestionPickupObservedSnapshotHandlesNullAndThreeWayRaces()
    {
        const string barcode = "20000000001331";
        var actor = await ReadConfiguredSuperAdminAsync();
        var provider = new ControllablePickupPatronProvider { IncludeEastBranch = true };
        await using var scopedFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.AddSingleton<IPatronProvider>(provider);
            }));
        using var client = scopedFactory.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

        async Task CheckAsync(int? observed, int? live, int selected, HttpStatusCode expected, int writes)
        {
            provider.CurrentPickupBranchId = live;
            var beforeUpdates = provider.UpdateCount;
            using var response = await client.PostAsJsonAsync("/api/asap/staff/suggestions", new
            {
                libraryOrgId = 2,
                barcode,
                format = "book",
                title = $"Observed pickup {Guid.NewGuid():N}",
                author = "Pickup author",
                publication = "Coming soon",
                preferredPickupBranchId = selected,
                currentPreferredPickupBranchIdAtLoad = observed,
                currentPreferredPickupBranchObservedAtLoad = true,
                autohold = true,
                emailPatronConfirmation = false,
                customFields = new Dictionary<string, string?>()
            });
            Assert.AreEqual(expected, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.AreEqual(writes, provider.UpdateCount - beforeUpdates);
            if (expected == HttpStatusCode.Conflict)
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.AreEqual("pickup_changed_since_load", body.RootElement.GetProperty("code").GetString());
            }
        }

        await CheckAsync(101, 101, 103, HttpStatusCode.Created, 1);
        await CheckAsync(101, 102, 101, HttpStatusCode.Conflict, 0);
        await CheckAsync(101, 102, 103, HttpStatusCode.Conflict, 0);
        await CheckAsync(101, 102, 102, HttpStatusCode.Created, 0);
        await CheckAsync(null, null, 103, HttpStatusCode.Created, 1);
        await CheckAsync(null, 102, 103, HttpStatusCode.Conflict, 0);
        await CheckAsync(null, 102, 102, HttpStatusCode.Created, 0);
    }

    [TestMethod]
    public async Task ExistingDuplicateIsRejectedBeforePickupMutation()
    {
        const string barcode = "20000000001332";
        var title = $"Preflight duplicate {Guid.NewGuid():N}";
        var actor = await ReadConfiguredSuperAdminAsync();
        var provider = new ControllablePickupPatronProvider();
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        long duplicateId;
        int beforeOutbox;
        await using (var seed = await contextFactory.CreateDbContextAsync())
        {
            var formatId = await seed.MaterialFormats.Where(item => item.OwnerOrganizationId == 1 && item.Code == "book")
                .Select(item => item.Id).SingleAsync();
            var existing = new TitleRequest
            {
                LibraryOrganizationId = 2,
                PatronOrganizationId = 101,
                Barcode = barcode,
                Title = title,
                MaterialFormatId = formatId,
                AutoHold = true,
                Status = "closed",
                CloseReason = "rejected",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
            seed.TitleRequests.Add(existing);
            await seed.SaveChangesAsync();
            duplicateId = existing.Id;
            beforeOutbox = await seed.EmailOutbox.CountAsync();
        }

        try
        {
            await using var scopedFactory = factory.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IPatronProvider>();
                    services.AddSingleton<IPatronProvider>(provider);
                }));
            using var client = scopedFactory.CreateClient();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
            using var response = await client.PostAsJsonAsync("/api/asap/staff/suggestions", new
            {
                libraryOrgId = 2,
                barcode,
                format = "book",
                title,
                author = "Duplicate author",
                publication = "Coming soon",
                preferredPickupBranchId = 102,
                currentPreferredPickupBranchIdAtLoad = 101,
                currentPreferredPickupBranchObservedAtLoad = true,
                autohold = true,
                emailPatronConfirmation = true,
                customFields = new Dictionary<string, string?>()
            });
            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode, await response.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("duplicate", body.RootElement.GetProperty("code").GetString());
            var duplicate = body.RootElement.GetProperty("duplicate");
            Assert.AreEqual(duplicateId.ToString(), duplicate.GetProperty("id").GetString());
            Assert.AreEqual("title_format", duplicate.GetProperty("matchType").GetString());
            Assert.AreEqual($"/staff/?request={duplicateId}", duplicate.GetProperty("requestUrl").GetString());
            Assert.AreEqual(0, provider.UpdateCount);
            await using var verify = await contextFactory.CreateDbContextAsync();
            Assert.AreEqual(1, await verify.TitleRequests.CountAsync(item => item.Barcode == barcode && item.Title == title));
            Assert.AreEqual(0, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == duplicateId));
            Assert.AreEqual(beforeOutbox, await verify.EmailOutbox.CountAsync());
        }
        finally
        {
            await ExecuteNonQueryAsync("DELETE FROM [asap].[TitleRequest] WHERE [Id] = @id;", ("@id", duplicateId));
        }
    }

    [TestMethod]
    public async Task StaffMayCreateInformationalEbookWhilePatronFormStillRejectsIt()
    {
        const string barcode = "20000000001333";
        var actor = await ReadConfiguredSuperAdminAsync();
        var title = $"Staff informational ebook {Guid.NewGuid():N}";
        using var client = factory!.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

        object Input(string? author) => new
        {
            libraryOrgId = 2,
            barcode,
            format = "ebook",
            title,
            author,
            publication = "Coming soon",
            preferredPickupBranchId = 101,
            currentPreferredPickupBranchIdAtLoad = 101,
            currentPreferredPickupBranchObservedAtLoad = true,
            autohold = true,
            emailPatronConfirmation = false,
            customFields = new Dictionary<string, string?>()
        };

        using (var invalid = await client.PostAsJsonAsync("/api/asap/staff/suggestions", Input(null)))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode, await invalid.Content.ReadAsStringAsync());
            StringAssert.Contains(await invalid.Content.ReadAsStringAsync(), "Author is required");
        }

        using (var created = await client.PostAsJsonAsync("/api/asap/staff/suggestions", Input("Staff author")))
        {
            Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            Assert.AreEqual(JsonValueKind.String, body.RootElement.GetProperty("id").ValueKind);
        }

        var publicService = CreatePatronSuggestionService(["example.org"], new RecordingOutboxDispatcher());
        var rejected = await Assert.ThrowsExactlyAsync<PatronFlowException>(async () =>
            await publicService.CreateAsync(
                new PatronSessionContext(7004, barcode, 2, 2, 2, DateTime.UtcNow.AddHours(1)),
                Suggestion($"Patron informational ebook {Guid.NewGuid():N}") with
                {
                    Format = "ebook",
                    Author = "Patron author"
                },
                CancellationToken.None));
        Assert.AreEqual(400, rejected.StatusCode);
    }

    [TestMethod]
    public async Task StaffHiddenPublicationRuleDoesNotPersistExactDate()
    {
        const string barcode = "20000000001336";
        var title = $"Hidden publication date {Guid.NewGuid():N}";
        var actor = await ReadConfiguredSuperAdminAsync();
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        string? previousMode;
        await using (var setup = await contextFactory.CreateDbContextAsync())
        {
            var format = await setup.MaterialFormats.SingleAsync(item =>
                item.OwnerOrganizationId == 1 && item.Code == "book");
            previousMode = format.PublicationMode;
            format.PublicationMode = "hidden";
            await setup.SaveChangesAsync();
        }

        try
        {
            using var client = factory.CreateClient();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
            using var response = await client.PostAsJsonAsync("/api/asap/staff/suggestions", new
            {
                libraryOrgId = 2,
                barcode,
                format = "book",
                title,
                author = "Staff author",
                publication = "Coming soon",
                exactPublicationDate = "2026-12-01",
                preferredPickupBranchId = 101,
                currentPreferredPickupBranchIdAtLoad = 101,
                currentPreferredPickupBranchObservedAtLoad = true,
                autohold = true,
                emailPatronConfirmation = false,
                customFields = new Dictionary<string, string?>()
            });
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var id = long.Parse(body.RootElement.GetProperty("id").GetString()!);
            await using var verify = await contextFactory.CreateDbContextAsync();
            var request = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == id);
            Assert.IsNull(request.Publication);
            Assert.IsNull(request.ExactPublicationDate);
        }
        finally
        {
            await using var restore = await contextFactory.CreateDbContextAsync();
            var format = await restore.MaterialFormats.SingleAsync(item =>
                item.OwnerOrganizationId == 1 && item.Code == "book");
            format.PublicationMode = previousMode;
            await restore.SaveChangesAsync();
        }
    }

    [TestMethod]
    public async Task StaffSuggestionBypassesOnlyPublicSubmissionCountLimit()
    {
        const string barcode = "20000000009991";
        var title = $"Staff limit suggestion {Guid.NewGuid():N}";
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        long existingId;
        int? previousLimit;
        string? previousMessage;
        var hadWorkflow = false;
        await using (var context = await contextFactory.CreateDbContextAsync())
        {
            var workflow = await context.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == 2);
            hadWorkflow = workflow is not null;
            previousLimit = workflow?.SuggestionLimit;
            previousMessage = workflow?.SuggestionLimitMessage;
            if (workflow is null)
            {
                workflow = new WorkflowSettings { OrganizationId = 2 };
                context.WorkflowSettings.Add(workflow);
            }

            workflow.SuggestionLimit = 1;
            workflow.SuggestionLimitMessage = "Configured public limit reached.";
            var formatId = await context.MaterialFormats
                .Where(item => item.OwnerOrganizationId == 1 && item.Code == "book")
                .Select(item => item.Id)
                .SingleAsync();
            var existing = new TitleRequest
            {
                LibraryOrganizationId = 2,
                PatronOrganizationId = 101,
                Barcode = barcode,
                Title = "Existing limited suggestion",
                MaterialFormatId = formatId,
                Status = "suggestion",
                PreferredPickupBranchId = 101,
                PreferredPickupBranchName = "Main Library",
                LibraryNameSnapshot = "Test Library",
                AutoHold = true,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
            context.TitleRequests.Add(existing);
            await context.SaveChangesAsync();
            existingId = existing.Id;
        }

        try
        {
            var actor = await ReadConfiguredSuperAdminAsync();
            var publicService = CreatePatronSuggestionService(
                ["example.org"],
                new RecordingOutboxDispatcher());
            var publicFailure = await Assert.ThrowsExactlyAsync<PatronFlowException>(async () =>
                await publicService.CreateAsync(
                    new PatronSessionContext(7004, barcode, 2, 2, 2, DateTime.UtcNow.AddHours(1)),
                    Suggestion(title),
                    CancellationToken.None));
            Assert.AreEqual(406, publicFailure.StatusCode);

            using var client = factory.CreateClient();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
            using var response = await client.PostAsJsonAsync(
                "/api/asap/staff/suggestions",
                new
                {
                    libraryOrgId = 2,
                    barcode,
                    format = "book",
                    title,
                    author = "Limit author",
                    publication = "Coming soon",
                    preferredPickupBranchId = 101,
                    currentPreferredPickupBranchIdAtLoad = 101,
                    autohold = true,
                    emailPatronConfirmation = false,
                    customFields = new Dictionary<string, string?>()
                });

            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode,
                await response.Content.ReadAsStringAsync());
            using var duplicate = await client.PostAsJsonAsync(
                "/api/asap/staff/suggestions",
                new
                {
                    libraryOrgId = 2,
                    barcode,
                    format = "book",
                    title,
                    author = "Limit author",
                    publication = "Coming soon",
                    preferredPickupBranchId = 101,
                    currentPreferredPickupBranchIdAtLoad = 101,
                    autohold = true,
                    emailPatronConfirmation = false,
                    customFields = new Dictionary<string, string?>()
                });
            Assert.AreEqual(HttpStatusCode.Conflict, duplicate.StatusCode,
                await duplicate.Content.ReadAsStringAsync());
        }
        finally
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            var workflow = await context.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == 2);
            if (workflow is not null && hadWorkflow)
            {
                workflow.SuggestionLimit = previousLimit;
                workflow.SuggestionLimitMessage = previousMessage;
            }
            else if (workflow is not null)
            {
                context.WorkflowSettings.Remove(workflow);
            }

            var existing = await context.TitleRequests.SingleOrDefaultAsync(item => item.Id == existingId);
            if (existing is not null)
            {
                context.TitleRequests.Remove(existing);
            }

            await context.SaveChangesAsync();
        }
    }

    [TestMethod]
    public async Task StaffAutoholdChoiceIgnoresPatronOptOutSetting()
    {
        const string barcode = "20000000009992";
        var prefix = $"Staff autohold {Guid.NewGuid():N}";
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var hadWorkflow = false;
        bool? previousOptOut;
        int? previousLimit;
        await using (var setup = await contextFactory.CreateDbContextAsync())
        {
            var workflow = await setup.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == 2);
            hadWorkflow = workflow is not null;
            previousOptOut = workflow?.AllowPatronAutoholdOptOut;
            previousLimit = workflow?.SuggestionLimit;
            if (workflow is null)
            {
                workflow = new WorkflowSettings { OrganizationId = 2 };
                setup.WorkflowSettings.Add(workflow);
            }

            workflow.AllowPatronAutoholdOptOut = false;
            workflow.SuggestionLimit = null;
            await setup.SaveChangesAsync();
        }

        try
        {
            var actor = await ReadConfiguredSuperAdminAsync();
            using var client = factory.CreateClient();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

            async Task<long> CreateStaffAsync(string suffix, bool? autohold)
            {
                using var response = await client.PostAsJsonAsync("/api/asap/staff/suggestions", new
                {
                    libraryOrgId = 2,
                    barcode,
                    format = "book",
                    title = $"{prefix} {suffix}",
                    author = "Staff author",
                    publication = "Coming soon",
                    preferredPickupBranchId = 101,
                    currentPreferredPickupBranchIdAtLoad = 101,
                    autohold,
                    emailPatronConfirmation = false,
                    customFields = new Dictionary<string, string?>()
                });
                Assert.AreEqual(HttpStatusCode.Created, response.StatusCode,
                    await response.Content.ReadAsStringAsync());
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return long.Parse(body.RootElement.GetProperty("id").GetString()!);
            }

            var staffFalse = await CreateStaffAsync("false", false);
            var staffTrue = await CreateStaffAsync("true", true);
            var staffDefault = await CreateStaffAsync("default", null);
            var publicService = CreatePatronSuggestionService(
                ["example.org"],
                new RecordingOutboxDispatcher());
            var patronResult = await publicService.CreateAsync(
                new PatronSessionContext(0, barcode, 2, 2, 2, DateTime.UtcNow.AddHours(1)),
                Suggestion($"{prefix} patron") with { Autohold = false },
                CancellationToken.None);

            await using var context = await contextFactory.CreateDbContextAsync();
            var holds = await context.TitleRequests.AsNoTracking()
                .Where(item => new[] { staffFalse, staffTrue, staffDefault, patronResult.Id }.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, item => item.AutoHold);
            Assert.IsFalse(holds[staffFalse]);
            Assert.IsTrue(holds[staffTrue]);
            Assert.IsTrue(holds[staffDefault]);
            Assert.IsTrue(holds[patronResult.Id]);
        }
        finally
        {
            await using var restore = await contextFactory.CreateDbContextAsync();
            var workflow = await restore.WorkflowSettings.SingleAsync(item => item.OrganizationId == 2);
            if (hadWorkflow)
            {
                workflow.AllowPatronAutoholdOptOut = previousOptOut;
                workflow.SuggestionLimit = previousLimit;
            }
            else
            {
                restore.WorkflowSettings.Remove(workflow);
            }

            await restore.SaveChangesAsync();
        }
    }

    [TestMethod]
    public async Task StaffSuggestionCreationPersistsAuthoritativeScopeActorAndVerifiedBib()
    {
        const string barcode = "20000000009993";
        var actor = await ReadConfiguredSuperAdminAsync();
        using var client = factory!.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        var title = $"Staff-created suggestion {Guid.NewGuid():N}";
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        int? previousLimit = null;
        string? previousMessage = null;
        var hadWorkflow = false;
        await using (var setup = await contextFactory.CreateDbContextAsync())
        {
            var workflow = await setup.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == 2);
            if (workflow is not null)
            {
                hadWorkflow = true;
                previousLimit = workflow.SuggestionLimit;
                previousMessage = workflow.SuggestionLimitMessage;
                workflow.SuggestionLimit = null;
                await setup.SaveChangesAsync();
            }
        }

        try
        {
            using var response = await client.PostAsJsonAsync(
                "/api/asap/staff/suggestions",
                new
                {
                    libraryOrgId = 2,
                    barcode,
                    format = "book",
                    title,
                    author = "Staff author",
                    identifier = (string?)null,
                    publication = "Coming soon",
                    exactPublicationDate = "2026-12-01",
                    notes = "Created during the staff suggestion journey.",
                    preferredPickupBranchId = 101,
                    currentPreferredPickupBranchIdAtLoad = 101,
                    autohold = true,
                    emailPatronConfirmation = false,
                    customFields = new Dictionary<string, string?>(),
                    verifiedBibId = "9001"
                });

            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual(JsonValueKind.String, body.RootElement.GetProperty("id").ValueKind);
            Assert.AreEqual("not_requested", body.RootElement.GetProperty("notificationStatus").GetString());
            var requestId = long.Parse(body.RootElement.GetProperty("id").GetString()!);

            await using var context = await contextFactory.CreateDbContextAsync();
            var request = await context.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == requestId);
            Assert.AreEqual(2, request.LibraryOrganizationId);
            Assert.AreEqual(2, request.StaffLibraryOrganizationIdCreatedBy);
            Assert.AreEqual(barcode, request.Barcode);
            Assert.AreEqual("9001", request.BibId);
            Assert.IsTrue(request.BibIdStaffVerified);
            Assert.AreEqual(new DateOnly(2026, 12, 1), request.ExactPublicationDate);
            Assert.AreEqual("Created during the staff suggestion journey.", request.Notes);
            var createdEvent = await context.TitleRequestEvents.AsNoTracking().SingleAsync(item =>
                item.TitleRequestId == requestId && item.EventType == "created");
            Assert.AreEqual("staff", createdEvent.ActorType);
            Assert.AreEqual(actor.Id, createdEvent.StaffUserId);
        }
        finally
        {
            if (hadWorkflow)
            {
                await using var restore = await contextFactory.CreateDbContextAsync();
                var workflow = await restore.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == 2);
                if (workflow is not null)
                {
                    workflow.SuggestionLimit = previousLimit;
                    workflow.SuggestionLimitMessage = previousMessage;
                    await restore.SaveChangesAsync();
                }
            }
        }
    }

    [TestMethod]
    public async Task StaffSuggestionReportsSuppressedConfirmationAfterSuccessfulCreation()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        int? previousLimit;
        string? previousLimitMessage;
        await using (var setup = await contextFactory.CreateDbContextAsync())
        {
            var workflow = await setup.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == 2);
            previousLimit = workflow?.SuggestionLimit;
            previousLimitMessage = workflow?.SuggestionLimitMessage;
            if (workflow is not null)
            {
                workflow.SuggestionLimit = null;
                await setup.SaveChangesAsync();
            }
        }

        try
        {
            await using var suppressedFactory = factory.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IEmailSender>();
                    services.AddSingleton<IEmailSender, SuppressedEmailSender>();
                }));
            using var client = suppressedFactory.CreateClient();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
            using var response = await client.PostAsJsonAsync(
                "/api/asap/staff/suggestions",
                new
                {
                    libraryOrgId = 2,
                    barcode = "20000000000098",
                    format = "book",
                    title = $"Suppressed confirmation {Guid.NewGuid():N}",
                    author = "Suppressed author",
                    publication = "Coming soon",
                    preferredPickupBranchId = 101,
                    currentPreferredPickupBranchIdAtLoad = 101,
                    autohold = true,
                    emailPatronConfirmation = true,
                    customFields = new Dictionary<string, string?>()
                });

            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("suppressed", body.RootElement.GetProperty("notificationStatus").GetString());
        }
        finally
        {
            await using var restore = await contextFactory.CreateDbContextAsync();
            var workflow = await restore.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == 2);
            if (workflow is not null)
            {
                workflow.SuggestionLimit = previousLimit;
                workflow.SuggestionLimitMessage = previousLimitMessage;
                await restore.SaveChangesAsync();
            }
        }
    }

    [TestMethod]
    public async Task StaffCreationReportsSuccessWhenPostCommitIdentifierSqlTimesOut()
    {
        const string barcode = "20000000001337";
        const string identifier = "9780000013337";
        var actor = await ReadConfiguredSuperAdminAsync();
        var provider = new ControllablePickupPatronProvider();
        SqlConnection? lockConnection = null;
        SqlTransaction? lockTransaction = null;
        var identifierLookupReached = false;
        provider.BeforeIdentifierLookup = () =>
        {
            identifierLookupReached = true;
            lockConnection = new SqlConnection(databaseConnectionString);
            lockConnection.Open();
            lockTransaction = lockConnection.BeginTransaction();
            using var hold = new SqlCommand(
                "SELECT [Id] FROM [asap].[Organization] WITH (XLOCK, HOLDLOCK) WHERE [Id] = 2;",
                lockConnection,
                lockTransaction);
            Assert.AreEqual(2, Convert.ToInt32(hold.ExecuteScalar()));
        };

        try
        {
            await using var scopedFactory = factory!.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IPatronProvider>();
                    services.AddSingleton<IPatronProvider>(provider);
                }));
            using var client = scopedFactory.CreateClient();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
            using var response = await client.PostAsJsonAsync("/api/asap/staff/suggestions", new
            {
                libraryOrgId = 2,
                barcode,
                format = "book",
                title = $"Post-commit identifier failure {Guid.NewGuid():N}",
                author = "Staff author",
                identifier,
                publication = "Coming soon",
                preferredPickupBranchId = 101,
                currentPreferredPickupBranchIdAtLoad = 101,
                currentPreferredPickupBranchObservedAtLoad = true,
                autohold = true,
                emailPatronConfirmation = false,
                customFields = new Dictionary<string, string?>()
            });

            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.IsTrue(identifierLookupReached);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual(JsonValueKind.String, body.RootElement.GetProperty("id").ValueKind);
            Assert.AreEqual("not_requested", body.RootElement.GetProperty("notificationStatus").GetString());
            var id = long.Parse(body.RootElement.GetProperty("id").GetString()!);
            lockTransaction?.Rollback();
            lockTransaction = null;
            lockConnection?.Dispose();
            lockConnection = null;

            await using var context = await factory.Services
                .GetRequiredService<IDbContextFactory<AsapDbContext>>()
                .CreateDbContextAsync();
            var saved = await context.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == id);
            Assert.AreEqual(identifier, saved.Identifier);
            Assert.AreEqual("pending", saved.IsbnCheckStatus);
            Assert.AreEqual(1, await context.TitleRequestEvents.CountAsync(item =>
                item.TitleRequestId == id && item.EventType == "created"));
        }
        finally
        {
            lockTransaction?.Rollback();
            lockConnection?.Dispose();
        }
    }

    [TestMethod]
    public async Task StaffVerifiedBibDuplicatePreservesBigintIdAsString()
    {
        const long duplicateId = 9007199254740993;
        const string barcode = "20000000000123";
        const string title = "Bigint duplicate suggestion";
        var actor = await ReadConfiguredSuperAdminAsync();
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        int? previousLimit;
        string? previousLimitMessage;
        var hadWorkflow = false;
        long previousIdentity = 0;
        var identityCaptured = false;

        await using (var setup = await contextFactory.CreateDbContextAsync())
        {
            var workflow = await setup.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == 2);
            hadWorkflow = workflow is not null;
            previousLimit = workflow?.SuggestionLimit;
            previousLimitMessage = workflow?.SuggestionLimitMessage;
            if (workflow is not null)
            {
                workflow.SuggestionLimit = null;
                await setup.SaveChangesAsync();
            }
        }

        try
        {
            await using (var connection = new SqlConnection(databaseConnectionString))
            {
                await connection.OpenAsync();
                await using (var identity = connection.CreateCommand())
                {
                    identity.CommandText =
                        "SELECT CONVERT(bigint, IDENT_CURRENT(N'[asap].[TitleRequest]'));";
                    previousIdentity = Convert.ToInt64(await identity.ExecuteScalarAsync());
                    identityCaptured = true;
                }

                long formatId;
                await using (var format = connection.CreateCommand())
                {
                    format.CommandText =
                        "SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'audiobook_cd';";
                    formatId = Convert.ToInt64(await format.ExecuteScalarAsync());
                }

                try
                {
                    await using var seed = connection.CreateCommand();
                    seed.CommandText =
                        """
                        SET IDENTITY_INSERT [asap].[TitleRequest] ON;
                        INSERT INTO [asap].[TitleRequest]
                            ([Id], [LibraryOrganizationId], [PatronOrganizationId], [Barcode], [Title], [Author], [Identifier], [BibId],
                             [AutoHold], [MaterialFormatId], [Status], [CloseReason], [CreatedUtc], [UpdatedUtc])
                        VALUES (@id, 2, 2, @barcode, @title, N'Existing author', N'OTHER-IDENTIFIER', N'9001', 1, @formatId,
                                N'closed', N'rejected', SYSUTCDATETIME(), SYSUTCDATETIME());
                        """;
                    seed.Parameters.AddWithValue("@id", duplicateId);
                    seed.Parameters.AddWithValue("@barcode", barcode);
                    seed.Parameters.AddWithValue("@title", title);
                    seed.Parameters.AddWithValue("@formatId", formatId);
                    await seed.ExecuteNonQueryAsync();
                }
                finally
                {
                    await using var identityOff = connection.CreateCommand();
                    identityOff.CommandText = "SET IDENTITY_INSERT [asap].[TitleRequest] OFF;";
                    await identityOff.ExecuteNonQueryAsync();
                }
            }

            using var client = factory.CreateClient();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
            using var response = await client.PostAsJsonAsync(
                "/api/asap/staff/suggestions",
                new
                {
                    libraryOrgId = 2,
                    barcode,
                    format = "book",
                    title = $"{title} distinct",
                    author = "New author",
                    identifier = "NEW-IDENTIFIER",
                    publication = "Coming soon",
                    preferredPickupBranchId = 101,
                    currentPreferredPickupBranchIdAtLoad = 101,
                    autohold = true,
                    emailPatronConfirmation = false,
                    customFields = new Dictionary<string, string?>(),
                    verifiedBibId = "9001"
                });

            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode, await response.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var duplicate = body.RootElement.GetProperty("duplicate");
            Assert.AreEqual(JsonValueKind.String, duplicate.GetProperty("id").ValueKind);
            Assert.AreEqual("9007199254740993", duplicate.GetProperty("id").GetString());
            Assert.AreEqual("bibid", duplicate.GetProperty("matchType").GetString());
            Assert.AreEqual("/staff/?request=9007199254740993",
                duplicate.GetProperty("requestUrl").GetString());
        }
        finally
        {
            await ExecuteNonQueryAsync(
                "DELETE FROM [asap].[TitleRequest] WHERE [Id] = @id;",
                ("@id", duplicateId));
            if (identityCaptured)
            {
                await using var connection = new SqlConnection(databaseConnectionString);
                await connection.OpenAsync();
                await using var reset = connection.CreateCommand();
                reset.CommandText =
                    $"DBCC CHECKIDENT ('[asap].[TitleRequest]', RESEED, {previousIdentity});";
                await reset.ExecuteNonQueryAsync();
            }

            await using var restore = await contextFactory.CreateDbContextAsync();
            var workflow = await restore.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == 2);
            if (workflow is not null && hadWorkflow)
            {
                workflow.SuggestionLimit = previousLimit;
                workflow.SuggestionLimitMessage = previousLimitMessage;
                await restore.SaveChangesAsync();
            }
        }
    }

    [TestMethod]
    public async Task DuplicateRacingAfterPickupUpdateReturnsStructuredPartialFailureWithExactBigintId()
    {
        const long duplicateId = 9007199254740993;
        const string barcode = "20000000001334";
        var title = $"Post-pickup duplicate {Guid.NewGuid():N}";
        var actor = await ReadConfiguredSuperAdminAsync();
        var provider = new ControllablePickupPatronProvider();
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        long previousIdentity;
        long formatId;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var identity = connection.CreateCommand();
            identity.CommandText = "SELECT CONVERT(bigint, IDENT_CURRENT(N'[asap].[TitleRequest]'));";
            previousIdentity = Convert.ToInt64(await identity.ExecuteScalarAsync());
        }

        await using (var context = await contextFactory.CreateDbContextAsync())
        {
            formatId = await context.MaterialFormats
                .Where(item => item.OwnerOrganizationId == 1 && item.Code == "book")
                .Select(item => item.Id).SingleAsync();
        }

        provider.AfterUpdate = () =>
        {
            using var connection = new SqlConnection(databaseConnectionString);
            connection.Open();
            using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                SET IDENTITY_INSERT [asap].[TitleRequest] ON;
                INSERT INTO [asap].[TitleRequest]
                    ([Id], [LibraryOrganizationId], [PatronOrganizationId], [Barcode], [Title], [Author],
                     [AutoHold], [MaterialFormatId], [Status], [CloseReason], [CreatedUtc], [UpdatedUtc])
                VALUES (@id, 2, 101, @barcode, @title, N'Existing author',
                        1, @formatId, N'closed', N'rejected', SYSUTCDATETIME(), SYSUTCDATETIME());
                SET IDENTITY_INSERT [asap].[TitleRequest] OFF;
                """;
            insert.Parameters.AddWithValue("@id", duplicateId);
            insert.Parameters.AddWithValue("@barcode", barcode);
            insert.Parameters.AddWithValue("@title", title);
            insert.Parameters.AddWithValue("@formatId", formatId);
            insert.ExecuteNonQuery();
        };

        try
        {
            await using var scopedFactory = factory.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IPatronProvider>();
                    services.AddSingleton<IPatronProvider>(provider);
                }));
            using var client = scopedFactory.CreateClient();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
            using var response = await client.PostAsJsonAsync("/api/asap/staff/suggestions", new
            {
                libraryOrgId = 2,
                barcode,
                format = "book",
                title,
                author = "Staff author",
                publication = "Coming soon",
                preferredPickupBranchId = 102,
                currentPreferredPickupBranchIdAtLoad = 101,
                currentPreferredPickupBranchObservedAtLoad = true,
                autohold = true,
                emailPatronConfirmation = false,
                customFields = new Dictionary<string, string?>()
            });

            var raw = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode, raw);
            Assert.AreEqual(1, provider.UpdateCount);
            using var body = JsonDocument.Parse(raw);
            Assert.AreEqual("request_not_created_pickup_changed", body.RootElement.GetProperty("code").GetString());
            Assert.IsTrue(body.RootElement.GetProperty("pickupPreferenceChanged").GetBoolean());
            StringAssert.Contains(body.RootElement.GetProperty("message").GetString(),
                "preferred pickup location was changed successfully");
            Assert.AreEqual("Already Submitted", body.RootElement.GetProperty("conflictTitle").GetString());
            Assert.IsFalse(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("conflictMessage").GetString()));
            var duplicate = body.RootElement.GetProperty("duplicate");
            Assert.AreEqual(JsonValueKind.String, duplicate.GetProperty("id").ValueKind);
            Assert.AreEqual("9007199254740993", duplicate.GetProperty("id").GetString());
            Assert.AreEqual("title_format", duplicate.GetProperty("matchType").GetString());
            Assert.AreEqual("closed", duplicate.GetProperty("status").GetString());
            Assert.AreEqual("rejected", duplicate.GetProperty("closeReason").GetString());
            Assert.AreEqual(title, duplicate.GetProperty("title").GetString());
            Assert.AreEqual("Existing author", duplicate.GetProperty("author").GetString());
            Assert.AreEqual("book", duplicate.GetProperty("format").GetString());
            Assert.AreEqual("/staff/?request=9007199254740993", duplicate.GetProperty("requestUrl").GetString());
            Assert.IsFalse(raw.Contains(":9007199254740993", StringComparison.Ordinal));
            await using var verify = await contextFactory.CreateDbContextAsync();
            Assert.AreEqual(1, await verify.TitleRequests.CountAsync(item => item.Barcode == barcode && item.Title == title));
        }
        finally
        {
            await ExecuteNonQueryAsync("DELETE FROM [asap].[TitleRequest] WHERE [Id] = @id;", ("@id", duplicateId));
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var reset = connection.CreateCommand();
            reset.CommandText = $"DBCC CHECKIDENT ('[asap].[TitleRequest]', RESEED, {previousIdentity});";
            await reset.ExecuteNonQueryAsync();
        }
    }

    [TestMethod]
    public async Task StaffVerifiedBibDuplicateKeepsScopeAndIdentifierPrecedence()
    {
        const string barcode = "20000000001234";
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        long precedenceId;
        await using (var setup = await contextFactory.CreateDbContextAsync())
        {
            setup.Organizations.Add(new Organization
            {
                Id = 333,
                DisplayName = "Other Library",
                Abbreviation = "OTHER",
                IsActive = true
            });
            var formatId = await setup.MaterialFormats
                .Where(item => item.OwnerOrganizationId == 1 && item.Code == "book")
                .Select(item => item.Id)
                .SingleAsync();
            setup.TitleRequests.AddRange(
                new TitleRequest
                {
                    LibraryOrganizationId = 2,
                    PatronOrganizationId = 101,
                    Barcode = "20000000001235",
                    Title = "Other patron BIB",
                    BibId = "9001",
                    MaterialFormatId = formatId,
                    AutoHold = true,
                    Status = "suggestion",
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                },
                new TitleRequest
                {
                    LibraryOrganizationId = 333,
                    PatronOrganizationId = 101,
                    Barcode = barcode,
                    Title = "Other library BIB",
                    BibId = "9001",
                    MaterialFormatId = formatId,
                    AutoHold = true,
                    Status = "suggestion",
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                });
            await setup.SaveChangesAsync();
        }

        try
        {
            var actor = await ReadConfiguredSuperAdminAsync();
            using var client = factory.CreateClient();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

            async Task<HttpResponseMessage> SubmitAsync(string title, string? identifier) =>
                await client.PostAsJsonAsync("/api/asap/staff/suggestions", new
                {
                    libraryOrgId = 2,
                    barcode,
                    format = "book",
                    title,
                    author = "BIB author",
                    identifier,
                    publication = "Coming soon",
                    preferredPickupBranchId = 101,
                    currentPreferredPickupBranchIdAtLoad = 101,
                    autohold = true,
                    emailPatronConfirmation = false,
                    customFields = new Dictionary<string, string?>(),
                    verifiedBibId = "9001"
                });

            using (var first = await SubmitAsync($"Scoped BIB {Guid.NewGuid():N}", null))
            {
                Assert.AreEqual(HttpStatusCode.Created, first.StatusCode,
                    await first.Content.ReadAsStringAsync());
            }

            await using (var seed = await contextFactory.CreateDbContextAsync())
            {
                var formatId = await seed.MaterialFormats
                    .Where(item => item.OwnerOrganizationId == 1 && item.Code == "book")
                    .Select(item => item.Id)
                    .SingleAsync();
                var precedence = new TitleRequest
                {
                    LibraryOrganizationId = 2,
                    PatronOrganizationId = 101,
                    Barcode = barcode,
                    Title = "Identifier precedence",
                    Identifier = "BIB-PRECEDENCE-IDENTIFIER",
                    BibId = "9001",
                    MaterialFormatId = formatId,
                    AutoHold = true,
                    Status = "closed",
                    CloseReason = "rejected",
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                };
                seed.TitleRequests.Add(precedence);
                await seed.SaveChangesAsync();
                precedenceId = precedence.Id;
            }

            using var duplicate = await SubmitAsync(
                $"Identifier and BIB {Guid.NewGuid():N}", "BIB-PRECEDENCE-IDENTIFIER");
            Assert.AreEqual(HttpStatusCode.Conflict, duplicate.StatusCode,
                await duplicate.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync());
            var match = body.RootElement.GetProperty("duplicate");
            Assert.AreEqual("identifier", match.GetProperty("matchType").GetString());
            Assert.AreEqual(precedenceId.ToString(), match.GetProperty("id").GetString());
        }
        finally
        {
            await using var cleanup = await contextFactory.CreateDbContextAsync();
            var foreignRows = await cleanup.TitleRequests
                .Where(item => item.LibraryOrganizationId == 333)
                .ToListAsync();
            cleanup.TitleRequests.RemoveRange(foreignRows);
            await cleanup.SaveChangesAsync();
            var organization = await cleanup.Organizations.SingleAsync(item => item.Id == 333);
            cleanup.Organizations.Remove(organization);
            await cleanup.SaveChangesAsync();
        }
    }

    [TestMethod]
    public async Task ConcurrentStaffVerifiedBibSubmissionsCreateOnlyOneRequest()
    {
        const string barcode = "20000000001236";
        var actor = await ReadConfiguredSuperAdminAsync();
        using var client = factory!.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        var prefix = $"Concurrent BIB {Guid.NewGuid():N}";

        Task<HttpResponseMessage> SubmitAsync(int number) => client.PostAsJsonAsync(
            "/api/asap/staff/suggestions",
            new
            {
                libraryOrgId = 2,
                barcode,
                format = "book",
                title = $"{prefix} {number}",
                author = "Concurrent author",
                publication = "Coming soon",
                preferredPickupBranchId = 101,
                currentPreferredPickupBranchIdAtLoad = 101,
                autohold = true,
                emailPatronConfirmation = false,
                customFields = new Dictionary<string, string?>(),
                verifiedBibId = "9001"
            });

        var firstTask = SubmitAsync(1);
        var secondTask = SubmitAsync(2);
        using var first = await firstTask;
        using var second = await secondTask;
        var statuses = new[] { first.StatusCode, second.StatusCode };
        CollectionAssert.AreEquivalent(
            new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }, statuses);

        await using var context = await factory.Services
            .GetRequiredService<IDbContextFactory<AsapDbContext>>()
            .CreateDbContextAsync();
        Assert.AreEqual(1, await context.TitleRequests.AsNoTracking()
            .CountAsync(item => item.LibraryOrganizationId == 2 && item.Barcode == barcode && item.BibId == "9001"));
    }

    private sealed class SuppressedEmailSender : IEmailSender
    {
        public Task<EmailTransportReadiness> CheckReadinessAsync(
            int organizationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(EmailTransportReadiness.NotConfigured);

        public Task<EmailSendResult> SendAsync(
            EmailEnvelope envelope,
            CancellationToken cancellationToken) =>
            Task.FromResult(EmailSendResult.NotConfigured);
    }
}
