using System.Net;
using System.Text.Json;
using Asap.Web.Features.Administration;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task SuccessfulSignInUpdatesOidMetadataWithoutChangingAuthenticationOrNotificationEmail()
    {
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        var actor = await ReadConfiguredSuperAdminAsync();
        var target = await CreateCorrectiveStaffAsync(actor, "staff", 2);
        var newObjectId = Guid.NewGuid();
        var signIn = factory.Services.GetRequiredService<StaffSignInService>();
        await signIn.RecordSuccessfulSignInAsync(
            target.Id,
            target.NormalizedUserPrincipalName!,
            actor.EntraTenantId,
            newObjectId,
            "Current identity",
            CancellationToken.None);
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync();
        var persisted = await context.StaffUsers.SingleAsync(item => item.Id == target.Id);
        Assert.AreEqual(newObjectId, persisted.EntraObjectId);
        Assert.AreEqual(actor.EntraTenantId, persisted.EntraTenantId);
        Assert.AreEqual(target.UserPrincipalName, persisted.UserPrincipalName);
        Assert.AreEqual(target.NormalizedUserPrincipalName, persisted.NormalizedUserPrincipalName);
        Assert.AreEqual("Current identity", persisted.DisplayName);
        Assert.AreEqual(target.NotificationEmail, persisted.NotificationEmail);
        Assert.IsNotNull(persisted.LastLoginUtc);

        var laterObjectId = Guid.NewGuid();
        await signIn.RecordSuccessfulSignInAsync(
            target.Id,
            target.NormalizedUserPrincipalName!,
            actor.EntraTenantId,
            laterObjectId,
            null,
            CancellationToken.None);
        await context.Entry(persisted).ReloadAsync();
        Assert.AreEqual(laterObjectId, persisted.EntraObjectId);
        Assert.AreEqual(target.NormalizedUserPrincipalName, persisted.NormalizedUserPrincipalName);
    }

    [TestMethod]
    public async Task NeverSignedInStaffCanReceiveExplicitAndAutomaticClaims()
    {
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        var actor = await ReadConfiguredSuperAdminAsync();
        var lifecycle = factory.Services.GetRequiredService<StaffLifecycleService>();
        var target = await CreateCorrectiveStaffAsync(actor, "staff", 2);
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync();
        var historical = await context.StaffUsers.SingleAsync(item => item.Id == target.Id);
        Assert.IsNull(historical.EntraTenantId);
        Assert.IsNull(historical.EntraObjectId);
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
            Assert.IsTrue((await lifecycle.ListAssignmentCandidatesAsync(2, CancellationToken.None)).Any(item => item.Id == historical.Id));
            Assert.AreEqual("updated", (await factory.Services.GetRequiredService<TitleRequestMutationService>().AssignAsync(actor, title.Id,
                new AssignTitleRequestInput(StaffVersion.Encode(title.RowVersion), historical.Id), CancellationToken.None)).Code);
            Assert.AreEqual("updated", (await factory.Services.GetRequiredService<AdditionalCopyService>().AssignAsync(actor, copy.Id,
                new AssignAdditionalCopyInput(StaffVersion.Encode(copy.RowVersion), historical.Id), CancellationToken.None)).Code);
            var administration = factory.Services.GetRequiredService<AdministrationService>();
            var settings = JsonSerializer.SerializeToElement((await administration.GetSettingsAsync(actor, "2", CancellationToken.None)).Data);
            Assert.IsTrue(settings.GetProperty("autoClaimStaff").EnumerateArray().Any(item => item.GetProperty("id").GetString() == historical.Id.ToString()));
            await context.Entry(title).ReloadAsync();
            await context.Entry(copy).ReloadAsync();
            Assert.AreEqual(historical.Id, title.ClaimedByStaffUserId);
            Assert.AreEqual(historical.Id, copy.ClaimedByStaffUserId);

            foreach (var rule in previousRules) rule.IsActive = false;
            await context.SaveChangesAsync();
            historicalRule = new FormatAutoClaimRule
            {
                LibraryOrganizationId = 2, MaterialFormatId = format.Id, StaffUserId = historical.Id, IsActive = true, CreatedUtc = now
            };
            context.FormatAutoClaimRules.Add(historicalRule);
            await context.SaveChangesAsync();
            var result = await CreatePatronSuggestionService(["example.org"], new RecordingOutboxDispatcher(), new RecordingEmailSender()).CreateAsync(
                new PatronSessionContext(9092, "20000000003921", 2, 2, 2, DateTime.UtcNow.AddHours(1)),
                Suggestion($"Historical auto claim {Guid.NewGuid():N}"), CancellationToken.None);
            var submitted = await context.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == result.Id);
            Assert.AreEqual(historical.Id, submitted.ClaimedByStaffUserId);
            Assert.AreEqual(historicalRule.Id, submitted.ClaimRuleId);
        }
        finally
        {
            if (historicalRule is not null)
            {
                await context.TitleRequests
                    .Where(item => item.ClaimRuleId == historicalRule.Id)
                    .ExecuteDeleteAsync();
                context.FormatAutoClaimRules.Remove(historicalRule);
            }
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
            ClaimedByStaffUserId = target.Id, ClaimedByDisplayName = target.DisplayName ?? target.UserPrincipalName,
            ClaimType = "manual", ClaimedAtUtc = now,
            CreatedUtc = now, UpdatedUtc = now
        };
        AdditionalCopyRequest Copy(string status) => new()
        {
            LibraryOrganizationId = 91907, BibId = "9001", Title = "Cleanup copy", Status = status,
            ClaimedByStaffUserId = target.Id, ClaimedByDisplayName = target.DisplayName ?? target.UserPrincipalName,
            ClaimType = "manual", ClaimedAtUtc = now,
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
    public async Task EmailOnlySuperAdminCountsAsUsableAndCanAuthenticate()
    {
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        var actor = await ReadConfiguredSuperAdminAsync();
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync();
        var otherAdmins = await context.StaffUsers.Where(item => item.Id != actor.Id && item.Role == "super_admin" && item.IsActive).ToListAsync();
        foreach (var other in otherAdmins) other.IsActive = false;
        var emailOnly = new StaffUser
        {
            UserPrincipalName = "historical.email@example.org",
            NormalizedUserPrincipalName = "HISTORICAL.EMAIL@EXAMPLE.ORG",
            Role = "super_admin",
            OrganizationId = 1,
            IsActive = true
        };
        context.StaffUsers.Add(emailOnly);
        await context.SaveChangesAsync();
        var service = factory.Services.GetRequiredService<StaffLifecycleService>();
        var eligibility = factory.Services.GetRequiredService<StaffEligibilityService>();
        var real = await context.StaffUsers.SingleAsync(item => item.Id == actor.Id);
        try
        {
            Assert.AreEqual(StaffEligibilityOutcome.Allowed, (await eligibility.EvaluateAsync(
                new StaffIdentityEvidence(emailOnly.Id, emailOnly.NormalizedUserPrincipalName!, actor.EntraTenantId),
                null, StaffRoleRequirement.Any, true, CancellationToken.None)).Outcome);
            real.IsActive = false;
            await context.SaveChangesAsync();
            Assert.IsTrue(await eligibility.HasUsableSuperAdminAsync(CancellationToken.None));
        }
        finally
        {
            real.IsActive = true;
            foreach (var other in otherAdmins) other.IsActive = true;
            context.StaffUsers.Remove(emailOnly);
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
    public async Task ForbiddenStaffCookieCanRecoverSessionSignOutAndChallengeWithRealCookieAuthentication()
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
        await using var cookieApplication = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Keep Testing for CI's SQL connection, but exercise the real secure cookie/antiforgery/OIDC boundary.
                services.PostConfigure<AuthenticationOptions>(options =>
                    options.DefaultAuthenticateScheme = StaffAuthenticationRegistration.CookieScheme);
                services.PostConfigure<AntiforgeryOptions>(options =>
                {
                    options.Cookie.Name = "__Host-ASAP-AF";
                    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                });
                services.PostConfigure<OpenIdConnectOptions>(StaffAuthenticationRegistration.EntraScheme,
                    options => options.Configuration = new OpenIdConnectConfiguration
                    {
                        AuthorizationEndpoint = "https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize",
                        TokenEndpoint = "https://login.microsoftonline.com/organizations/oauth2/v2.0/token",
                        Issuer = $"https://login.microsoftonline.com/{superAdmin.EntraTenantId}/v2.0"
                    });
            });
        });
        using var client = cookieApplication.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true
        });
        Assert.AreEqual("Testing", cookieApplication.Services.GetRequiredService<IWebHostEnvironment>().EnvironmentName,
            "The real-SQL fixture must retain its trusted Testing host allowance on every platform.");
        var schemes = cookieApplication.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        var authenticate = await schemes.GetDefaultAuthenticateSchemeAsync();
        Assert.AreEqual(StaffAuthenticationRegistration.CookieScheme, authenticate!.Name);
        Assert.AreEqual(typeof(CookieAuthenticationHandler), authenticate.HandlerType);
        var challengeScheme = await schemes.GetDefaultChallengeSchemeAsync();
        Assert.AreEqual(StaffAuthenticationRegistration.EntraScheme, challengeScheme!.Name);
        Assert.AreEqual(typeof(OpenIdConnectHandler), challengeScheme.HandlerType);
        var protectedCookie = ProtectStaffCookie(cookieApplication, staff.Id, superAdmin.EntraTenantId, staff.NormalizedUserPrincipalName!);
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-ASAP.Staff={protectedCookie}");
        Assert.IsFalse(client.DefaultRequestHeaders.Any(header => header.Key.StartsWith("X-ASAP-Test-", StringComparison.OrdinalIgnoreCase)),
            "This journey must authenticate only the protected cookie, never a testing identity header.");
        using (var activeSession = await client.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, activeSession.StatusCode);
            var antiforgeryCookie = activeSession.Headers.GetValues("Set-Cookie")
                .Single(value => value.StartsWith("__Host-ASAP-AF=", StringComparison.Ordinal)).ToLowerInvariant();
            StringAssert.Contains(antiforgeryCookie, "; secure");
            StringAssert.Contains(antiforgeryCookie, "; path=/");
            StringAssert.Contains(antiforgeryCookie, "; httponly");
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
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(challenge.Headers.Location.Query);
            Assert.AreEqual(OpenIdConnectResponseType.IdToken, query["response_type"].ToString());
            Assert.AreEqual(OpenIdConnectResponseMode.FormPost, query["response_mode"].ToString());
            CollectionAssert.AreEquivalent(
                new[] { OpenIdConnectScope.OpenId, OpenIdConnectScope.Profile, OpenIdConnectScope.Email },
                query["scope"].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
            Assert.AreEqual("select_account", query["prompt"].ToString());
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
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-ASAP.Staff={ProtectStaffCookie(cookieApplication, superAdmin.Id, superAdmin.EntraTenantId, superAdmin.AuthenticationEmail)}");
        using var replacement = await client.GetAsync("/api/asap/staff/session");
        using var replacementBody = JsonDocument.Parse(await replacement.Content.ReadAsStringAsync());
        Assert.IsTrue(replacementBody.RootElement.GetProperty("accessAllowed").GetBoolean());
        Assert.AreEqual(superAdmin.Id.ToString(), replacementBody.RootElement.GetProperty("staff").GetProperty("id").GetString());
    }

    [TestMethod]
    public async Task StaffProvisioningUsesUniqueEmailAndReactivationPreservesProfileState()
    {
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var service = factory.Services.GetRequiredService<StaffLifecycleService>();
        var valid = new StaffCreateInput("identity@example.org", "staff", 2);
        foreach (var invalid in new[]
                 {
                     valid with { Email = "" },
                     valid with { Email = "not-an-email" },
                     valid with { Email = "legacy:1" },
                     valid with { Email = null }
                 })
        {
            Assert.AreEqual("invalid_staff_user", (await service.CreateAsync(superAdmin, invalid, CancellationToken.None)).Code);
        }

        var admin = await CreateCorrectiveStaffAsync(superAdmin, "admin", 2);
        var actor = await ReadCorrectiveStaffAsync(admin);
        var target = await CreateCorrectiveStaffAsync(superAdmin, "staff", 2);
        Assert.IsNull(target.EntraTenantId);
        Assert.IsNull(target.EntraObjectId);
        Assert.IsNull(target.DisplayName);
        Assert.AreEqual(target.UserPrincipalName, target.NotificationEmail);
        Assert.AreEqual("identity_already_exists", (await service.CreateAsync(
            superAdmin,
            new StaffCreateInput(target.UserPrincipalName!.ToUpperInvariant(), "staff", 2),
            CancellationToken.None)).Code);

        var profile = await factory.Services.GetRequiredService<StaffProfileService>().UpdateAsync(
            await ReadCorrectiveStaffAsync(target),
            new StaffProfileInput(StaffVersion.Encode(target.RowVersion), true, "weekly@example.org", true, true, false),
            CancellationToken.None);
        Assert.AreEqual("updated", profile.Code);
        var deactivated = await service.DeactivateAsync(
            superAdmin,
            target.Id,
            new StaffDeactivateInput(StaffVersion.Encode(profile.Staff!.RowVersion)),
            CancellationToken.None);
        Assert.AreEqual("updated", deactivated.Code);
        var reactivated = await service.CreateAsync(
            actor,
            new StaffCreateInput(target.UserPrincipalName, "staff", 2),
            CancellationToken.None);
        Assert.AreEqual("created", reactivated.Code);
        Assert.AreEqual(target.Id, reactivated.User!.Id);
        Assert.IsTrue(reactivated.User.WeeklyActionSummaryEnabled);
        Assert.AreEqual("weekly@example.org", reactivated.User.WeeklyActionSummaryEmail);

        var changedEmail = $"changed.{Guid.NewGuid():N}@example.org";
        var changed = await service.UpdateMetadataAsync(
            actor,
            target.Id,
            new StaffMetadataInput(StaffVersion.Encode(reactivated.User.RowVersion), changedEmail, "Readable", "notify@example.org"),
            CancellationToken.None);
        Assert.AreEqual("updated", changed.Code);
        Assert.AreEqual(changedEmail.ToUpperInvariant(), changed.User!.NormalizedUserPrincipalName);
        Assert.AreEqual("notify@example.org", changed.User.NotificationEmail);
        Assert.AreEqual("identity_already_exists", (await service.UpdateMetadataAsync(
            actor,
            target.Id,
            new StaffMetadataInput(StaffVersion.Encode(changed.User.RowVersion), admin.UserPrincipalName, null, null),
            CancellationToken.None)).Code);
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
                target.UserPrincipalName, "staff", 2), CancellationToken.None);
            Assert.AreEqual("created", readded.Code);
            Assert.AreEqual(target.Id, readded.User!.Id);
            Assert.IsTrue(readded.User.IsActive);
            Assert.AreEqual(target.UserPrincipalName, readded.User.UserPrincipalName);
            Assert.AreEqual(target.NormalizedUserPrincipalName, readded.User.NormalizedUserPrincipalName);
        }
    }

    private async Task<StaffUser> CreateCorrectiveStaffAsync(CurrentStaff actor, string role, int organizationId)
    {
        var email = $"corrective.{Guid.NewGuid():N}@example.org";
        var result = await factory!.Services.GetRequiredService<StaffLifecycleService>().CreateAsync(actor,
            new StaffCreateInput($" {email} ", role, organizationId), CancellationToken.None);
        Assert.AreEqual("created", result.Code);
        Assert.AreEqual(email, result.User!.UserPrincipalName);
        Assert.AreEqual(email.ToUpperInvariant(), result.User.NormalizedUserPrincipalName);
        return result.User;
    }

    private async Task<CurrentStaff> ReadCorrectiveStaffAsync(StaffUser row)
    {
        var tenantId = Guid.Parse(factory!.Services.GetRequiredService<Asap.Web.Infrastructure.Configuration.ExternalConfiguration>()
            .Authentication.Entra.AllowedTenantIds![0]);
        var result = await factory!.Services.GetRequiredService<StaffEligibilityService>().EvaluateAsync(
            new StaffIdentityEvidence(row.Id, row.NormalizedUserPrincipalName!, tenantId),
            null, StaffRoleRequirement.Any, true, CancellationToken.None);
        Assert.AreEqual(StaffEligibilityOutcome.Allowed, result.Outcome);
        return result.Staff!;
    }
}
