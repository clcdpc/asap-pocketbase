using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
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
    public async Task StaffSuggestionRevalidatesConfiguredLimit()
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
            workflow.SuggestionLimitMessage = "Configured staff limit reached.";
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

            Assert.AreEqual(HttpStatusCode.NotAcceptable, response.StatusCode,
                await response.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("suggestion_limit_reached", body.RootElement.GetProperty("code").GetString());
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
    public async Task StaffSuggestionCreationPersistsAuthoritativeScopeActorAndVerifiedBib()
    {
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
                    barcode = "20000000000001",
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
            Assert.AreEqual("20000000000001", request.Barcode);
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
    public async Task StaffDuplicateConflictPreservesBigintDuplicateIdAsString()
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
                        "SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'book';";
                    formatId = Convert.ToInt64(await format.ExecuteScalarAsync());
                }

                try
                {
                    await using var seed = connection.CreateCommand();
                    seed.CommandText =
                        """
                        SET IDENTITY_INSERT [asap].[TitleRequest] ON;
                        INSERT INTO [asap].[TitleRequest]
                            ([Id], [LibraryOrganizationId], [PatronOrganizationId], [Barcode], [Title], [Author],
                             [AutoHold], [MaterialFormatId], [Status], [CreatedUtc], [UpdatedUtc])
                        VALUES (@id, 2, 2, @barcode, @title, N'Existing author', 1, @formatId,
                                N'suggestion', SYSUTCDATETIME(), SYSUTCDATETIME());
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
                    title,
                    author = "New author",
                    publication = "Coming soon",
                    preferredPickupBranchId = 101,
                    currentPreferredPickupBranchIdAtLoad = 101,
                    autohold = true,
                    emailPatronConfirmation = false,
                    customFields = new Dictionary<string, string?>()
                });

            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode, await response.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var duplicate = body.RootElement.GetProperty("duplicate");
            Assert.AreEqual(JsonValueKind.String, duplicate.GetProperty("id").ValueKind);
            Assert.AreEqual("9007199254740993", duplicate.GetProperty("id").GetString());
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
