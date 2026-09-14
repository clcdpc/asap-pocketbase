using System.Net;
using System.Text.Json;
using Asap.Web.Features.Administration;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task ReboundIdentityCannotBeOverwrittenByStaleSignInMetadata()
    {
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        var actor = await ReadConfiguredSuperAdminAsync();
        var target = await CreateCorrectiveStaffAsync(actor, "staff", 2);
        var oldEvidence = new StaffIdentityEvidence(target.Id, target.EntraTenantId!.Value, target.EntraObjectId!.Value);
        var newObjectId = Guid.NewGuid();
        var rebound = await factory.Services.GetRequiredService<StaffLifecycleService>().RebindAsync(actor, target.Id,
            new StaffRebindInput(StaffVersion.Encode(target.RowVersion), actor.EntraTenantId.ToString(), newObjectId.ToString(),
                "new.identity@example.org", true, "Replace the reviewed directory binding"), CancellationToken.None);
        Assert.AreEqual("updated", rebound.Code);
        var signIn = factory.Services.GetRequiredService<StaffSignInService>();
        await signIn.RecordSuccessfulSignInAsync(oldEvidence, "old.identity@example.org", "Old identity", CancellationToken.None);
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync();
        var persisted = await context.StaffUsers.SingleAsync(item => item.Id == target.Id);
        Assert.AreEqual(newObjectId, persisted.EntraObjectId);
        Assert.AreEqual(actor.EntraTenantId, persisted.EntraTenantId);
        Assert.AreEqual("new.identity@example.org", persisted.UserPrincipalName);
        Assert.AreEqual("NEW.IDENTITY@EXAMPLE.ORG", persisted.NormalizedUserPrincipalName);
        Assert.AreEqual(rebound.User!.DisplayName, persisted.DisplayName);
        Assert.AreEqual(target.NotificationEmail, persisted.NotificationEmail);
        Assert.AreEqual(rebound.User.LastLoginUtc, persisted.LastLoginUtc);
        CollectionAssert.AreEqual(rebound.User.RowVersion, persisted.RowVersion);

        await signIn.RecordSuccessfulSignInAsync(new StaffIdentityEvidence(target.Id, actor.EntraTenantId, newObjectId),
            " refreshed.identity@example.org ", " Current identity ", CancellationToken.None);
        await context.Entry(persisted).ReloadAsync();
        Assert.AreEqual(newObjectId, persisted.EntraObjectId);
        Assert.AreEqual("refreshed.identity@example.org", persisted.UserPrincipalName);
        Assert.AreEqual("REFRESHED.IDENTITY@EXAMPLE.ORG", persisted.NormalizedUserPrincipalName);
        Assert.AreEqual("Current identity", persisted.DisplayName);
        Assert.AreEqual(target.NotificationEmail, persisted.NotificationEmail);
        Assert.IsNotNull(persisted.LastLoginUtc);
        Assert.IsFalse(rebound.User.RowVersion.SequenceEqual(persisted.RowVersion));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HistoricallyUnusableBindingsCannotReceiveExplicitOrAutomaticClaims(bool disallowedTenant)
    {
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        var actor = await ReadConfiguredSuperAdminAsync();
        var lifecycle = factory.Services.GetRequiredService<StaffLifecycleService>();
        var target = await CreateCorrectiveStaffAsync(actor, "staff", 2);
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync();
        var historical = await context.StaffUsers.SingleAsync(item => item.Id == target.Id);
        if (disallowedTenant) historical.EntraTenantId = Guid.NewGuid();
        else historical.EntraObjectId = Guid.Empty;
        var now = timeProvider!.GetUtcNow().UtcDateTime;
        var format = await context.MaterialFormats.SingleAsync(item => item.OwnerOrganizationId == 1 && item.Code == "book");
        var title = new TitleRequest
        {
            LibraryOrganizationId = 2, MaterialFormatId = format.Id, Barcode = "20000000003920", Title = "Historical assignment target",
            Status = "suggestion", CreatedUtc = now, UpdatedUtc = now
        };
        var copy = new AdditionalCopyRequest
        {
            LibraryOrganizationId = 2, BibId = "9001", Title = "Historical assignment target", Status = "open", CreatedUtc = now, UpdatedUtc = now
        };
        context.TitleRequests.Add(title);
        context.AdditionalCopyRequests.Add(copy);
        await context.SaveChangesAsync();
        var previousRules = await context.FormatAutoClaimRules.Where(item => item.LibraryOrganizationId == 2 && item.MaterialFormatId == format.Id && item.IsActive).ToListAsync();
        FormatAutoClaimRule? historicalRule = null;
        try
        {
            Assert.IsFalse((await lifecycle.ListAssignmentCandidatesAsync(2, CancellationToken.None)).Any(item => item.Id == historical.Id));
            Assert.AreEqual("assignee_ineligible", (await factory.Services.GetRequiredService<TitleRequestMutationService>().AssignAsync(actor, title.Id,
                new AssignTitleRequestInput(StaffVersion.Encode(title.RowVersion), historical.Id), CancellationToken.None)).Code);
            Assert.AreEqual("assignee_ineligible", (await factory.Services.GetRequiredService<AdditionalCopyService>().AssignAsync(actor, copy.Id,
                new AssignAdditionalCopyInput(StaffVersion.Encode(copy.RowVersion), historical.Id), CancellationToken.None)).Code);
            var administration = factory.Services.GetRequiredService<AdministrationService>();
            var settings = JsonSerializer.SerializeToElement((await administration.GetSettingsAsync(actor, "2", CancellationToken.None)).Data);
            Assert.IsFalse(settings.GetProperty("autoClaimStaff").EnumerateArray().Any(item => item.GetProperty("id").GetString() == historical.Id.ToString()));
            await Assert.ThrowsAsync<InvalidOperationException>(() => administration.SaveSettingsAsync(actor, JsonSerializer.SerializeToElement(new
            {
                orgId = "2", version = settings.GetProperty("version").GetString(),
                autoClaimRules = new[] { new { materialFormatId = format.Id.ToString(), staffUserId = historical.Id.ToString(), active = true } }
            }), CancellationToken.None));
            await context.Entry(title).ReloadAsync();
            await context.Entry(copy).ReloadAsync();
            Assert.IsNull(title.ClaimedByStaffUserId);
            Assert.IsNull(copy.ClaimedByStaffUserId);
            Assert.IsFalse(await context.FormatAutoClaimRules.AnyAsync(item => item.StaffUserId == historical.Id));

            foreach (var rule in previousRules) rule.IsActive = false;
            await context.SaveChangesAsync();
            historicalRule = new FormatAutoClaimRule
            {
                LibraryOrganizationId = 2, MaterialFormatId = format.Id, StaffUserId = historical.Id, IsActive = true, CreatedUtc = now
            };
            context.FormatAutoClaimRules.Add(historicalRule);
            await context.SaveChangesAsync();
            var result = await CreatePatronSuggestionService(["example.org"], new RecordingOutboxDispatcher(), new RecordingEmailSender()).CreateAsync(
                new PatronSessionContext(9092, disallowedTenant ? "20000000003922" : "20000000003921", 2, 2, 2, DateTime.UtcNow.AddHours(1)),
                Suggestion($"Historical auto claim {Guid.NewGuid():N}"), CancellationToken.None);
            var submitted = await context.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == result.Id);
            Assert.IsNull(submitted.ClaimedByStaffUserId);
            Assert.IsNull(submitted.ClaimRuleId);
        }
        finally
        {
            if (historicalRule is not null) context.FormatAutoClaimRules.Remove(historicalRule);
            historical.EntraTenantId = target.EntraTenantId;
            historical.EntraObjectId = target.EntraObjectId;
            historical.IsActive = false;
            await context.SaveChangesAsync();
            foreach (var rule in previousRules) rule.IsActive = true;
            await context.SaveChangesAsync();
        }
    }

    [TestMethod]
    public async Task StaffScopeContractionRollsBackAllRelationshipsAndAuditTogether()
    {
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        var actor = await ReadConfiguredSuperAdminAsync();
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync();
        context.Organizations.AddRange(
            new Organization { Id = 91907, DisplayName = "Cleanup source", IsActive = true },
            new Organization { Id = 91908, DisplayName = "Cleanup destination", IsActive = true });
        await context.SaveChangesAsync();
        var target = await CreateCorrectiveStaffAsync(actor, "admin", 91907);
        var formatId = await context.MaterialFormats.Where(item => item.OwnerOrganizationId == 1 && item.Code == "book").Select(item => item.Id).SingleAsync();
        var now = timeProvider!.GetUtcNow().UtcDateTime;
        var rule = new FormatAutoClaimRule { LibraryOrganizationId = 91907, MaterialFormatId = formatId, StaffUserId = target.Id, IsActive = true, CreatedUtc = now };
        TitleRequest Title(string status) => new()
        {
            LibraryOrganizationId = 91907, MaterialFormatId = formatId, Barcode = "cleanup", Title = "Cleanup title",
            Status = status, CloseReason = status == "closed" ? "manual" : null,
            ClaimedByStaffUserId = target.Id, ClaimedByDisplayName = target.DisplayName, ClaimType = "manual", ClaimedAtUtc = now,
            CreatedUtc = now, UpdatedUtc = now
        };
        AdditionalCopyRequest Copy(string status) => new()
        {
            LibraryOrganizationId = 91907, BibId = "9001", Title = "Cleanup copy", Status = status,
            ClaimedByStaffUserId = target.Id, ClaimedByDisplayName = target.DisplayName, ClaimType = "manual", ClaimedAtUtc = now,
            CreatedUtc = now, UpdatedUtc = now, ClosedUtc = status == "closed" ? now : null, Notes = "Committed copy history."
        };
        var openTitle = Title("suggestion");
        var closedTitle = Title("closed");
        var openCopy = Copy("open");
        var closedCopy = Copy("closed");
        context.FormatAutoClaimRules.Add(rule);
        context.TitleRequests.AddRange(openTitle, closedTitle);
        context.AdditionalCopyRequests.AddRange(openCopy, closedCopy);
        await context.SaveChangesAsync();
        var service = factory.Services.GetRequiredService<StaffLifecycleService>();
        var input = new StaffRoleInput(StaffVersion.Encode(target.RowVersion), "admin", 91908);
        await context.Database.ExecuteSqlRawAsync("CREATE TRIGGER [asap].[TR_CorrectiveRejectLifecycleAudit] ON [asap].[AdministrativeAudit] AFTER INSERT AS THROW 51014, 'Corrective lifecycle rollback probe', 1;");
        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => service.ChangeRoleAsync(actor, target.Id, input, CancellationToken.None));
            await using var verify = await contextFactory.CreateDbContextAsync();
            Assert.IsTrue((await verify.FormatAutoClaimRules.SingleAsync(item => item.Id == rule.Id)).IsActive);
            Assert.AreEqual(target.Id, (await verify.TitleRequests.SingleAsync(item => item.Id == openTitle.Id)).ClaimedByStaffUserId);
            Assert.AreEqual(target.Id, (await verify.AdditionalCopyRequests.SingleAsync(item => item.Id == openCopy.Id)).ClaimedByStaffUserId);
            Assert.AreEqual("Committed copy history.", (await verify.AdditionalCopyRequests.SingleAsync(item => item.Id == openCopy.Id)).Notes);
            CollectionAssert.AreEqual(target.RowVersion, (await verify.StaffUsers.SingleAsync(item => item.Id == target.Id)).RowVersion);
            Assert.AreEqual(0, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == openTitle.Id));
            Assert.AreEqual(0, await verify.AdministrativeAudits.CountAsync(item => item.TargetId == target.Id.ToString() && item.Action == "staff_lifecycle_updated"));
        }
        finally { await context.Database.ExecuteSqlRawAsync("DROP TRIGGER [asap].[TR_CorrectiveRejectLifecycleAudit];"); }
        var result = await service.ChangeRoleAsync(actor, target.Id, input, CancellationToken.None);
        Assert.AreEqual("updated", result.Code);
        Assert.AreEqual(1, result.RulesDeactivated);
        Assert.AreEqual(1, result.OpenTitleClaimsCleared);
        Assert.AreEqual(1, result.OpenAdditionalCopyClaimsCleared);
        await using var completed = await contextFactory.CreateDbContextAsync();
        Assert.AreEqual(target.Id, (await completed.TitleRequests.SingleAsync(item => item.Id == closedTitle.Id)).ClaimedByStaffUserId);
        Assert.AreEqual(target.Id, (await completed.AdditionalCopyRequests.SingleAsync(item => item.Id == closedCopy.Id)).ClaimedByStaffUserId);
        Assert.AreEqual(1, await completed.TitleRequestEvents.CountAsync(item => item.TitleRequestId == openTitle.Id && item.EventType == "claim_cleared"));
        StringAssert.Contains((await completed.AdditionalCopyRequests.SingleAsync(item => item.Id == openCopy.Id)).Notes!, "Committed copy history.");
        StringAssert.Contains((await completed.AdditionalCopyRequests.SingleAsync(item => item.Id == openCopy.Id)).Notes!, "System cleared claim because the assignee's staff access changed.");
        Assert.AreEqual(1, await completed.AdministrativeAudits.CountAsync(item => item.TargetId == target.Id.ToString() && item.Action == "staff_lifecycle_updated"));
    }

    [TestMethod]
    public async Task EmptyImportedObjectIdentityCannotCountAsLastUsableSuperAdmin()
    {
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        var actor = await ReadConfiguredSuperAdminAsync();
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync();
        var otherAdmins = await context.StaffUsers.Where(item => item.Id != actor.Id && item.Role == "super_admin" && item.IsActive).ToListAsync();
        foreach (var other in otherAdmins) other.IsActive = false;
        var unusable = new StaffUser
        {
            EntraTenantId = actor.EntraTenantId, EntraObjectId = Guid.Empty, UserPrincipalName = "historical.empty@example.org",
            NormalizedUserPrincipalName = "HISTORICAL.EMPTY@EXAMPLE.ORG", Role = "super_admin", OrganizationId = 1, IsActive = true
        };
        context.StaffUsers.Add(unusable);
        await context.SaveChangesAsync();
        var service = factory.Services.GetRequiredService<StaffLifecycleService>();
        var eligibility = factory.Services.GetRequiredService<StaffEligibilityService>();
        var real = await context.StaffUsers.SingleAsync(item => item.Id == actor.Id);
        try
        {
            Assert.AreEqual("active_super_admin_required", (await service.ChangeRoleAsync(actor, actor.Id,
                new StaffRoleInput(StaffVersion.Encode(actor.RowVersion), "admin", 2), CancellationToken.None)).Code);
            Assert.AreEqual("active_super_admin_required", (await service.DeactivateAsync(actor, actor.Id,
                new StaffDeactivateInput(StaffVersion.Encode(actor.RowVersion)), CancellationToken.None)).Code);
            Assert.AreEqual(StaffEligibilityOutcome.InvalidIdentity, (await eligibility.EvaluateAsync(
                new StaffIdentityEvidence(unusable.Id, actor.EntraTenantId, Guid.Empty), null, StaffRoleRequirement.Any, true, CancellationToken.None)).Outcome);
            real.IsActive = false;
            await context.SaveChangesAsync();
            Assert.IsFalse(await eligibility.HasUsableSuperAdminAsync(CancellationToken.None));
        }
        finally
        {
            real.IsActive = true;
            foreach (var other in otherAdmins) other.IsActive = true;
            context.StaffUsers.Remove(unusable);
            await context.SaveChangesAsync();
        }
    }

    [TestMethod]
    public async Task StaffProfileRejectsParticipationLossBeforeCommitWithoutChangingPreferences()
    {
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync();
        var organization = new Organization { Id = 91906, DisplayName = "Profile participation", IsActive = true };
        context.Organizations.Add(organization);
        await context.SaveChangesAsync();
        var staff = await CreateCorrectiveStaffAsync(superAdmin, "staff", organization.Id);
        var actor = await ReadCorrectiveStaffAsync(staff);
        var profiles = factory.Services.GetRequiredService<StaffProfileService>();
        var input = new StaffProfileInput(StaffVersion.Encode(staff.RowVersion), true, "profile@example.org", true, true, true);
        organization.IsActive = false;
        await context.SaveChangesAsync();
        var result = await profiles.UpdateAsync(actor, input, CancellationToken.None);
        Assert.AreEqual("staff_scope_forbidden", result.Code);
        var unchanged = await context.StaffUsers.AsNoTracking().SingleAsync(item => item.Id == staff.Id);
        CollectionAssert.AreEqual(staff.RowVersion, unchanged.RowVersion, "Forbidden preference updates must not commit before reporting failure.");
        Assert.IsFalse(unchanged.WeeklyActionSummaryEnabled);
        organization.IsActive = true;
        await context.SaveChangesAsync();
        var saved = await profiles.UpdateAsync(actor, input, CancellationToken.None);
        Assert.AreEqual("updated", saved.Code);
        Assert.IsTrue(saved.Staff!.WeeklyActionSummaryEnabled);
        Assert.AreEqual("profile@example.org", saved.Staff.WeeklyActionSummaryEmail);
        Assert.AreEqual("stale_version", (await profiles.UpdateAsync(actor, input, CancellationToken.None)).Code);
    }

    [TestMethod]
    public async Task ForbiddenStaffCookieCanRecoverSessionSignOutAndChallengeInDevelopment()
    {
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using (var context = await contextFactory.CreateDbContextAsync())
        {
            context.Organizations.Add(new Organization { Id = 91903, DisplayName = "Cookie recovery", IsActive = true });
            await context.SaveChangesAsync();
        }
        var staff = await CreateCorrectiveStaffAsync(superAdmin, "staff", 91903);
        await using var development = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                // The fixture already deployed the real DACPAC to its isolated SQL database.
                services.Remove(services.Single(item => item.ServiceType == typeof(IHostedService) &&
                    item.ImplementationType == typeof(Asap.Web.Infrastructure.Development.DevelopmentDatabaseInitializer)));
                services.PostConfigure<OpenIdConnectOptions>(StaffAuthenticationRegistration.EntraScheme,
                    options => options.Configuration = new OpenIdConnectConfiguration
                    {
                        AuthorizationEndpoint = "https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize",
                        TokenEndpoint = "https://login.microsoftonline.com/organizations/oauth2/v2.0/token",
                        Issuer = $"https://login.microsoftonline.com/{superAdmin.EntraTenantId}/v2.0"
                    });
            });
        });
        using var client = development.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true
        });
        var protectedCookie = ProtectStaffCookie(development, staff.Id, staff.EntraTenantId!.Value, staff.EntraObjectId!.Value);
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-ASAP.Staff={protectedCookie}");
        using (var activeSession = await client.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, activeSession.StatusCode);
            using var body = JsonDocument.Parse(await activeSession.Content.ReadAsStringAsync());
            Assert.IsTrue(body.RootElement.GetProperty("authenticated").GetBoolean());
        }
        await using (var context = await contextFactory.CreateDbContextAsync())
        {
            var organization = await context.Organizations.SingleAsync(item => item.Id == 91903);
            organization.IsActive = false;
            await context.SaveChangesAsync();
        }
        foreach (var path in new[] { "/api/asap/staff/title-requests", "/api/asap/staff/additional-copies", "/api/asap/staff/users" })
        {
            using var denied = await client.GetAsync(path);
            Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode, path);
        }
        using var session = await client.GetAsync("/api/asap/staff/session");
        Assert.AreEqual(HttpStatusCode.OK, session.StatusCode, await session.Content.ReadAsStringAsync());
        using var sessionBody = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        Assert.IsTrue(sessionBody.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.IsFalse(sessionBody.RootElement.GetProperty("accessAllowed").GetBoolean());
        Assert.IsFalse(sessionBody.RootElement.TryGetProperty("staff", out _));
        using (var noAntiforgery = await client.PostAsync("/api/asap/staff/sign-out", null))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, noAntiforgery.StatusCode);
            Assert.IsFalse(noAntiforgery.Headers.TryGetValues("Set-Cookie", out var cookies) &&
                cookies.Any(value => value.StartsWith("__Host-ASAP.Staff=;", StringComparison.Ordinal)));
        }
        using (var stillAuthenticated = await client.GetAsync("/api/asap/staff/session"))
        {
            using var body = JsonDocument.Parse(await stillAuthenticated.Content.ReadAsStringAsync());
            Assert.IsTrue(body.RootElement.GetProperty("authenticated").GetBoolean());
        }
        using (var challenge = await client.GetAsync("/api/asap/staff/sign-in"))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, challenge.StatusCode);
            Assert.AreEqual("login.microsoftonline.com", challenge.Headers.Location!.Host);
            StringAssert.Contains(challenge.Headers.Location.Query, "response_type=code");
            Assert.AreEqual("select_account", Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(challenge.Headers.Location.Query)["prompt"].ToString());
        }
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", sessionBody.RootElement.GetProperty("antiforgeryToken").GetString());
        using var signedOut = await client.PostAsync("/api/asap/staff/sign-out", null);
        Assert.AreEqual(HttpStatusCode.OK, signedOut.StatusCode, await signedOut.Content.ReadAsStringAsync());
        Assert.IsTrue(signedOut.Headers.GetValues("Set-Cookie").Any(value => value.StartsWith("__Host-ASAP.Staff=;", StringComparison.Ordinal)));
        client.DefaultRequestHeaders.Remove("Cookie");
        using (var anonymous = await client.GetAsync("/api/asap/staff/session"))
        {
            using var body = JsonDocument.Parse(await anonymous.Content.ReadAsStringAsync());
            Assert.IsFalse(body.RootElement.GetProperty("authenticated").GetBoolean());
        }
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-ASAP.Staff={ProtectStaffCookie(development, superAdmin.Id, superAdmin.EntraTenantId, superAdmin.EntraObjectId)}");
        using var replacement = await client.GetAsync("/api/asap/staff/session");
        using var replacementBody = JsonDocument.Parse(await replacement.Content.ReadAsStringAsync());
        Assert.IsTrue(replacementBody.RootElement.GetProperty("accessAllowed").GetBoolean());
        Assert.AreEqual(superAdmin.Id.ToString(), replacementBody.RootElement.GetProperty("staff").GetProperty("id").GetString());
    }

    [TestMethod]
    public async Task StaffProvisioningAndRebindRequireDurableIdentityAndAtomicReadableLabel()
    {
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var service = factory.Services.GetRequiredService<StaffLifecycleService>();
        var valid = new StaffCreateInput(superAdmin.EntraTenantId.ToString(), Guid.NewGuid().ToString(),
            "identity@example.org", "Identity", "notify@example.org", "staff", 2);
        foreach (var invalid in new[]
                 {
                     valid with { TenantId = "" }, valid with { TenantId = Guid.Empty.ToString() },
                     valid with { TenantId = Guid.NewGuid().ToString() }, valid with { ObjectId = "" },
                     valid with { ObjectId = Guid.Empty.ToString() }, valid with { UserPrincipalName = "  " },
                     valid with { UserPrincipalName = null }
                 })
        {
            Assert.AreEqual("invalid_staff_user", (await service.CreateAsync(superAdmin, invalid, CancellationToken.None)).Code);
        }

        var admin = await CreateCorrectiveStaffAsync(superAdmin, "admin", 2);
        var actor = await ReadCorrectiveStaffAsync(admin);
        var target = await CreateCorrectiveStaffAsync(superAdmin, "staff", 2);
        var duplicate = await CreateCorrectiveStaffAsync(superAdmin, "staff", 2);
        var deactivated = await service.DeactivateAsync(superAdmin, duplicate.Id,
            new StaffDeactivateInput(StaffVersion.Encode(duplicate.RowVersion)), CancellationToken.None);
        Assert.AreEqual("updated", deactivated.Code);
        var input = new StaffRebindInput(StaffVersion.Encode(target.RowVersion), superAdmin.EntraTenantId.ToString(),
            Guid.NewGuid().ToString(), " Rebound.User@example.org ", true, "Correct reviewed directory identity");
        foreach (var invalid in new[]
                 {
                     input with { TenantId = "" }, input with { TenantId = Guid.Empty.ToString() },
                     input with { TenantId = Guid.NewGuid().ToString() }, input with { ObjectId = "" },
                     input with { ObjectId = Guid.Empty.ToString() }, input with { UserPrincipalName = " " },
                     input with { UserPrincipalName = null }, input with { Confirmed = false }, input with { Reason = " " }
                 })
        {
            Assert.AreEqual("rebind_not_confirmed", (await service.RebindAsync(actor, target.Id, invalid, CancellationToken.None)).Code);
        }
        Assert.AreEqual("identity_already_exists", (await service.RebindAsync(actor, target.Id,
            input with { ObjectId = duplicate.EntraObjectId.ToString() }, CancellationToken.None)).Code);
        Assert.AreEqual("staff_scope_forbidden", (await service.RebindAsync(actor, superAdmin.Id,
            input with { Version = StaffVersion.Encode(superAdmin.RowVersion) }, CancellationToken.None)).Code);

        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using (var context = await contextFactory.CreateDbContextAsync())
        {
            context.Organizations.Add(new Organization { Id = 91902, DisplayName = "Other identity library", IsActive = true });
            await context.SaveChangesAsync();
        }
        var foreign = await CreateCorrectiveStaffAsync(superAdmin, "admin", 91902);
        Assert.AreEqual("staff_scope_forbidden", (await service.RebindAsync(actor, foreign.Id,
            input with { Version = StaffVersion.Encode(foreign.RowVersion) }, CancellationToken.None)).Code);
        var rebound = await service.RebindAsync(actor, target.Id, input, CancellationToken.None);
        Assert.AreEqual("updated", rebound.Code);
        Assert.AreEqual("Rebound.User@example.org", rebound.User!.UserPrincipalName);
        Assert.AreEqual("REBOUND.USER@EXAMPLE.ORG", rebound.User.NormalizedUserPrincipalName);
        Assert.AreEqual(Guid.Parse(input.ObjectId!), rebound.User.EntraObjectId);
        Assert.AreEqual(target.NotificationEmail, rebound.User.NotificationEmail);
        Assert.AreEqual("stale_version", (await service.RebindAsync(actor, target.Id, input, CancellationToken.None)).Code);
        var oldBinding = await factory.Services.GetRequiredService<StaffEligibilityService>().EvaluateAsync(
            new StaffIdentityEvidence(target.Id, target.EntraTenantId!.Value, target.EntraObjectId!.Value), null,
            StaffRoleRequirement.Any, true, CancellationToken.None);
        Assert.AreEqual(StaffEligibilityOutcome.InvalidIdentity, oldBinding.Outcome);
        Assert.AreEqual("updated", (await service.RebindAsync(superAdmin, foreign.Id,
            input with { Version = StaffVersion.Encode(foreign.RowVersion), ObjectId = Guid.NewGuid().ToString() }, CancellationToken.None)).Code);
        var adminTarget = await service.RebindAsync(actor, admin.Id,
            input with { Version = StaffVersion.Encode(admin.RowVersion), ObjectId = Guid.NewGuid().ToString() }, CancellationToken.None);
        Assert.AreEqual("updated", adminTarget.Code, "An administrator may rebind an own-library administrator, including itself.");
        await using var verify = await contextFactory.CreateDbContextAsync();
        var persisted = await verify.StaffUsers.SingleAsync(item => item.Id == target.Id);
        Assert.AreEqual(rebound.User.EntraObjectId, persisted.EntraObjectId);
        Assert.AreEqual(rebound.User.NormalizedUserPrincipalName, persisted.NormalizedUserPrincipalName);
        Assert.AreEqual(target.NotificationEmail, persisted.NotificationEmail);
        Assert.AreEqual(1, await verify.AdministrativeAudits.CountAsync(item => item.TargetType == "StaffUser" &&
            item.TargetId == target.Id.ToString() && item.Action == "staff_identity_rebound"));
    }

    [TestMethod]
    public async Task StaffRoleEditsAuthorizeDestinationAndPreserveInactiveState()
    {
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var service = factory.Services.GetRequiredService<StaffLifecycleService>();
        var admin = await CreateCorrectiveStaffAsync(superAdmin, "admin", 2);
        var actor = await ReadCorrectiveStaffAsync(admin);
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using (var context = await contextFactory.CreateDbContextAsync())
        {
            context.Organizations.Add(new Organization { Id = 91901, DisplayName = "Corrective destination", IsActive = true });
            await context.SaveChangesAsync();
        }

        foreach (var role in new[] { "staff", "admin" })
        {
            var target = await CreateCorrectiveStaffAsync(superAdmin, role, 2);
            var denied = await service.ChangeRoleAsync(actor, target.Id,
                new StaffRoleInput(StaffVersion.Encode(target.RowVersion), role, 91901), CancellationToken.None);
            Assert.AreEqual("staff_scope_forbidden", denied.Code, $"Admin cannot move {role} to another library.");
            var changed = await service.ChangeRoleAsync(actor, target.Id,
                new StaffRoleInput(StaffVersion.Encode(target.RowVersion), role == "staff" ? "admin" : "staff", 2), CancellationToken.None);
            Assert.AreEqual("updated", changed.Code);
            var moved = await service.ChangeRoleAsync(superAdmin, target.Id,
                new StaffRoleInput(StaffVersion.Encode(changed.User!.RowVersion), role, 91901), CancellationToken.None);
            Assert.AreEqual("updated", moved.Code);
            var deactivated = await service.DeactivateAsync(superAdmin, target.Id,
                new StaffDeactivateInput(StaffVersion.Encode(moved.User!.RowVersion)), CancellationToken.None);
            Assert.AreEqual("updated", deactivated.Code);
            var inactiveEdit = await service.ChangeRoleAsync(superAdmin, target.Id,
                new StaffRoleInput(StaffVersion.Encode(deactivated.User!.RowVersion), "admin", 2), CancellationToken.None);
            Assert.AreEqual("updated", inactiveEdit.Code);
            Assert.IsFalse(inactiveEdit.User!.IsActive, "Editing role and library must not reactivate a historical account.");

            var readded = await service.CreateAsync(actor, new StaffCreateInput(
                target.EntraTenantId.ToString(), target.EntraObjectId.ToString(), " Readded@example.org ",
                "Readded", target.NotificationEmail, "staff", 2), CancellationToken.None);
            Assert.AreEqual("created", readded.Code);
            Assert.AreEqual(target.Id, readded.User!.Id);
            Assert.IsTrue(readded.User.IsActive);
            Assert.AreEqual("Readded@example.org", readded.User.UserPrincipalName);
            Assert.AreEqual("READDED@EXAMPLE.ORG", readded.User.NormalizedUserPrincipalName);
        }
    }

    private async Task<StaffUser> CreateCorrectiveStaffAsync(CurrentStaff actor, string role, int organizationId)
    {
        var result = await factory!.Services.GetRequiredService<StaffLifecycleService>().CreateAsync(actor,
            new StaffCreateInput(actor.EntraTenantId.ToString(), Guid.NewGuid().ToString(), " Corrective@example.org ",
                "Corrective staff", "notify@example.org", role, organizationId), CancellationToken.None);
        Assert.AreEqual("created", result.Code);
        Assert.AreEqual("Corrective@example.org", result.User!.UserPrincipalName);
        Assert.AreEqual("CORRECTIVE@EXAMPLE.ORG", result.User.NormalizedUserPrincipalName);
        return result.User;
    }

    private async Task<CurrentStaff> ReadCorrectiveStaffAsync(StaffUser row)
    {
        var result = await factory!.Services.GetRequiredService<StaffEligibilityService>().EvaluateAsync(
            new StaffIdentityEvidence(row.Id, row.EntraTenantId!.Value, row.EntraObjectId!.Value),
            null, StaffRoleRequirement.Any, true, CancellationToken.None);
        Assert.AreEqual(StaffEligibilityOutcome.Allowed, result.Outcome);
        return result.Staff!;
    }
}
