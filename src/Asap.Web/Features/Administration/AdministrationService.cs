using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asap.Shared;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Asap.Web.Features.Administration;

public sealed record AdministrationResult(string Code, object? Data = null, string? Message = null);

public sealed record OrganizationSummary(
    int Id,
    string Name,
    string? Abbreviation,
    bool IsActive,
    DateTime? LastSyncedUtc,
    string Version);

public sealed record AdministrativeAuditEntry(
    long Id,
    long? ActorStaffUserId,
    string? ActorName,
    int? OrganizationId,
    string Action,
    string? TargetType,
    string? TargetId,
    string? DetailsJson,
    DateTime CreatedUtc);

public sealed class AdministrationService(
    IDbContextFactory<AsapDbContext> contextFactory,
    PatronConfigurationService patronConfiguration,
    StaffEligibilityService staffEligibility,
    IntegrationCredentialProtector credentialProtector,
    IPolarisReferenceProvider polarisProvider,
    TimeProvider timeProvider)
{
    private static readonly string[] WorkflowTextFields =
    [
        "suggestionLimitMessage", "commonAuthorsLabel", "commonAuthorsHelp", "commonAuthorsMessage",
        "patronCodeEligibilityMessage"
    ];

    private static readonly string[] WorkflowBoolFields =
    [
        "outstandingTimeoutEnabled", "outstandingTimeoutSendEmail", "holdPickupTimeoutEnabled",
        "pendingHoldTimeoutEnabled", "additionalCopyTimeoutEnabled", "autoPromote", "commonAuthorsEnabled",
        "allowPatronAutoholdOptOut", "allowAnyRegisteredCardLogin", "patronCodeEligibilityEnabled"
    ];

    private static readonly string[] WorkflowIntFields =
    [
        "suggestionLimit", "outstandingTimeoutDays", "holdPickupTimeoutDays", "pendingHoldTimeoutDays",
        "additionalCopyTimeoutDays"
    ];

    private static readonly IReadOnlyDictionary<string, string> PatronTextColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pageTitle"] = nameof(PatronSettings.PageTitle),
            ["barcodeLabel"] = nameof(PatronSettings.BarcodeLabel),
            ["pinLabel"] = nameof(PatronSettings.PinLabel),
            ["loginPrompt"] = nameof(PatronSettings.LoginPrompt),
            ["loginNote"] = nameof(PatronSettings.LoginNote),
            ["suggestionFormNote"] = nameof(PatronSettings.SuggestionFormNote),
            ["noEmailMessage"] = nameof(PatronSettings.NoEmailMessage),
            ["successTitle"] = nameof(PatronSettings.SuccessTitle),
            ["successMessage"] = nameof(PatronSettings.SuccessMessage),
            ["alreadySubmittedMessage"] = nameof(PatronSettings.AlreadySubmittedMessage),
            ["ebookMessage"] = nameof(PatronSettings.EbookMessage),
            ["eaudiobookMessage"] = nameof(PatronSettings.EaudiobookMessage),
            ["suggestionStatusLabel"] = nameof(PatronSettings.SuggestionStatusLabel),
            ["outstandingPurchaseStatusLabel"] = nameof(PatronSettings.OutstandingPurchaseStatusLabel),
            ["pendingHoldStatusLabel"] = nameof(PatronSettings.PendingHoldStatusLabel),
            ["holdPlacedStatusLabel"] = nameof(PatronSettings.HoldPlacedStatusLabel),
            ["closedStatusLabel"] = nameof(PatronSettings.ClosedStatusLabel),
            ["rejectedStatusLabel"] = nameof(PatronSettings.RejectedStatusLabel),
            ["holdCompletedStatusLabel"] = nameof(PatronSettings.HoldCompletedStatusLabel),
            ["holdNotPickedUpStatusLabel"] = nameof(PatronSettings.HoldNotPickedUpStatusLabel),
            ["manualStatusLabel"] = nameof(PatronSettings.ManualStatusLabel),
            ["silentStatusLabel"] = nameof(PatronSettings.SilentStatusLabel)
        };

    private static readonly IReadOnlyDictionary<string, string> DuplicateLabelFields =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["suggestion"] = nameof(PatronSettings.SuggestionStatusLabel),
            ["outstanding_purchase"] = nameof(PatronSettings.OutstandingPurchaseStatusLabel),
            ["pending_hold"] = nameof(PatronSettings.PendingHoldStatusLabel),
            ["hold_placed"] = nameof(PatronSettings.HoldPlacedStatusLabel),
            ["closed"] = nameof(PatronSettings.ClosedStatusLabel),
            ["rejected"] = nameof(PatronSettings.RejectedStatusLabel),
            ["hold_completed"] = nameof(PatronSettings.HoldCompletedStatusLabel),
            ["hold_not_picked_up"] = nameof(PatronSettings.HoldNotPickedUpStatusLabel),
            ["manual"] = nameof(PatronSettings.ManualStatusLabel),
            ["silent"] = nameof(PatronSettings.SilentStatusLabel)
        };

    public async Task<AdministrationResult> GetSettingsAsync(
        CurrentStaff actor,
        string? requestedOrganization,
        CancellationToken cancellationToken)
    {
        if (!TryResolveScope(actor, requestedOrganization, out var organizationId, out var failure))
        {
            return failure;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            cancellationToken);
        var lockedOrganizations = await LockOrganizationsAsync(
            context,
            [1, organizationId],
            cancellationToken);
        if (!lockedOrganizations.TryGetValue(organizationId, out var organization))
        {
            return new AdministrationResult("organization_not_found");
        }

        var systemSettings = await context.SystemSettings.AsNoTracking()
            .SingleAsync(item => item.OrganizationId == 1, cancellationToken);
        var systemPolaris = await context.PolarisSettings.AsNoTracking()
            .SingleAsync(item => item.OrganizationId == 1, cancellationToken);
        var systemWorkflow = await context.WorkflowSettings.AsNoTracking()
            .SingleAsync(item => item.OrganizationId == 1, cancellationToken);
        var systemPatron = await context.PatronSettings.AsNoTracking()
            .SingleAsync(item => item.OrganizationId == 1, cancellationToken);
        var systemEmail = await context.EmailSettings.AsNoTracking()
            .SingleAsync(item => item.OrganizationId == 1, cancellationToken);
        var libraryWorkflow = organizationId == 1
            ? null
            : await context.WorkflowSettings.AsNoTracking().SingleOrDefaultAsync(
                item => item.OrganizationId == organizationId, cancellationToken);
        var libraryPatron = organizationId == 1
            ? null
            : await context.PatronSettings.AsNoTracking().SingleOrDefaultAsync(
                item => item.OrganizationId == organizationId, cancellationToken);
        var libraryEmail = organizationId == 1
            ? null
            : await context.EmailSettings.AsNoTracking().SingleOrDefaultAsync(
                item => item.OrganizationId == organizationId, cancellationToken);
        var effective = await patronConfiguration.GetAsync(context, organizationId, cancellationToken);
        if (effective is null)
        {
            return new AdministrationResult("organization_not_found");
        }

        var origins = await context.PatronEmbedAllowedOrigins.AsNoTracking()
            .Where(item => item.OrganizationId == 1)
            .OrderBy(item => item.NormalizedOrigin)
            .Select(item => item.Origin)
            .ToListAsync(cancellationToken);
        var providers = await LoadProvidersAsync(context, organizationId, cancellationToken);
        var formats = await LoadFormatsAsync(context, organizationId, cancellationToken);
        var customFields = organizationId == 1
            ? []
            : await LoadCustomFieldsAsync(context, organizationId, cancellationToken);
        var templates = await LoadTemplatesAsync(context, organizationId, cancellationToken);
        var publicationOptions = await LoadPublicationOptionsAsync(context, organizationId, cancellationToken);
        var commonCreators = await LoadCommonCreatorsAsync(context, organizationId, cancellationToken);
        var patronCodes = await LoadPatronCodesAsync(context, organizationId, cancellationToken);
        var autoClaimRules = organizationId == 1
            ? []
            : await LoadAutoClaimRulesAsync(context, organizationId, cancellationToken);
        var autoClaimStaff = organizationId == 1
            ? Array.Empty<object>()
            : (await context.StaffUsers.AsNoTracking()
                .Where(item => item.IsActive &&
                    (((item.Role == "staff" || item.Role == "admin") && item.OrganizationId == organizationId) ||
                     (item.Role == "super_admin" && item.OrganizationId == 1)))
                .OrderBy(item => item.DisplayName)
                .ThenBy(item => item.UserPrincipalName)
                .ToListAsync(cancellationToken))
                .Where(item => staffEligibility.IsAssignmentEligible(item, organizationId))
                .Select(item => (object)new
                {
                    id = item.Id.ToString(),
                    label = item.DisplayName ?? item.UserPrincipalName ?? $"Staff {item.Id}"
                })
                .ToArray();
        var branding = await context.Branding.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        var systemBranding = organizationId == 1
            ? branding
            : await context.Branding.AsNoTracking()
                .SingleOrDefaultAsync(item => item.OrganizationId == 1, cancellationToken);
        var hasOverrides = organizationId != 1 && await HasLibraryOverridesAsync(context, organizationId, cancellationToken);
        var version = await ComputeSettingsVersionAsync(context, organizationId, cancellationToken);

        var configuredSystem = new
        {
            workflow = ToWorkflow(systemWorkflow),
            patron = ToPatron(systemPatron),
            email = ToEmail(systemEmail),
            publicationOptions = await LoadPublicationSnapshotAsync(context, 1, cancellationToken),
            commonCreators = await LoadCommonCreatorSnapshotAsync(context, 1, cancellationToken),
            allowedPatronCodeIds = await LoadPatronCodeSnapshotAsync(context, 1, cancellationToken),
            providers = await LoadRawProviderOverridesAsync(context, 1, cancellationToken),
            formats = await LoadRawFormatsAsync(context, 1, cancellationToken),
            templates = await LoadRawTemplatesAsync(context, 1, cancellationToken),
            branding = ToBranding(systemBranding)
        };
        var libraryOverride = organizationId == 1
            ? null
            : new
            {
                workflow = libraryWorkflow is null ? null : ToWorkflow(libraryWorkflow),
                patron = libraryPatron is null ? null : ToPatron(libraryPatron),
                email = libraryEmail is null ? null : ToEmail(libraryEmail),
                publicationOptions = await LoadPublicationSnapshotAsync(context, organizationId, cancellationToken),
                commonCreators = await LoadCommonCreatorSnapshotAsync(context, organizationId, cancellationToken),
                allowedPatronCodeIds = await LoadPatronCodeSnapshotAsync(context, organizationId, cancellationToken),
                providers = await LoadRawProviderOverridesAsync(context, organizationId, cancellationToken),
                formats = await LoadRawFormatsAsync(context, organizationId, cancellationToken),
                templates = await LoadRawTemplatesAsync(context, organizationId, cancellationToken),
                branding = ToBranding(branding)
            };
        var stored = new
        {
            systemSettings = ToSystemSettings(systemSettings, origins),
            polaris = ToPolarisSettings(systemPolaris),
            configuredSystem,
            libraryOverride,
            workflow = ToWorkflow(libraryWorkflow ?? systemWorkflow),
            patron = ToPatron(libraryPatron ?? systemPatron),
            email = ToEmail(libraryEmail ?? systemEmail),
            origins,
            publicationOptions,
            commonCreators,
            allowedPatronCodeIds = patronCodes,
            providers,
            formats,
            customFields,
            templates,
            autoClaimRules,
            branding = ToBranding(branding)
        };

        var effectiveDto = ToEffectiveConfiguration(effective, systemWorkflow, libraryWorkflow);
        var result = new AdministrationResult(
            "ok",
            new
            {
                orgId = organizationId == 1 ? "system" : organizationId.ToString(),
                organization = new
                {
                    id = organization.Id,
                    name = organization.DisplayName,
                    abbreviation = organization.Abbreviation,
                    active = organization.IsActive,
                    version = StaffVersion.Encode(organization.RowVersion)
                },
                isOverride = hasOverrides,
                hasOverrides,
                version,
                stored,
                effective = effectiveDto,
                // These aliases preserve the shape used by the existing vanilla settings workflow.
                workflow = ToWorkflow(effective, system: libraryWorkflow is null || organizationId == 1),
                ui_text = ToEffectivePatronText(effective),
                emails = await ToEffectiveEmailAsync(context, organizationId, effective, cancellationToken),
                formatClaimRules = autoClaimRules,
                autoClaimStaff,
                originsForEditor = origins
            });
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<AdministrationResult> SaveSettingsAsync(
        CurrentStaff actor,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (!TryResolveScope(actor, GetString(payload, "orgId") ?? GetString(payload, "organizationId"), out var organizationId, out var failure))
        {
            return failure;
        }

        var isReset = organizationId != 1 &&
                      string.Equals(GetString(payload, "action"), "reset", StringComparison.OrdinalIgnoreCase);
        if (!isReset)
        {
            var patronCodeFailure = await ValidatePatronCodePayloadAsync(payload, cancellationToken);
            if (patronCodeFailure is not null)
            {
                return patronCodeFailure;
            }
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            cancellationToken);
        var autoClaimStaffIds = organizationId != 1 &&
            TryGetAny(payload, out var autoClaimRules, "autoClaimRules", "formatClaimRules") &&
            autoClaimRules.ValueKind == JsonValueKind.Array
            ? autoClaimRules.EnumerateArray()
                .Select(item => GetLong(item, "staffUserId") ?? GetLong(item, "staffId"))
                .Where(item => item.HasValue).Select(item => item!.Value).ToArray()
            : [];
        var locked = await LockAndRevalidateActorAsync(
            context,
            actor,
            organizationId,
            [],
            cancellationToken,
            includeAllOrganizations: organizationId == 1,
            additionalStaffIds: autoClaimStaffIds);
        if (locked.Failure is not null)
        {
            return locked.Failure;
        }
        var organization = locked.Organizations[organizationId];

        var expectedVersion = GetString(payload, "version");
        if (!TryValidateSettingsVersion(expectedVersion, out var versionFailure))
        {
            return versionFailure!;
        }
        var currentVersion = await ComputeSettingsVersionAsync(context, organizationId, cancellationToken);
        if (!string.Equals(expectedVersion, currentVersion, StringComparison.Ordinal))
        {
            return new AdministrationResult(
                "stale_version",
                Message: "These settings changed in another session. Reload before saving.");
        }

        if (isReset)
        {
            await ResetLibrarySettingsInTransactionAsync(context, organizationId, actor, cancellationToken);
        }
        else
        {
            if (organizationId == 1)
            {
                await ApplySystemSettingsAsync(context, payload, cancellationToken);
            }
            await ApplyScopedSettingsAsync(context, organizationId, payload, cancellationToken);
        }

        if (!isReset)
        {
            await AddAuditAsync(
                context,
                actor,
                organizationId,
                organizationId == 1 ? "system_settings_updated" : "library_settings_updated",
                "Configuration",
                organizationId.ToString(),
                new
                {
                    action = "save",
                    scope = organizationId == 1 ? "system" : "library",
                    sections = payload.ValueKind == JsonValueKind.Object
                        ? payload.EnumerateObject().Select(item => item.Name).Where(item => item is not "version" and not "orgId").Order().ToArray()
                        : []
                });
        }
        await context.SaveChangesAsync(cancellationToken);
        var savedVersion = await ComputeSettingsVersionAsync(context, organizationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AdministrationResult(
            "saved",
            new
            {
                orgId = organizationId == 1 ? "system" : organizationId.ToString(),
                version = savedVersion
            });
    }

    public async Task<AdministrationResult> ResetLibrarySettingsAsync(
        CurrentStaff actor,
        int organizationId,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        if (!TryResolveScope(actor, organizationId.ToString(), out var resolved, out var failure) || resolved == 1)
        {
            return failure.Code == "ok" ? new AdministrationResult("staff_scope_forbidden") : failure;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            cancellationToken);
        var locked = await LockAndRevalidateActorAsync(
            context,
            actor,
            organizationId,
            [],
            cancellationToken);
        if (locked.Failure is not null)
        {
            return locked.Failure;
        }
        if (!TryValidateSettingsVersion(expectedVersion, out var versionFailure))
        {
            return versionFailure!;
        }
        var currentVersion = await ComputeSettingsVersionAsync(context, organizationId, cancellationToken);
        if (!string.Equals(expectedVersion, currentVersion, StringComparison.Ordinal))
        {
            return new AdministrationResult(
                "stale_version",
                Message: "These settings changed in another session. Reload before resetting.");
        }
        await ResetLibrarySettingsInTransactionAsync(context, organizationId, actor, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        var resetVersion = await ComputeSettingsVersionAsync(context, organizationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AdministrationResult("reset", new { orgId = organizationId.ToString(), version = resetVersion });
    }

    public async Task<AdministrationResult> ListOrganizationsAsync(
        CurrentStaff actor,
        CancellationToken cancellationToken)
    {
        if (actor.Role is not ("admin" or "super_admin"))
        {
            return new AdministrationResult("staff_scope_forbidden");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.Organizations.AsNoTracking();
        if (actor.Role != "super_admin")
        {
            query = query.Where(item => item.Id == actor.OrganizationId);
        }
        var organizations = await query.OrderBy(item => item.Id).ToListAsync(cancellationToken);
        return new AdministrationResult(
            "ok",
            organizations.Select(item => new OrganizationSummary(
                item.Id,
                item.DisplayName,
                item.Abbreviation,
                item.IsActive,
                item.LastSyncedUtc,
                StaffVersion.Encode(item.RowVersion))).ToArray());
    }

    public async Task<AdministrationResult> ListPatronCodesAsync(
        CurrentStaff actor,
        string? requestedOrganization,
        CancellationToken cancellationToken)
    {
        if (!TryResolveScope(actor, requestedOrganization, out _, out var failure))
        {
            return failure;
        }

        try
        {
            var choices = await polarisProvider.GetPatronCodesAsync(cancellationToken);
            return new AdministrationResult(
                "ok",
                choices.Select(item => new { id = item.Id, description = item.Description }).ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PolarisOperationalException exception)
        {
            return new AdministrationResult(
                "patron_codes_unavailable",
                Message: exception.Message);
        }
        catch (Exception)
        {
            return new AdministrationResult(
                "patron_codes_unavailable",
                Message: "Patron-code reference data is unavailable.");
        }
    }

    public async Task<AdministrationResult> SyncOrganizationsAsync(
        CurrentStaff actor,
        CancellationToken cancellationToken)
    {
        if (actor.Role != "super_admin")
        {
            return new AdministrationResult("staff_scope_forbidden");
        }

        var snapshots = await polarisProvider.GetOrganizationsAsync(cancellationToken);
        if (snapshots.Count == 0)
        {
            return new AdministrationResult("polaris_organizations_empty");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            cancellationToken);
        var lockedOrganizations = await LockOrganizationsAsync(
            context,
            new[] { 1, actor.OrganizationId }.Concat(snapshots.Select(item => item.Id)),
            cancellationToken);
        if (!lockedOrganizations.ContainsKey(actor.OrganizationId))
        {
            return new AdministrationResult("staff_session_invalid");
        }
        var actorEligibility = await staffEligibility.RevalidateLockedAsync(
            context,
            actor,
            null,
            StaffRoleRequirement.SuperAdmin,
            requireActorParticipation: true,
            lockedOrganizations.Keys.ToHashSet(),
            cancellationToken);
        if (actorEligibility.Outcome != StaffEligibilityOutcome.Allowed)
        {
            return AuthorizationFailure(actorEligibility);
        }
        if (!lockedOrganizations.ContainsKey(actorEligibility.Staff!.OrganizationId))
        {
            return new AdministrationResult("staff_session_invalid");
        }
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var changed = 0;
        foreach (var snapshot in snapshots.Where(item => item.Id > 0).GroupBy(item => item.Id).Select(item => item.First()))
        {
            var organization = await LockOrganizationAsync(context, snapshot.Id, cancellationToken);
            if (snapshot.Id == 1)
            {
                organization ??= new Organization { Id = 1, DisplayName = snapshot.DisplayName, IsActive = true };
                if (organization.Id == 1 && context.Entry(organization).State == EntityState.Detached)
                {
                    context.Organizations.Add(organization);
                }
                organization.DisplayName = snapshot.DisplayName;
                organization.Abbreviation = Clean(snapshot.Abbreviation);
                organization.IsActive = true;
                organization.LastSyncedUtc = now;
                changed++;
                continue;
            }

            if (organization is null)
            {
                organization = new Organization
                {
                    Id = snapshot.Id,
                    DisplayName = snapshot.DisplayName,
                    Abbreviation = Clean(snapshot.Abbreviation),
                    IsActive = false,
                    LastSyncedUtc = now
                };
                context.Organizations.Add(organization);
            }
            else
            {
                organization.DisplayName = snapshot.DisplayName;
                organization.Abbreviation = Clean(snapshot.Abbreviation);
                organization.LastSyncedUtc = now;
            }
            changed++;
        }

        await AddAuditAsync(
            context,
            actor,
            null,
            "organizations_synced",
            "Organization",
            null,
            new { received = snapshots.Count, changed, newLibrariesInactive = true });
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AdministrationResult("synced", new { received = snapshots.Count, changed });
    }

    public async Task<AdministrationResult> SetOrganizationActiveAsync(
        CurrentStaff actor,
        int organizationId,
        bool active,
        string? reason,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        if (actor.Role != "super_admin" || organizationId <= 1)
        {
            return new AdministrationResult("staff_scope_forbidden");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            cancellationToken);
        var locked = await LockAndRevalidateActorAsync(
            context,
            actor,
            organizationId,
            [],
            cancellationToken,
            StaffRoleRequirement.SuperAdmin);
        if (locked.Failure is not null)
        {
            return locked.Failure;
        }
        var organization = locked.Organizations[organizationId];
        if (!StaffVersion.TryDecode(expectedVersion, out var expectedOrganizationVersion))
        {
            return new AdministrationResult("organization_version_required");
        }
        if (!organization.RowVersion.SequenceEqual(expectedOrganizationVersion))
        {
            return new AdministrationResult(
                "stale_version",
                Message: "This library changed in another session. Reload before changing participation.");
        }

        var changed = organization.IsActive != active;
        organization.IsActive = active;
        var revoked = 0;
        if (!active)
        {
            var sessions = await context.PatronSessions
                .Where(item => item.EffectiveOrganizationId == organizationId && item.RevokedUtc == null)
                .ToListAsync(cancellationToken);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            foreach (var session in sessions)
            {
                session.RevokedUtc = now;
            }
            revoked = sessions.Count;
        }

        await AddAuditAsync(
            context,
            actor,
            organizationId,
            active ? "organization_activated" : "organization_deactivated",
            "Organization",
            organizationId.ToString(),
            new { active, changed, revokedPatronSessions = revoked, reason = Clean(reason) });
        await context.SaveChangesAsync(cancellationToken);
        var savedVersion = StaffVersion.Encode(organization.RowVersion);
        await transaction.CommitAsync(cancellationToken);
        return new AdministrationResult(active ? "activated" : "deactivated", new { organizationId, revokedPatronSessions = revoked, version = savedVersion });
    }

    public async Task<AdministrationResult> TestPolarisAsync(
        CurrentStaff actor,
        CancellationToken cancellationToken)
    {
        if (actor.Role != "super_admin")
        {
            return new AdministrationResult("staff_scope_forbidden");
        }

        var before = await staffEligibility.FindByEmailAsync(
            actor.AuthenticationEmail,
            actor.EntraTenantId,
            null,
            StaffRoleRequirement.SuperAdmin,
            requireParticipation: true,
            cancellationToken);
        if (before.Outcome != StaffEligibilityOutcome.Allowed)
        {
            return AuthorizationFailure(before);
        }
        var result = await polarisProvider.TestConnectionAsync(cancellationToken);
        var after = await staffEligibility.FindByEmailAsync(
            actor.AuthenticationEmail,
            actor.EntraTenantId,
            null,
            StaffRoleRequirement.SuperAdmin,
            requireParticipation: true,
            cancellationToken);
        if (after.Outcome != StaffEligibilityOutcome.Allowed)
        {
            return AuthorizationFailure(after);
        }
        return new AdministrationResult(
            result.IsConnected ? "polaris_connected" : "polaris_unavailable",
            new
            {
                connected = result.IsConnected,
                organizationCount = result.OrganizationCount,
                errorCode = result.SafeErrorCode
            });
    }

    public async Task<AdministrationResult> ListAuditAsync(
        CurrentStaff actor,
        int? organizationId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (actor.Role is not ("admin" or "super_admin"))
        {
            return new AdministrationResult("staff_scope_forbidden");
        }
        if (actor.Role != "super_admin" && organizationId.HasValue && organizationId != actor.OrganizationId)
        {
            return new AdministrationResult("staff_scope_forbidden");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var scope = actor.Role == "super_admin" ? organizationId : actor.OrganizationId;
        // Audit reads participate in the same scope boundary as the mutations they expose.
        // Lock routing organizations before the actor row so a changed binding, role, or
        // organization cannot authorize a stale request while lifecycle code is committing.
        var targetOrganizationId = scope ?? actor.OrganizationId;
        await using var transaction = await context.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            cancellationToken);
        var locked = await LockAndRevalidateActorAsync(
            context,
            actor,
            targetOrganizationId,
            [],
            cancellationToken);
        if (locked.Failure is not null)
        {
            return locked.Failure;
        }
        var rows = await context.AdministrativeAudits.AsNoTracking()
            .Where(item => !scope.HasValue || item.OrganizationId == scope)
            .OrderByDescending(item => item.CreatedUtc)
            .ThenByDescending(item => item.Id)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(item => new AdministrativeAuditEntry(
                item.Id,
                item.ActorStaffUserId,
                item.ActorName,
                item.OrganizationId,
                item.Action,
                item.TargetType,
                item.TargetId,
                item.DetailsJson,
                item.CreatedUtc))
            .ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AdministrationResult("ok", rows);
    }

    public async Task<AdministrationResult> SaveLogoAsync(
        CurrentStaff actor,
        string? requestedOrganization,
        byte[] data,
        string contentType,
        string fileName,
        string? altText,
        bool clearLogo,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        if (!TryResolveScope(actor, requestedOrganization, out var organizationId, out var failure))
        {
            return failure;
        }
        var hasImage = data is { Length: > 0 };
        LogoImageInfo? logoInfo = null;
        if (!clearLogo && hasImage && !LogoImageValidator.TryValidate(data, contentType, out logoInfo, out var logoError))
        {
            return new AdministrationResult("logo_invalid", Message: logoError);
        }
        if (!clearLogo && !hasImage && altText is null)
        {
            return new AdministrationResult("logo_invalid", Message: "Provide a PNG, JPEG, or GIF image, alt text, or explicitly clear the logo.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            cancellationToken);
        var locked = await LockAndRevalidateActorAsync(
            context,
            actor,
            organizationId,
            [],
            cancellationToken,
            includeAllOrganizations: organizationId == 1);
        if (locked.Failure is not null)
        {
            return locked.Failure;
        }
        if (!TryValidateSettingsVersion(expectedVersion, out var versionFailure))
        {
            return versionFailure!;
        }
        var currentVersion = await ComputeSettingsVersionAsync(context, organizationId, cancellationToken);
        if (!string.Equals(expectedVersion, currentVersion, StringComparison.Ordinal))
        {
            return new AdministrationResult(
                "stale_version",
                Message: "These settings changed in another session. Reload before updating branding.");
        }
        var branding = await context.Branding.SingleOrDefaultAsync(
            item => item.OrganizationId == organizationId,
            cancellationToken);
        branding ??= new Branding { OrganizationId = organizationId };
        if (context.Entry(branding).State == EntityState.Detached)
        {
            context.Branding.Add(branding);
        }
        if (clearLogo)
        {
            branding.LogoData = null;
            branding.LogoContentType = null;
            branding.LogoFileName = null;
        }
        else if (hasImage)
        {
            branding.LogoData = data;
            branding.LogoContentType = logoInfo!.ContentType;
            branding.LogoFileName = Clean(fileName) ?? "logo";
        }
        if (altText is not null)
        {
            branding.LogoAltText = Clean(altText);
        }
        branding.UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime;
        await AddAuditAsync(
            context,
            actor,
            organizationId,
            clearLogo ? "branding_logo_removed" : "branding_logo_updated",
            "Branding",
            organizationId.ToString(),
            new { clearLogo, hasAltOverride = branding.LogoAltText is not null });
        await context.SaveChangesAsync(cancellationToken);
        var savedVersion = await ComputeSettingsVersionAsync(context, organizationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AdministrationResult("branding_saved", new { organizationId, hasLogo = branding.LogoData is { Length: > 0 }, version = savedVersion });
    }

    public async Task<AdministrationResult> DeleteCustomFormatAsync(
        CurrentStaff actor,
        long formatId,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        if (actor.Role is not ("admin" or "super_admin"))
        {
            return new AdministrationResult("staff_scope_forbidden");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var ownerOrganizationId = await context.MaterialFormats.AsNoTracking()
            .Where(item => item.Id == formatId)
            .Select(item => (int?)item.OwnerOrganizationId)
            .SingleOrDefaultAsync(cancellationToken);
        if (!ownerOrganizationId.HasValue)
        {
            return new AdministrationResult("format_not_found");
        }
        if (!StaffVersion.TryDecode(expectedVersion, out var expectedFormatVersion))
        {
            return new AdministrationResult("format_version_required");
        }
        await using var transaction = await context.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            cancellationToken);
        var locked = await LockAndRevalidateActorAsync(
            context,
            actor,
            ownerOrganizationId.Value,
            [],
            cancellationToken);
        if (locked.Failure is not null)
        {
            return locked.Failure;
        }
        var format = await context.MaterialFormats.FromSqlInterpolated(
                $"SELECT * FROM [asap].[MaterialFormat] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {formatId}")
            .SingleOrDefaultAsync(cancellationToken);
        if (format is null)
        {
            return new AdministrationResult("format_not_found");
        }
        if (format.OwnerOrganizationId == 1)
        {
            return new AdministrationResult("system_format_durable");
        }
        if (!format.RowVersion.SequenceEqual(expectedFormatVersion))
        {
            return new AdministrationResult("stale_version", Message: "This format changed in another session. Reload before deleting it.");
        }

        var titleReference = await context.TitleRequests.AnyAsync(item => item.MaterialFormatId == formatId, cancellationToken);
        var copyReference = await context.AdditionalCopyRequests.AnyAsync(item => item.MaterialFormatId == formatId, cancellationToken);
        var ruleReference = await context.FormatAutoClaimRules.AnyAsync(item => item.MaterialFormatId == formatId, cancellationToken);
        if (titleReference || copyReference || ruleReference)
        {
            return new AdministrationResult(
                "format_referenced",
                new { titleRequests = titleReference, additionalCopies = copyReference, autoClaimRules = ruleReference },
                "This custom format is still referenced by workflow data and cannot be deleted.");
        }

        context.MaterialFormatCustomFieldRules.RemoveRange(
            await context.MaterialFormatCustomFieldRules.Where(item => item.MaterialFormatId == formatId).ToListAsync(cancellationToken));
        context.MaterialFormatOverrides.RemoveRange(
            await context.MaterialFormatOverrides.Where(item => item.MaterialFormatId == formatId).ToListAsync(cancellationToken));
        context.MaterialFormats.Remove(format);
        await AddAuditAsync(
            context,
            actor,
            format.OwnerOrganizationId,
            "custom_format_deleted",
            "MaterialFormat",
            formatId.ToString(),
            new { format.Code, format.Label });
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AdministrationResult("format_deleted", new { formatId });
    }

    private async Task ApplySystemSettingsAsync(
        AsapDbContext context,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var system = await context.SystemSettings.SingleAsync(item => item.OrganizationId == 1, cancellationToken);
        var polaris = await context.PolarisSettings.SingleAsync(item => item.OrganizationId == 1, cancellationToken);
        var email = await context.EmailSettings.SingleAsync(item => item.OrganizationId == 1, cancellationToken);
        var systemSection = GetObject(payload, "systemSettings", "system");
        var patronSection = GetObject(payload, "ui_text", "patron");
        var emailSection = GetObject(payload, "emails", "email");
        var polarisSection = GetObject(payload, "polaris");
        var smtpSection = GetObject(payload, "smtp");
        if (systemSection.ValueKind == JsonValueKind.Undefined) systemSection = payload;
        if (patronSection.ValueKind == JsonValueKind.Undefined) patronSection = payload;
        if (emailSection.ValueKind == JsonValueKind.Undefined) emailSection = payload;
        if (polarisSection.ValueKind == JsonValueKind.Undefined) polarisSection = payload;
        if (smtpSection.ValueKind == JsonValueKind.Undefined) smtpSection = payload;

        ApplyText(systemSection, "staffUrl", value => system.StaffApplicationUrl = NormalizeSystemText(value));
        ApplyText(payload, "staffUrl", value => system.StaffApplicationUrl = NormalizeSystemText(value));
        ApplyText(systemSection, "leapBibUrlPattern", value => system.LeapBibUrlPattern = NormalizeSystemText(value));
        ApplyText(payload, "leapBibUrlPattern", value => system.LeapBibUrlPattern = NormalizeSystemText(value));
        ApplyText(systemSection, "leapPatronUrlPattern", value => system.LeapPatronUrlPattern = NormalizeSystemText(value));
        ApplyText(payload, "leapPatronUrlPattern", value => system.LeapPatronUrlPattern = NormalizeSystemText(value));
        ApplyText(systemSection, "formatIconUrlPattern", value => system.MaterialTypeIconUrlPattern = NormalizeSystemText(value));
        ApplyText(payload, "formatIconUrlPattern", value => system.MaterialTypeIconUrlPattern = NormalizeSystemText(value));
        ApplyText(systemSection, "systemNotEnabledMessage", value => system.SystemNotEnabledMessage = NormalizeSystemText(value));
        ApplyText(payload, "systemNotEnabledMessage", value => system.SystemNotEnabledMessage = NormalizeSystemText(value));
        ApplyText(patronSection, "systemNotEnabledMessage", value => system.SystemNotEnabledMessage = NormalizeSystemText(value));
        ApplyText(systemSection, "misconfiguredMessage", value => system.MisconfiguredMessage = NormalizeSystemText(value));
        ApplyText(payload, "misconfiguredMessage", value => system.MisconfiguredMessage = NormalizeSystemText(value));
        ApplyText(patronSection, "misconfiguredMessage", value => system.MisconfiguredMessage = NormalizeSystemText(value));
        await ReplaceOriginsIfPresentAsync(context, payload, systemSection, cancellationToken);
        await ApplyParticipationAsync(context, payload, systemSection, cancellationToken);

        ApplyText(polarisSection, "host", value => polaris.Host = NormalizeSystemText(value));
        ApplyText(polarisSection, "accessId", value => polaris.AccessId = NormalizeSystemText(value));
        ApplyText(polarisSection, "staffDomain", value => polaris.StaffDomain = NormalizeSystemText(value));
        ApplyText(polarisSection, "adminUser", value => polaris.AdminUser = NormalizeSystemText(value));
        ApplyInt(polarisSection, "workstationId", value => polaris.WorkstationId = value);
        ApplyInt(polarisSection, "systemPolarisUserId", value => polaris.SystemPolarisUserId = value);
        ApplyInt(polarisSection, "userId", value => polaris.SystemPolarisUserId = value);
        ApplySecret(polarisSection, "apiKey", value => polaris.ProtectedApiKey = credentialProtector.Protect(value));
        ApplySecret(polarisSection, "adminPassword", value => polaris.ProtectedAdminPassword = credentialProtector.Protect(value));
        if (GetBool(polarisSection, "clearApiKey") == true) polaris.ProtectedApiKey = null;
        if (GetBool(polarisSection, "clearAdminPassword") == true) polaris.ProtectedAdminPassword = null;

        ApplyText(emailSection, "fromAddress", value => email.FromAddress = NormalizeSystemText(value));
        ApplyText(emailSection, "fromName", value => email.FromName = NormalizeSystemText(value));
        ApplyText(smtpSection, "fromAddress", value => email.FromAddress = NormalizeSystemText(value));
        ApplyText(smtpSection, "fromName", value => email.FromName = NormalizeSystemText(value));
        ApplySecret(emailSection, "postmarkToken", value => email.ProtectedServerToken = credentialProtector.Protect(value));
        ApplySecret(emailSection, "serverToken", value => email.ProtectedServerToken = credentialProtector.Protect(value));
        if (GetBool(emailSection, "clearPostmarkToken") == true || GetBool(emailSection, "clearServerToken") == true)
        {
            email.ProtectedServerToken = null;
        }
    }

    private async Task ApplyScopedSettingsAsync(
        AsapDbContext context,
        int organizationId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var workflowSection = GetObject(payload, "workflow");
        var patronSection = GetObject(payload, "ui_text", "patron");
        var emailSection = GetObject(payload, "emails", "email");
        if (workflowSection.ValueKind == JsonValueKind.Undefined) workflowSection = payload;
        if (patronSection.ValueKind == JsonValueKind.Undefined) patronSection = payload;
        if (emailSection.ValueKind == JsonValueKind.Undefined) emailSection = payload;
        var isSystem = organizationId == 1;

        var workflow = await GetOrCreateWorkflowAsync(context, organizationId, cancellationToken);
        foreach (var field in WorkflowTextFields)
        {
            ApplyText(workflowSection, field, value => SetWorkflowText(workflow, field, value, isSystem));
        }
        foreach (var field in WorkflowBoolFields)
        {
            ApplyBool(workflowSection, field, value => SetWorkflowBool(workflow, field, value, isSystem));
        }
        foreach (var field in WorkflowIntFields)
        {
            ApplyInt(workflowSection, field, value => SetWorkflowInt(workflow, field, value, isSystem));
        }
        if (HasProperty(workflowSection, "outstandingTimeoutRejectionTemplateId"))
        {
            workflow.OutstandingTimeoutRejectionTemplateId = await ResolveTemplateReferenceAsync(
                context,
                GetString(workflowSection, "outstandingTimeoutRejectionTemplateId"),
                organizationId,
                cancellationToken);
        }
        if (HasProperty(workflowSection, "outstandingTimeoutRejectionTemplate"))
        {
            workflow.OutstandingTimeoutRejectionTemplateId = await ResolveTemplateReferenceAsync(
                context,
                GetString(workflowSection, "outstandingTimeoutRejectionTemplate"),
                organizationId,
                cancellationToken);
        }

        var patron = await GetOrCreatePatronAsync(context, organizationId, cancellationToken);
        foreach (var field in PatronTextColumns)
        {
            ApplyText(patronSection, field.Key, value => SetPatronText(patron, field.Value, value, isSystem));
        }
        ApplyDuplicateLabels(patronSection, patron, isSystem);

        var email = await GetOrCreateEmailAsync(context, organizationId, cancellationToken);
        ApplyText(emailSection, "fromAddress", value => email.FromAddress = NormalizeScopedText(value, isSystem));
        ApplyText(emailSection, "fromName", value => email.FromName = NormalizeScopedText(value, isSystem));
        ApplySecret(emailSection, "postmarkToken", value => email.ProtectedServerToken = credentialProtector.Protect(value));
        if (GetBool(emailSection, "clearPostmarkToken") == true) email.ProtectedServerToken = null;

        await ApplyWholeSetsAsync(context, organizationId, workflowSection, patronSection, cancellationToken);
        await ApplyProvidersAsync(context, organizationId, workflowSection, payload, cancellationToken);
        await ApplyFormatsAsync(context, organizationId, payload, patronSection, cancellationToken);
        await ApplyCustomFieldsAsync(context, organizationId, patronSection, payload, cancellationToken);
        await ApplyAutoClaimRulesAsync(context, organizationId, payload, cancellationToken);
        await ApplyTemplatesAsync(context, organizationId, payload, cancellationToken);
        await ValidateRejectionTemplateReferencesAsync(context, organizationId, cancellationToken);
        await ApplyBrandingAsync(context, organizationId, patronSection, payload, cancellationToken);

        if (!isSystem)
        {
            await RemoveEmptyOverrideRowsAsync(context, organizationId, cancellationToken);
        }
    }

    private async Task ResetLibrarySettingsInTransactionAsync(
        AsapDbContext context,
        int organizationId,
        CurrentStaff actor,
        CancellationToken cancellationToken)
    {
        var workflow = await context.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        if (workflow is not null)
        {
            workflow.OutstandingTimeoutRejectionTemplateId = null;
            context.WorkflowSettings.Remove(workflow);
        }
        var patron = await context.PatronSettings.SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        if (patron is not null) context.PatronSettings.Remove(patron);
        var email = await context.EmailSettings.SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        if (email is not null) context.EmailSettings.Remove(email);

        var publication = await context.PublicationOptionSets.SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        if (publication is not null) context.PublicationOptionSets.Remove(publication);
        var creators = await context.CommonCreatorSets.SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        if (creators is not null) context.CommonCreatorSets.Remove(creators);
        var patronCodes = await context.PatronCodeEligibilitySets.SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        if (patronCodes is not null) context.PatronCodeEligibilitySets.Remove(patronCodes);

        context.ExternalSearchProviderOverrides.RemoveRange(
            await context.ExternalSearchProviderOverrides.Where(item => item.LibraryOrganizationId == organizationId).ToListAsync(cancellationToken));
        context.MaterialFormatOverrides.RemoveRange(
            await context.MaterialFormatOverrides.Where(item => item.LibraryOrganizationId == organizationId).ToListAsync(cancellationToken));
        context.EmailTemplates.RemoveRange(
            await context.EmailTemplates.Where(item => item.OrganizationId == organizationId && item.SourceTemplateId != null).ToListAsync(cancellationToken));
        var branding = await context.Branding.SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        if (branding is not null) context.Branding.Remove(branding);

        await AddAuditAsync(
            context,
            actor,
            organizationId,
            "library_settings_reset",
            "Configuration",
            organizationId.ToString(),
            new
            {
                preserved = new[] { "custom_fields", "custom_formats", "format_rules", "auto_claim_rules", "custom_templates" },
                removed = new[] { "scalar_overrides", "whole_sets", "provider_overrides", "format_overrides", "inherited_templates", "branding" }
            });
    }

    private static bool TryResolveScope(
        CurrentStaff actor,
        string? requested,
        out int organizationId,
        out AdministrationResult failure)
    {
        organizationId = 0;
        failure = new AdministrationResult("ok");
        if (actor.Role is not ("admin" or "super_admin"))
        {
            failure = new AdministrationResult("staff_scope_forbidden");
            return false;
        }

        var normalized = Clean(requested);
        if (string.IsNullOrEmpty(normalized))
        {
            organizationId = actor.Role == "super_admin" ? 1 : actor.OrganizationId;
        }
        else if (string.Equals(normalized, "system", StringComparison.OrdinalIgnoreCase))
        {
            organizationId = 1;
        }
        else if (!int.TryParse(normalized, out organizationId) || organizationId <= 1)
        {
            failure = new AdministrationResult("organization_invalid");
            return false;
        }

        if (organizationId == 1 && actor.Role != "super_admin" ||
            organizationId != 1 && actor.Role != "super_admin" && actor.OrganizationId != organizationId)
        {
            failure = new AdministrationResult("staff_scope_forbidden");
            return false;
        }
        return true;
    }

    private async Task<(Dictionary<int, Organization> Organizations, AdministrationResult? Failure)> LockAndRevalidateActorAsync(
        AsapDbContext context,
        CurrentStaff actor,
        int targetOrganizationId,
        IEnumerable<int> additionalOrganizationIds,
        CancellationToken cancellationToken,
        StaffRoleRequirement roleRequirement = StaffRoleRequirement.Admin,
        bool includeAllOrganizations = false,
        IEnumerable<long>? additionalStaffIds = null)
    {
        if (includeAllOrganizations)
        {
            // System versions include every participation row. Org1 also serializes
            // reference additions, so enumerate and lock that set before any Staff row.
            await LockOrganizationAsync(context, 1, cancellationToken);
            additionalOrganizationIds = additionalOrganizationIds.Concat(
                await context.Organizations.AsNoTracking().Where(item => item.Id > 1)
                    .OrderBy(item => item.Id).Select(item => item.Id).ToListAsync(cancellationToken)).ToArray();
        }
        var organizationIds = new[] { 1, actor.OrganizationId, targetOrganizationId }
            .Concat(additionalOrganizationIds)
            .Where(item => item > 0)
            .Distinct()
            .Order()
            .ToArray();
        var organizations = await LockOrganizationsAsync(context, organizationIds, cancellationToken);
        if (!organizations.ContainsKey(targetOrganizationId))
        {
            return (organizations, new AdministrationResult("organization_not_found"));
        }
        if (!organizations.ContainsKey(actor.OrganizationId))
        {
            return (organizations, new AdministrationResult("staff_session_invalid"));
        }

        if (additionalStaffIds is not null)
        {
            foreach (var staffId in additionalStaffIds.Append(actor.Id).Distinct().Order())
            {
                await context.StaffUsers.FromSqlInterpolated(
                        $"SELECT * FROM [asap].[StaffUser] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {staffId}")
                    .SingleOrDefaultAsync(cancellationToken);
            }
        }

        var eligibility = await staffEligibility.RevalidateLockedAsync(
            context,
            actor,
            targetOrganizationId,
            roleRequirement,
            requireActorParticipation: true,
            organizations.Keys.ToHashSet(),
            cancellationToken);
        if (eligibility.Outcome != StaffEligibilityOutcome.Allowed)
        {
            return (organizations, AuthorizationFailure(eligibility));
        }
        if (!organizations.ContainsKey(eligibility.Staff!.OrganizationId))
        {
            return (organizations, new AdministrationResult("staff_session_invalid"));
        }
        return (organizations, null);
    }

    private static async Task<Dictionary<int, Organization>> LockOrganizationsAsync(
        AsapDbContext context,
        IEnumerable<int> organizationIds,
        CancellationToken cancellationToken)
    {
        var organizations = new Dictionary<int, Organization>();
        foreach (var organizationId in organizationIds.Where(item => item > 0).Distinct().Order())
        {
            var organization = await LockOrganizationAsync(context, organizationId, cancellationToken);
            if (organization is not null)
            {
                organizations[organizationId] = organization;
            }
        }
        return organizations;
    }

    private static AdministrationResult AuthorizationFailure(StaffEligibilityResult result) =>
        result.Outcome == StaffEligibilityOutcome.InvalidIdentity
            ? new AdministrationResult("staff_session_invalid")
            : new AdministrationResult("staff_scope_forbidden");

    private async Task<AdministrationResult?> ValidatePatronCodePayloadAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var workflow = GetObject(payload, "workflow");
        if (workflow.ValueKind == JsonValueKind.Undefined)
        {
            workflow = payload;
        }
        if (!TryGetAny(workflow, out var value, "allowedPatronCodeIds", "patronCodeIds"))
        {
            return null;
        }
        if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Array))
        {
            return new AdministrationResult(
                "patron_codes_invalid",
                Message: "allowedPatronCodeIds must be an array or newline-delimited string.");
        }

        var requested = ParseValues(value);
        if (requested.Count == 0)
        {
            return null;
        }

        IReadOnlyList<PolarisPatronCodeSnapshot> choices;
        try
        {
            choices = await polarisProvider.GetPatronCodesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PolarisOperationalException exception)
        {
            return new AdministrationResult(
                "patron_codes_unavailable",
                Message: exception.Message);
        }
        catch (Exception)
        {
            return new AdministrationResult(
                "patron_codes_unavailable",
                Message: "Patron-code reference data is unavailable.");
        }

        var known = choices
            .Select(item => Clean(item.Id))
            .Where(item => item is not null)
            .Select(item => item!)
            .ToHashSet(StringComparer.Ordinal);
        var unknown = requested
            .Where(item => !known.Contains(item))
            .ToArray();
        return unknown.Length == 0
            ? null
            : new AdministrationResult(
                "patron_code_unknown",
                new { ids = unknown },
                "One or more selected patron-code IDs are not present in Polaris.");
    }

    private static bool TryValidateSettingsVersion(
        string? value,
        out AdministrationResult? failure)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failure = new AdministrationResult("settings_version_required");
            return false;
        }

        try
        {
            if (Convert.FromBase64String(value).Length != 32)
            {
                failure = new AdministrationResult("invalid_settings_version");
                return false;
            }
        }
        catch (FormatException)
        {
            failure = new AdministrationResult("invalid_settings_version");
            return false;
        }

        failure = null;
        return true;
    }

    private static Task<Organization?> LockOrganizationAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken) =>
        context.Organizations.FromSqlInterpolated(
                $"SELECT * FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {organizationId}")
            .SingleOrDefaultAsync(cancellationToken);

    private async Task AddAuditAsync(
        AsapDbContext context,
        CurrentStaff actor,
        int? organizationId,
        string action,
        string targetType,
        string? targetId,
        object details)
    {
        context.AdministrativeAudits.Add(new AdministrativeAudit
        {
            ActorStaffUserId = actor.Id,
            ActorName = actor.DisplayName ?? actor.UserPrincipalName,
            OrganizationId = organizationId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            DetailsJson = JsonSerializer.Serialize(details),
            CreatedUtc = timeProvider.GetUtcNow().UtcDateTime
        });
    }

    private static async Task<WorkflowSettings> GetOrCreateWorkflowAsync(AsapDbContext context, int organizationId, CancellationToken cancellationToken)
    {
        var row = await context.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        if (row is not null) return row;
        row = new WorkflowSettings { OrganizationId = organizationId };
        context.WorkflowSettings.Add(row);
        return row;
    }

    private static async Task<PatronSettings> GetOrCreatePatronAsync(AsapDbContext context, int organizationId, CancellationToken cancellationToken)
    {
        var row = await context.PatronSettings.SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        if (row is not null) return row;
        row = new PatronSettings { OrganizationId = organizationId };
        context.PatronSettings.Add(row);
        return row;
    }

    private static async Task<EmailSettings> GetOrCreateEmailAsync(AsapDbContext context, int organizationId, CancellationToken cancellationToken)
    {
        var row = await context.EmailSettings.SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        if (row is not null) return row;
        row = new EmailSettings { OrganizationId = organizationId };
        context.EmailSettings.Add(row);
        return row;
    }

    private static void SetWorkflowText(WorkflowSettings row, string field, string? value, bool system)
    {
        var normalized = NormalizeScopedText(value, system);
        switch (field)
        {
            case "suggestionLimitMessage": row.SuggestionLimitMessage = normalized; break;
            case "commonAuthorsLabel": row.CommonAuthorsLabel = normalized; break;
            case "commonAuthorsHelp": row.CommonAuthorsHelp = normalized; break;
            case "commonAuthorsMessage": row.CommonAuthorsMessage = normalized; break;
            case "patronCodeEligibilityMessage": row.PatronCodeEligibilityMessage = normalized; break;
        }
    }

    private static void SetWorkflowBool(WorkflowSettings row, string field, bool? value, bool system)
    {
        var normalized = system ? value ?? false : value;
        switch (field)
        {
            case "outstandingTimeoutEnabled": row.OutstandingTimeoutEnabled = normalized; break;
            case "outstandingTimeoutSendEmail": row.OutstandingTimeoutSendEmail = normalized; break;
            case "holdPickupTimeoutEnabled": row.HoldPickupTimeoutEnabled = normalized; break;
            case "pendingHoldTimeoutEnabled": row.PendingHoldTimeoutEnabled = normalized; break;
            case "additionalCopyTimeoutEnabled": row.AdditionalCopyTimeoutEnabled = normalized; break;
            case "autoPromote": row.AutoPromote = normalized; break;
            case "commonAuthorsEnabled": row.CommonAuthorsEnabled = normalized; break;
            case "allowPatronAutoholdOptOut": row.AllowPatronAutoholdOptOut = normalized; break;
            case "allowAnyRegisteredCardLogin": row.AllowAnyRegisteredCardLogin = normalized; break;
            case "patronCodeEligibilityEnabled": row.PatronCodeEligibilityEnabled = normalized; break;
        }
    }

    private static void SetWorkflowInt(WorkflowSettings row, string field, int? value, bool system)
    {
        var normalized = value;
        if (normalized is <= 0 or > 3650 && field.EndsWith("Days", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{field} must be between 1 and 3650.");
        }
        if (field == "suggestionLimit" && normalized is <= 0 or > 1000)
        {
            throw new InvalidOperationException("suggestionLimit must be between 1 and 1000.");
        }
        switch (field)
        {
            case "suggestionLimit": row.SuggestionLimit = normalized; break;
            case "outstandingTimeoutDays": row.OutstandingTimeoutDays = normalized; break;
            case "holdPickupTimeoutDays": row.HoldPickupTimeoutDays = normalized; break;
            case "pendingHoldTimeoutDays": row.PendingHoldTimeoutDays = normalized; break;
            case "additionalCopyTimeoutDays": row.AdditionalCopyTimeoutDays = normalized; break;
        }
    }

    private static void SetPatronText(PatronSettings row, string property, string? value, bool system)
    {
        var normalized = NormalizeScopedText(value, system);
        switch (property)
        {
            case nameof(PatronSettings.PageTitle): row.PageTitle = normalized; break;
            case nameof(PatronSettings.BarcodeLabel): row.BarcodeLabel = normalized; break;
            case nameof(PatronSettings.PinLabel): row.PinLabel = normalized; break;
            case nameof(PatronSettings.LoginPrompt): row.LoginPrompt = normalized; break;
            case nameof(PatronSettings.LoginNote): row.LoginNote = normalized; break;
            case nameof(PatronSettings.SuggestionFormNote): row.SuggestionFormNote = normalized; break;
            case nameof(PatronSettings.NoEmailMessage): row.NoEmailMessage = normalized; break;
            case nameof(PatronSettings.SuccessTitle): row.SuccessTitle = normalized; break;
            case nameof(PatronSettings.SuccessMessage): row.SuccessMessage = normalized; break;
            case nameof(PatronSettings.AlreadySubmittedMessage): row.AlreadySubmittedMessage = normalized; break;
            case nameof(PatronSettings.EbookMessage): row.EbookMessage = normalized; break;
            case nameof(PatronSettings.EaudiobookMessage): row.EaudiobookMessage = normalized; break;
            case nameof(PatronSettings.SuggestionStatusLabel): row.SuggestionStatusLabel = normalized; break;
            case nameof(PatronSettings.OutstandingPurchaseStatusLabel): row.OutstandingPurchaseStatusLabel = normalized; break;
            case nameof(PatronSettings.PendingHoldStatusLabel): row.PendingHoldStatusLabel = normalized; break;
            case nameof(PatronSettings.HoldPlacedStatusLabel): row.HoldPlacedStatusLabel = normalized; break;
            case nameof(PatronSettings.ClosedStatusLabel): row.ClosedStatusLabel = normalized; break;
            case nameof(PatronSettings.RejectedStatusLabel): row.RejectedStatusLabel = normalized; break;
            case nameof(PatronSettings.HoldCompletedStatusLabel): row.HoldCompletedStatusLabel = normalized; break;
            case nameof(PatronSettings.HoldNotPickedUpStatusLabel): row.HoldNotPickedUpStatusLabel = normalized; break;
            case nameof(PatronSettings.ManualStatusLabel): row.ManualStatusLabel = normalized; break;
            case nameof(PatronSettings.SilentStatusLabel): row.SilentStatusLabel = normalized; break;
        }
    }

    private static void ApplyDuplicateLabels(JsonElement section, PatronSettings row, bool system)
    {
        var labels = GetObject(section, "duplicateStatusLabels", "duplicateLabels");
        foreach (var pair in DuplicateLabelFields)
        {
            ApplyText(labels, pair.Key, value => SetPatronText(row, pair.Value, value, system));
        }
    }

    private static string? NormalizeSystemText(string? value) => Clean(value);

    private static string? NormalizeScopedText(string? value, bool system) =>
        system ? Clean(value) : Clean(value);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static JsonElement GetObject(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) return default;
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object)
            {
                return value;
            }
        }
        return default;
    }

    private static bool HasProperty(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out _);

    private static bool TryGetAny(JsonElement root, out JsonElement value, params string[] names)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in names)
            {
                if (root.TryGetProperty(name, out value)) return true;
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement root, string name)
    {
        if (!HasProperty(root, name)) return null;
        var value = root.GetProperty(name);
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static bool? GetBool(JsonElement root, string name)
    {
        if (!HasProperty(root, name)) return null;
        var value = root.GetProperty(name);
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        return bool.TryParse(GetString(root, name), out var parsed) ? parsed : null;
    }

    private static int? GetInt(JsonElement root, string name)
    {
        if (!HasProperty(root, name)) return null;
        var value = root.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return int.TryParse(GetString(root, name), out number) ? number : null;
    }

    private static long? GetLong(JsonElement root, string name)
    {
        if (!HasProperty(root, name)) return null;
        var value = root.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return long.TryParse(GetString(root, name), out number) ? number : null;
    }

    private static void ApplyText(JsonElement root, string name, Action<string?> setter)
    {
        if (HasProperty(root, name)) setter(GetString(root, name));
    }

    private static void ApplyBool(JsonElement root, string name, Action<bool?> setter)
    {
        if (HasProperty(root, name)) setter(GetBool(root, name));
    }

    private static void ApplyInt(JsonElement root, string name, Action<int?> setter)
    {
        if (HasProperty(root, name)) setter(GetInt(root, name));
    }

    private static void ApplySecret(JsonElement root, string name, Action<string> setter)
    {
        var value = Clean(GetString(root, name));
        if (value is not null) setter(value);
    }

    private static object ToSystemSettings(SystemSettings row, IReadOnlyList<string> origins) => new
    {
        staffUrl = row.StaffApplicationUrl,
        leapBibUrlPattern = row.LeapBibUrlPattern,
        leapPatronUrlPattern = row.LeapPatronUrlPattern,
        formatIconUrlPattern = row.MaterialTypeIconUrlPattern,
        systemNotEnabledMessage = row.SystemNotEnabledMessage,
        misconfiguredMessage = row.MisconfiguredMessage,
        patronEmbedAllowedOrigins = origins,
        version = StaffVersion.Encode(row.RowVersion)
    };

    private static object ToPolarisSettings(PolarisSettings row) => new
    {
        host = row.Host,
        accessId = row.AccessId,
        staffDomain = row.StaffDomain,
        adminUser = row.AdminUser,
        workstationId = row.WorkstationId,
        systemPolarisUserId = row.SystemPolarisUserId,
        hasApiKey = !string.IsNullOrWhiteSpace(row.ProtectedApiKey),
        hasAdminPassword = !string.IsNullOrWhiteSpace(row.ProtectedAdminPassword),
        version = StaffVersion.Encode(row.RowVersion)
    };

    private static object ToWorkflowRow(WorkflowSettings row) => new
    {
        suggestionLimit = row.SuggestionLimit,
        suggestionLimitMessage = row.SuggestionLimitMessage,
        outstandingTimeoutEnabled = row.OutstandingTimeoutEnabled,
        outstandingTimeoutDays = row.OutstandingTimeoutDays,
        outstandingTimeoutSendEmail = row.OutstandingTimeoutSendEmail,
        outstandingTimeoutRejectionTemplateId = row.OutstandingTimeoutRejectionTemplateId?.ToString(),
        holdPickupTimeoutEnabled = row.HoldPickupTimeoutEnabled,
        holdPickupTimeoutDays = row.HoldPickupTimeoutDays,
        pendingHoldTimeoutEnabled = row.PendingHoldTimeoutEnabled,
        pendingHoldTimeoutDays = row.PendingHoldTimeoutDays,
        additionalCopyTimeoutEnabled = row.AdditionalCopyTimeoutEnabled,
        additionalCopyTimeoutDays = row.AdditionalCopyTimeoutDays,
        autoPromote = row.AutoPromote,
        commonAuthorsEnabled = row.CommonAuthorsEnabled,
        commonAuthorsLabel = row.CommonAuthorsLabel,
        commonAuthorsHelp = row.CommonAuthorsHelp,
        commonAuthorsMessage = row.CommonAuthorsMessage,
        allowPatronAutoholdOptOut = row.AllowPatronAutoholdOptOut,
        allowAnyRegisteredCardLogin = row.AllowAnyRegisteredCardLogin,
        patronCodeEligibilityEnabled = row.PatronCodeEligibilityEnabled,
        patronCodeEligibilityMessage = row.PatronCodeEligibilityMessage,
        version = StaffVersion.Encode(row.RowVersion)
    };

    private static object? ToWorkflow(WorkflowSettings? row) => row is null ? null : ToWorkflowRow(row);

    private static object ToWorkflow(EffectivePatronConfiguration row, bool system) => new
    {
        suggestionLimit = row.SuggestionLimit,
        suggestionLimitMessage = row.SuggestionLimitMessage,
        allowAnyRegisteredCardLogin = row.AllowAnyRegisteredCardLogin,
        allowPatronAutoholdOptOut = row.AllowPatronAutoholdOptOut,
        patronCodeEligibilityEnabled = row.PatronCodeEligibilityEnabled,
        patronCodeEligibilityMessage = row.PatronCodeEligibilityMessage,
        commonAuthorsEnabled = row.CommonCreatorsEnabled,
        commonAuthorsLabel = row.CommonCreatorsLabel,
        commonAuthorsHelp = row.CommonCreatorsHelp,
        commonAuthorsMessage = row.CommonCreatorsMessage,
        system
    };

    private static object ToPatronRow(PatronSettings row) => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["pageTitle"] = row.PageTitle,
        ["barcodeLabel"] = row.BarcodeLabel,
        ["pinLabel"] = row.PinLabel,
        ["loginPrompt"] = row.LoginPrompt,
        ["loginNote"] = row.LoginNote,
        ["suggestionFormNote"] = row.SuggestionFormNote,
        ["noEmailMessage"] = row.NoEmailMessage,
        ["successTitle"] = row.SuccessTitle,
        ["successMessage"] = row.SuccessMessage,
        ["alreadySubmittedMessage"] = row.AlreadySubmittedMessage,
        ["ebookMessage"] = row.EbookMessage,
        ["eaudiobookMessage"] = row.EaudiobookMessage,
        ["suggestionStatusLabel"] = row.SuggestionStatusLabel,
        ["outstandingPurchaseStatusLabel"] = row.OutstandingPurchaseStatusLabel,
        ["pendingHoldStatusLabel"] = row.PendingHoldStatusLabel,
        ["holdPlacedStatusLabel"] = row.HoldPlacedStatusLabel,
        ["closedStatusLabel"] = row.ClosedStatusLabel,
        ["rejectedStatusLabel"] = row.RejectedStatusLabel,
        ["holdCompletedStatusLabel"] = row.HoldCompletedStatusLabel,
        ["holdNotPickedUpStatusLabel"] = row.HoldNotPickedUpStatusLabel,
        ["manualStatusLabel"] = row.ManualStatusLabel,
        ["silentStatusLabel"] = row.SilentStatusLabel
    };

    private static object? ToPatron(PatronSettings? row) => row is null ? null : ToPatronRow(row);

    private static object ToEmailRow(EmailSettings row) => new
    {
        fromAddress = row.FromAddress,
        fromName = row.FromName,
        hasPostmarkToken = !string.IsNullOrWhiteSpace(row.ProtectedServerToken),
        version = StaffVersion.Encode(row.RowVersion)
    };

    private static object? ToEmail(EmailSettings? row) => row is null ? null : ToEmailRow(row);

    private static object ToBranding(Branding? row) => new
    {
        hasLogo = row?.LogoData is { Length: > 0 },
        contentType = row?.LogoContentType,
        fileName = row?.LogoFileName,
        altText = row?.LogoAltText,
        version = row is null ? null : StaffVersion.Encode(row.RowVersion)
    };

    private static object ToEffectiveConfiguration(
        EffectivePatronConfiguration row,
        WorkflowSettings systemWorkflow,
        WorkflowSettings? libraryWorkflow) => new
    {
        workflow = ToEffectiveWorkflow(systemWorkflow, libraryWorkflow),
        row.OrganizationId,
        row.OrganizationName,
        row.IsActive,
        row.SuggestionLimit,
        row.SuggestionLimitMessage,
        row.AllowAnyRegisteredCardLogin,
        row.AllowPatronAutoholdOptOut,
        row.PatronCodeEligibilityEnabled,
        row.PatronCodeEligibilityMessage,
        allowedPatronCodeIds = row.AllowedPatronCodeIds,
        row.PageTitle,
        row.BarcodeLabel,
        row.PinLabel,
        row.LoginPrompt,
        row.LoginNote,
        row.SuggestionFormNote,
        row.NoEmailMessage,
        row.SuccessTitle,
        row.SuccessMessage,
        row.AlreadySubmittedMessage,
        row.SystemNotEnabledMessage,
        row.MisconfiguredMessage,
        row.EbookMessage,
        row.EaudiobookMessage,
        row.DuplicateStatusLabels,
        externalSearchProviders = row.ExternalSearchProviders.Select(item => new
        {
            id = item.Id.ToString(),
            key = item.Key,
            isEnabled = item.IsEnabled,
            label = item.Label,
            urlTemplate = item.UrlTemplate,
            sortOrder = item.SortOrder
        }).ToArray(),
        row.PublicationOptions,
        row.CommonCreators,
        row.CommonCreatorsLabel,
        row.CommonCreatorsHelp,
        row.CommonCreatorsMessage,
        row.CommonCreatorsEnabled,
        formats = row.Formats.Select(item => new
        {
            id = item.Id.ToString(),
            code = item.Code,
            label = item.Label,
            sortOrder = item.SortOrder,
            isEnabled = item.IsEnabled,
            messageBehavior = item.MessageBehavior,
            message = item.Message,
            title = new { mode = item.Title.Mode, label = item.Title.Label },
            author = new { mode = item.Author.Mode, label = item.Author.Label },
            identifier = new { mode = item.Identifier.Mode, label = item.Identifier.Label },
            publication = new { mode = item.Publication.Mode, label = item.Publication.Label },
            customFields = item.CustomFields.ToDictionary(
                pair => pair.Key,
                pair => (object)new { mode = pair.Value.Mode, labelOverride = pair.Value.Label },
                StringComparer.Ordinal)
        }).ToArray(),
        customFields = row.CustomFields.Select(item => new
        {
            id = item.Id.ToString(),
            key = item.Key,
            type = item.Type,
            label = item.Label,
            helpText = item.HelpText,
            sortOrder = item.SortOrder,
            options = item.Options.Select(option => new
            {
                id = option.Key,
                label = option.Label,
                sortOrder = option.SortOrder
            }).ToArray()
        }).ToArray(),
        email = new
        {
            row.Email.FromAddress,
            row.Email.FromName,
            hasServerToken = !string.IsNullOrWhiteSpace(row.Email.ProtectedServerToken)
        },
        row.SubmissionTemplate,
        row.HasLogo,
        row.LogoAltText
    };

    private static object ToEffectiveWorkflow(
        WorkflowSettings system,
        WorkflowSettings? library) => new
    {
        suggestionLimit = library?.SuggestionLimit ?? system.SuggestionLimit,
        suggestionLimitMessage = library?.SuggestionLimitMessage ?? system.SuggestionLimitMessage,
        outstandingTimeoutEnabled = library?.OutstandingTimeoutEnabled ?? system.OutstandingTimeoutEnabled,
        outstandingTimeoutDays = library?.OutstandingTimeoutDays ?? system.OutstandingTimeoutDays,
        outstandingTimeoutSendEmail = library?.OutstandingTimeoutSendEmail ?? system.OutstandingTimeoutSendEmail,
        outstandingTimeoutRejectionTemplateId = (library?.OutstandingTimeoutRejectionTemplateId ?? system.OutstandingTimeoutRejectionTemplateId)?.ToString(),
        holdPickupTimeoutEnabled = library?.HoldPickupTimeoutEnabled ?? system.HoldPickupTimeoutEnabled,
        holdPickupTimeoutDays = library?.HoldPickupTimeoutDays ?? system.HoldPickupTimeoutDays,
        pendingHoldTimeoutEnabled = library?.PendingHoldTimeoutEnabled ?? system.PendingHoldTimeoutEnabled,
        pendingHoldTimeoutDays = library?.PendingHoldTimeoutDays ?? system.PendingHoldTimeoutDays,
        additionalCopyTimeoutEnabled = library?.AdditionalCopyTimeoutEnabled ?? system.AdditionalCopyTimeoutEnabled,
        additionalCopyTimeoutDays = library?.AdditionalCopyTimeoutDays ?? system.AdditionalCopyTimeoutDays,
        autoPromote = library?.AutoPromote ?? system.AutoPromote,
        commonAuthorsEnabled = library?.CommonAuthorsEnabled ?? system.CommonAuthorsEnabled,
        commonAuthorsLabel = library?.CommonAuthorsLabel ?? system.CommonAuthorsLabel,
        commonAuthorsHelp = library?.CommonAuthorsHelp ?? system.CommonAuthorsHelp,
        commonAuthorsMessage = library?.CommonAuthorsMessage ?? system.CommonAuthorsMessage,
        allowPatronAutoholdOptOut = library?.AllowPatronAutoholdOptOut ?? system.AllowPatronAutoholdOptOut,
        allowAnyRegisteredCardLogin = library?.AllowAnyRegisteredCardLogin ?? system.AllowAnyRegisteredCardLogin,
        patronCodeEligibilityEnabled = library?.PatronCodeEligibilityEnabled ?? system.PatronCodeEligibilityEnabled,
        patronCodeEligibilityMessage = library?.PatronCodeEligibilityMessage ?? system.PatronCodeEligibilityMessage
    };

    private static object ToEffectivePatronText(EffectivePatronConfiguration row) => new
    {
        row.PageTitle,
        row.BarcodeLabel,
        row.PinLabel,
        row.LoginPrompt,
        row.LoginNote,
        row.SuggestionFormNote,
        row.NoEmailMessage,
        row.SuccessTitle,
        row.SuccessMessage,
        row.AlreadySubmittedMessage,
        row.EbookMessage,
        row.EaudiobookMessage,
        row.DuplicateStatusLabels,
        row.SystemNotEnabledMessage,
        row.MisconfiguredMessage
    };

    private static async Task<object> ToEffectiveEmailAsync(
        AsapDbContext context,
        int organizationId,
        EffectivePatronConfiguration effective,
        CancellationToken cancellationToken)
    {
        var templates = await LoadTemplatesAsync(context, organizationId, cancellationToken);
        return new
        {
            fromAddress = effective.Email.FromAddress,
            fromName = effective.Email.FromName,
            hasPostmarkToken = !string.IsNullOrWhiteSpace(effective.Email.ProtectedServerToken),
            templates,
            submissionTemplate = effective.SubmissionTemplate
        };
    }

    private static async Task<bool> HasLibraryOverridesAsync(AsapDbContext context, int organizationId, CancellationToken cancellationToken)
    {
        return await context.WorkflowSettings.AnyAsync(item => item.OrganizationId == organizationId, cancellationToken) ||
               await context.PatronSettings.AnyAsync(item => item.OrganizationId == organizationId, cancellationToken) ||
               await context.EmailSettings.AnyAsync(item => item.OrganizationId == organizationId, cancellationToken) ||
               await context.PublicationOptionSets.AnyAsync(item => item.OrganizationId == organizationId, cancellationToken) ||
               await context.CommonCreatorSets.AnyAsync(item => item.OrganizationId == organizationId, cancellationToken) ||
               await context.PatronCodeEligibilitySets.AnyAsync(item => item.OrganizationId == organizationId, cancellationToken) ||
               await context.ExternalSearchProviderOverrides.AnyAsync(item => item.LibraryOrganizationId == organizationId, cancellationToken) ||
               await context.MaterialFormatOverrides.AnyAsync(item => item.LibraryOrganizationId == organizationId, cancellationToken) ||
               await context.MaterialFormatCustomFieldRules.AnyAsync(item => item.LibraryOrganizationId == organizationId, cancellationToken) ||
               await context.FormatAutoClaimRules.AnyAsync(item => item.LibraryOrganizationId == organizationId, cancellationToken) ||
               await context.PatronCustomFields.AnyAsync(item => item.LibraryOrganizationId == organizationId, cancellationToken) ||
               await context.EmailTemplates.AnyAsync(item => item.OrganizationId == organizationId, cancellationToken) ||
               await context.Branding.AnyAsync(item => item.OrganizationId == organizationId, cancellationToken);
    }

    private static async Task<object> LoadPublicationSnapshotAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken)
    {
        var exists = await context.PublicationOptionSets.AsNoTracking()
            .AnyAsync(item => item.OrganizationId == organizationId, cancellationToken);
        var values = await context.PublicationOptions.AsNoTracking()
            .Where(item => item.OrganizationId == organizationId)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .Select(item => new
            {
                id = item.OptionKey,
                label = item.Label,
                enabled = item.IsEnabled,
                sortOrder = item.SortOrder
            })
            .ToArrayAsync(cancellationToken);
        return new { exists, values };
    }

    private static async Task<object> LoadCommonCreatorSnapshotAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken)
    {
        var exists = await context.CommonCreatorSets.AsNoTracking()
            .AnyAsync(item => item.OrganizationId == organizationId, cancellationToken);
        var values = await context.CommonCreatorTerms.AsNoTracking()
            .Where(item => item.OrganizationId == organizationId)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .Select(item => new { value = item.Value, sortOrder = item.SortOrder })
            .ToArrayAsync(cancellationToken);
        return new { exists, values };
    }

    private static async Task<object> LoadPatronCodeSnapshotAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken)
    {
        var exists = await context.PatronCodeEligibilitySets.AsNoTracking()
            .AnyAsync(item => item.OrganizationId == organizationId, cancellationToken);
        var values = await context.PatronCodeEligibilityMembers.AsNoTracking()
            .Where(item => item.OrganizationId == organizationId)
            .OrderBy(item => item.PatronCodeId)
            .Select(item => item.PatronCodeId)
            .ToArrayAsync(cancellationToken);
        return new { exists, values };
    }

    private static async Task<IReadOnlyList<object>> LoadRawProviderOverridesAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken)
    {
        if (organizationId == 1)
        {
            var providers = await context.ExternalSearchProviders.AsNoTracking()
                .Where(item => item.OrganizationId == 1)
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Id)
                .ToListAsync(cancellationToken);
            return providers.Select(item => (object)new
            {
                kind = "system",
                id = item.Id.ToString(),
                key = item.ProviderKey,
                isEnabled = (bool?)item.IsEnabled,
                label = item.Label,
                urlTemplate = item.UrlTemplate,
                version = StaffVersion.Encode(item.RowVersion)
            }).ToArray();
        }

        var overrides = await context.ExternalSearchProviderOverrides.AsNoTracking()
            .Where(item => item.LibraryOrganizationId == organizationId)
            .OrderBy(item => item.ExternalSearchProviderId)
            .ToListAsync(cancellationToken);
        return overrides.Select(item => (object)new
        {
            kind = "libraryOverride",
            id = item.ExternalSearchProviderId.ToString(),
            key = (string?)null,
            isEnabled = item.IsEnabled,
            label = item.Label,
            urlTemplate = item.UrlTemplate,
            version = StaffVersion.Encode(item.RowVersion)
        }).ToArray();
    }

    private static async Task<IReadOnlyList<object>> LoadRawFormatsAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken)
    {
        var formats = await context.MaterialFormats.AsNoTracking()
            .Where(item => item.OwnerOrganizationId == organizationId)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var values = formats.Select(item => (object)new
        {
            kind = organizationId == 1 ? "system" : "custom",
            id = item.Id.ToString(),
            materialFormatId = (string?)null,
            ownerOrganizationId = item.OwnerOrganizationId.ToString(),
            code = item.Code,
            label = item.Label,
            sortOrder = (int?)item.SortOrder,
            isEnabled = (bool?)item.IsEnabled,
            messageBehavior = item.MessageBehavior,
            message = item.Message,
            titleMode = item.TitleMode,
            titleLabel = item.TitleLabel,
            authorMode = item.AuthorMode,
            authorLabel = item.AuthorLabel,
            identifierMode = item.IdentifierMode,
            identifierLabel = item.IdentifierLabel,
            publicationMode = item.PublicationMode,
            publicationLabel = item.PublicationLabel,
            version = StaffVersion.Encode(item.RowVersion)
        }).ToList();
        if (organizationId == 1)
        {
            return values;
        }

        var overrides = await context.MaterialFormatOverrides.AsNoTracking()
            .Where(item => item.LibraryOrganizationId == organizationId)
            .OrderBy(item => item.MaterialFormatId)
            .ToListAsync(cancellationToken);
        values.AddRange(overrides.Select(item => (object)new
        {
            kind = "systemOverride",
            id = item.Id.ToString(),
            materialFormatId = item.MaterialFormatId.ToString(),
            ownerOrganizationId = "1",
            code = (string?)null,
            label = item.Label,
            sortOrder = item.SortOrder,
            isEnabled = item.IsEnabled,
            messageBehavior = item.MessageBehavior,
            message = item.Message,
            titleMode = item.TitleMode,
            titleLabel = item.TitleLabel,
            authorMode = item.AuthorMode,
            authorLabel = item.AuthorLabel,
            identifierMode = item.IdentifierMode,
            identifierLabel = item.IdentifierLabel,
            publicationMode = item.PublicationMode,
            publicationLabel = item.PublicationLabel,
            version = StaffVersion.Encode(item.RowVersion)
        }));
        return values;
    }

    private static async Task<IReadOnlyList<object>> LoadRawTemplatesAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken)
    {
        var templates = await context.EmailTemplates.AsNoTracking()
            .Where(item => item.OrganizationId == organizationId)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        return templates.Select(item => (object)new
        {
            id = item.Id.ToString(),
            organizationId = item.OrganizationId.ToString(),
            templateKey = item.TemplateKey,
            sourceTemplateId = item.SourceTemplateId?.ToString(),
            displayName = item.DisplayName,
            subject = item.SubjectTemplate,
            body = item.BodyTemplate,
            enabled = !item.IsHidden,
            isCustom = item.IsCustom,
            sortOrder = item.SortOrder,
            version = StaffVersion.Encode(item.RowVersion)
        }).ToArray();
    }

    private static async Task<IReadOnlyList<object>> LoadProvidersAsync(AsapDbContext context, int organizationId, CancellationToken cancellationToken)
    {
        var providers = await context.ExternalSearchProviders.AsNoTracking()
            .Where(item => item.OrganizationId == 1)
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var overrides = organizationId == 1
            ? []
            : await context.ExternalSearchProviderOverrides.AsNoTracking()
                .Where(item => item.LibraryOrganizationId == organizationId)
                .ToListAsync(cancellationToken);
        var byId = overrides.ToDictionary(item => item.ExternalSearchProviderId);
        return providers.Select(provider =>
        {
            byId.TryGetValue(provider.Id, out var value);
            return (object)new
            {
                key = provider.ProviderKey,
                id = provider.Id.ToString(),
                isEnabled = value?.IsEnabled ?? provider.IsEnabled,
                label = value?.Label ?? provider.Label,
                urlTemplate = value?.UrlTemplate ?? provider.UrlTemplate,
                system = new { provider.IsEnabled, provider.Label, provider.UrlTemplate },
                overridden = value is not null
            };
        }).ToArray();
    }

    private static async Task<IReadOnlyList<object>> LoadFormatsAsync(AsapDbContext context, int organizationId, CancellationToken cancellationToken)
    {
        var formats = await context.MaterialFormats.AsNoTracking()
            .Where(item => item.OwnerOrganizationId == 1 || item.OwnerOrganizationId == organizationId)
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var overrides = organizationId == 1
            ? []
            : await context.MaterialFormatOverrides.AsNoTracking()
                .Where(item => item.LibraryOrganizationId == organizationId)
                .ToListAsync(cancellationToken);
        var byId = overrides.ToDictionary(item => item.MaterialFormatId);
        return formats.Select(format =>
        {
            byId.TryGetValue(format.Id, out var value);
            return (object)new
            {
                id = format.Id.ToString(),
                code = format.Code,
                ownerOrganizationId = format.OwnerOrganizationId.ToString(),
                label = value?.Label ?? format.Label,
                sortOrder = value?.SortOrder ?? format.SortOrder,
                isEnabled = value?.IsEnabled ?? format.IsEnabled,
                messageBehavior = value?.MessageBehavior ?? format.MessageBehavior,
                message = value?.Message ?? format.Message,
                titleMode = value?.TitleMode ?? format.TitleMode,
                titleLabel = value?.TitleLabel ?? format.TitleLabel,
                authorMode = value?.AuthorMode ?? format.AuthorMode,
                authorLabel = value?.AuthorLabel ?? format.AuthorLabel,
                identifierMode = value?.IdentifierMode ?? format.IdentifierMode,
                identifierLabel = value?.IdentifierLabel ?? format.IdentifierLabel,
                publicationMode = value?.PublicationMode ?? format.PublicationMode,
                publicationLabel = value?.PublicationLabel ?? format.PublicationLabel,
                overridden = value is not null,
                version = value is null ? StaffVersion.Encode(format.RowVersion) : StaffVersion.Encode(value.RowVersion)
            };
        }).ToArray();
    }

    private static async Task<IReadOnlyList<object>> LoadCustomFieldsAsync(AsapDbContext context, int organizationId, CancellationToken cancellationToken)
    {
        var fields = await context.PatronCustomFields.AsNoTracking()
            .Where(item => item.LibraryOrganizationId == organizationId)
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var ids = fields.Select(item => item.Id).ToArray();
        var options = await context.PatronCustomFieldOptions.AsNoTracking()
            .Where(item => ids.Contains(item.PatronCustomFieldId))
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        return fields.Select(field => (object)new
        {
            id = field.Id.ToString(),
            key = field.FieldKey,
            type = field.FieldType,
            label = field.Label,
            helpText = field.HelpText,
            enabled = field.IsEnabled,
            sortOrder = field.SortOrder,
            options = options.Where(item => item.PatronCustomFieldId == field.Id).Select(item => new
            {
                id = item.OptionKey,
                label = item.Label,
                enabled = item.IsEnabled,
                sortOrder = item.SortOrder
            }).ToArray(),
            version = StaffVersion.Encode(field.RowVersion)
        }).ToArray();
    }

    private static async Task<IReadOnlyList<object>> LoadTemplatesAsync(AsapDbContext context, int organizationId, CancellationToken cancellationToken)
    {
        var system = await context.EmailTemplates.AsNoTracking()
            .Where(item => item.OrganizationId == 1)
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var library = organizationId == 1
            ? []
            : await context.EmailTemplates.AsNoTracking()
                .Where(item => item.OrganizationId == organizationId)
                .OrderBy(item => item.SortOrder).ThenBy(item => item.Id)
                .ToListAsync(cancellationToken);
        return system.Concat(library).Select(item => (object)new
        {
            id = item.Id.ToString(),
            organizationId = item.OrganizationId.ToString(),
            templateKey = item.TemplateKey,
            sourceTemplateId = item.SourceTemplateId?.ToString(),
            displayName = item.DisplayName,
            subject = item.SubjectTemplate,
            body = item.BodyTemplate,
            enabled = !item.IsHidden,
            isCustom = item.IsCustom,
            sortOrder = item.SortOrder,
            version = StaffVersion.Encode(item.RowVersion)
        }).ToArray();
    }

    private static async Task<IReadOnlyList<object>> LoadPublicationOptionsAsync(AsapDbContext context, int organizationId, CancellationToken cancellationToken)
    {
        var rows = await context.PublicationOptions.AsNoTracking()
            .Where(item => item.OrganizationId == 1 || item.OrganizationId == organizationId)
            .OrderBy(item => item.OrganizationId).ThenBy(item => item.SortOrder).ThenBy(item => item.Id)
            .Select(item => new { item.OrganizationId, item.OptionKey, item.Label, item.IsEnabled, item.SortOrder })
            .ToListAsync(cancellationToken);
        return rows.Select(item => (object)new
        {
            organizationId = item.OrganizationId,
            id = item.OptionKey,
            label = item.Label,
            enabled = item.IsEnabled,
            sortOrder = item.SortOrder
        }).ToArray();
    }

    private static async Task<IReadOnlyList<string>> LoadCommonCreatorsAsync(AsapDbContext context, int organizationId, CancellationToken cancellationToken)
    {
        var rows = await context.CommonCreatorTerms.AsNoTracking()
            .Where(item => item.OrganizationId == 1 || item.OrganizationId == organizationId)
            .OrderBy(item => item.OrganizationId).ThenBy(item => item.SortOrder).ThenBy(item => item.Id)
            .Select(item => new { item.OrganizationId, item.Value })
            .ToListAsync(cancellationToken);
        var owner = organizationId != 1 && rows.Any(item => item.OrganizationId == organizationId)
            ? organizationId
            : 1;
        return rows.Where(item => item.OrganizationId == owner).Select(item => item.Value).ToArray();
    }

    private static async Task<IReadOnlyList<string>> LoadPatronCodesAsync(AsapDbContext context, int organizationId, CancellationToken cancellationToken)
    {
        var rows = await context.PatronCodeEligibilityMembers.AsNoTracking()
            .Where(item => item.OrganizationId == 1 || item.OrganizationId == organizationId)
            .OrderBy(item => item.OrganizationId).ThenBy(item => item.PatronCodeId)
            .ToListAsync(cancellationToken);
        var owner = organizationId != 1 && rows.Any(item => item.OrganizationId == organizationId)
            ? organizationId
            : 1;
        return rows.Where(item => item.OrganizationId == owner).Select(item => item.PatronCodeId).ToArray();
    }

    private static async Task<IReadOnlyList<object>> LoadAutoClaimRulesAsync(AsapDbContext context, int organizationId, CancellationToken cancellationToken) =>
        await context.FormatAutoClaimRules.AsNoTracking()
            .Where(item => item.LibraryOrganizationId == organizationId)
            .OrderBy(item => item.MaterialFormatId).ThenBy(item => item.Id)
            .Select(item => (object)new
            {
                id = item.Id.ToString(),
                materialFormatId = item.MaterialFormatId.ToString(),
                staffUserId = item.StaffUserId.HasValue ? item.StaffUserId.Value.ToString() : null,
                active = item.IsActive,
                version = StaffVersion.Encode(item.RowVersion)
            })
            .ToArrayAsync(cancellationToken);

    private static async Task<WorkflowSettings?> GetWorkflowAsync(AsapDbContext context, int organizationId, CancellationToken cancellationToken) =>
        await context.WorkflowSettings.AsNoTracking().SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);

    private static async Task<string> ComputeSettingsVersionAsync(AsapDbContext context, int organizationId, CancellationToken cancellationToken)
    {
        var parts = new List<string>();
        async Task AddVersionsAsync<T>(IQueryable<T> query, Func<T, byte[]> version)
        {
            foreach (var item in await query.ToListAsync(cancellationToken))
            {
                parts.Add(Convert.ToHexString(version(item)));
            }
        }

        await AddVersionsAsync(
            context.Organizations.AsNoTracking()
                .Where(item => organizationId == 1 || item.Id == 1 || item.Id == organizationId).OrderBy(item => item.Id),
            item => item.RowVersion);
        await AddVersionsAsync(context.SystemSettings.AsNoTracking().OrderBy(item => item.OrganizationId), item => item.RowVersion);
        await AddVersionsAsync(context.PolarisSettings.AsNoTracking().OrderBy(item => item.OrganizationId), item => item.RowVersion);
        await AddVersionsAsync(context.WorkflowSettings.AsNoTracking().Where(item => item.OrganizationId == 1 || item.OrganizationId == organizationId).OrderBy(item => item.OrganizationId), item => item.RowVersion);
        await AddVersionsAsync(context.PatronSettings.AsNoTracking().Where(item => item.OrganizationId == 1 || item.OrganizationId == organizationId).OrderBy(item => item.OrganizationId), item => item.RowVersion);
        await AddVersionsAsync(context.EmailSettings.AsNoTracking().Where(item => item.OrganizationId == 1 || item.OrganizationId == organizationId).OrderBy(item => item.OrganizationId), item => item.RowVersion);
        await AddVersionsAsync(context.CommonCreatorSets.AsNoTracking().Where(item => item.OrganizationId == 1 || item.OrganizationId == organizationId).OrderBy(item => item.OrganizationId), item => item.RowVersion);
        await AddVersionsAsync(context.PatronCodeEligibilitySets.AsNoTracking().Where(item => item.OrganizationId == 1 || item.OrganizationId == organizationId).OrderBy(item => item.OrganizationId), item => item.RowVersion);
        await AddVersionsAsync(context.PublicationOptionSets.AsNoTracking().Where(item => item.OrganizationId == 1 || item.OrganizationId == organizationId).OrderBy(item => item.OrganizationId), item => item.RowVersion);
        await AddVersionsAsync(context.ExternalSearchProviders.AsNoTracking().OrderBy(item => item.Id), item => item.RowVersion);
        await AddVersionsAsync(context.ExternalSearchProviderOverrides.AsNoTracking().Where(item => item.LibraryOrganizationId == organizationId).OrderBy(item => item.ExternalSearchProviderId), item => item.RowVersion);
        await AddVersionsAsync(context.PatronCustomFields.AsNoTracking().Where(item => item.LibraryOrganizationId == organizationId).OrderBy(item => item.Id), item => item.RowVersion);
        await AddVersionsAsync(context.MaterialFormats.AsNoTracking().Where(item => item.OwnerOrganizationId == 1 || item.OwnerOrganizationId == organizationId).OrderBy(item => item.Id), item => item.RowVersion);
        await AddVersionsAsync(context.MaterialFormatOverrides.AsNoTracking().Where(item => item.LibraryOrganizationId == organizationId).OrderBy(item => item.MaterialFormatId), item => item.RowVersion);
        await AddVersionsAsync(context.MaterialFormatCustomFieldRules.AsNoTracking().Where(item => item.LibraryOrganizationId == organizationId).OrderBy(item => item.MaterialFormatId).ThenBy(item => item.PatronCustomFieldId), item => item.RowVersion);
        await AddVersionsAsync(context.FormatAutoClaimRules.AsNoTracking().Where(item => item.LibraryOrganizationId == organizationId).OrderBy(item => item.Id), item => item.RowVersion);
        await AddVersionsAsync(context.EmailTemplates.AsNoTracking().Where(item => item.OrganizationId == 1 || item.OrganizationId == organizationId).OrderBy(item => item.Id), item => item.RowVersion);
        await AddVersionsAsync(context.Branding.AsNoTracking().Where(item => item.OrganizationId == 1 || item.OrganizationId == organizationId).OrderBy(item => item.OrganizationId), item => item.RowVersion);
        parts.Add(JsonSerializer.Serialize(await context.PatronEmbedAllowedOrigins.AsNoTracking().Where(item => item.OrganizationId == 1).OrderBy(item => item.NormalizedOrigin).Select(item => new { item.Origin, item.NormalizedOrigin }).ToListAsync(cancellationToken)));
        parts.Add(JsonSerializer.Serialize(await context.CommonCreatorTerms.AsNoTracking().Where(item => item.OrganizationId == 1 || item.OrganizationId == organizationId).OrderBy(item => item.Id).Select(item => new { item.OrganizationId, item.Value, item.SortOrder }).ToListAsync(cancellationToken)));
        parts.Add(JsonSerializer.Serialize(await context.PatronCodeEligibilityMembers.AsNoTracking().Where(item => item.OrganizationId == 1 || item.OrganizationId == organizationId).OrderBy(item => item.OrganizationId).ThenBy(item => item.PatronCodeId).Select(item => new { item.OrganizationId, item.PatronCodeId }).ToListAsync(cancellationToken)));
        parts.Add(JsonSerializer.Serialize(await context.PublicationOptions.AsNoTracking().Where(item => item.OrganizationId == 1 || item.OrganizationId == organizationId).OrderBy(item => item.Id).Select(item => new { item.OrganizationId, item.OptionKey, item.Label, item.IsEnabled, item.SortOrder }).ToListAsync(cancellationToken)));
        parts.Add(JsonSerializer.Serialize(await context.PatronCustomFieldOptions.AsNoTracking().Where(item => context.PatronCustomFields.Any(field => field.Id == item.PatronCustomFieldId && field.LibraryOrganizationId == organizationId)).OrderBy(item => item.Id).Select(item => new { item.PatronCustomFieldId, item.OptionKey, item.Label, item.IsEnabled, item.SortOrder }).ToListAsync(cancellationToken)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts))));
    }

    // The writers below deliberately mirror the relational configuration model. Each method is
    // called only after the scope gate above, so a library payload cannot reach system-only rows.
    private async Task ReplaceOriginsIfPresentAsync(
        AsapDbContext context,
        JsonElement payload,
        JsonElement systemSection,
        CancellationToken cancellationToken)
    {
        if (!TryGetAny(systemSection, out var value, "patronEmbedAllowedOrigins") &&
            !TryGetAny(payload, out value, "patronEmbedAllowedOrigins", "origins"))
        {
            return;
        }

        var normalized = ParseValues(value)
            .Select(NormalizeOrigin)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var existing = await context.PatronEmbedAllowedOrigins
            .Where(item => item.OrganizationId == 1)
            .ToListAsync(cancellationToken);
        context.PatronEmbedAllowedOrigins.RemoveRange(existing);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        context.PatronEmbedAllowedOrigins.AddRange(normalized.Select(origin => new PatronEmbedAllowedOrigin
        {
            OrganizationId = 1,
            Origin = origin,
            NormalizedOrigin = origin,
            CreatedUtc = now
        }));
    }

    private async Task ApplyParticipationAsync(
        AsapDbContext context,
        JsonElement payload,
        JsonElement systemSection,
        CancellationToken cancellationToken)
    {
        if (!TryGetAny(systemSection, out var value, "enabledLibraryOrgIds", "enabledLibraries") &&
            !TryGetAny(payload, out value, "enabledLibraryOrgIds", "enabledLibraries"))
        {
            return;
        }

        var enabled = ParseValues(value)
            .Select(item => int.TryParse(item, out var id) ? id : 0)
            .Where(item => item > 1)
            .ToHashSet();
        var organizations = await context.Organizations
            .Where(item => item.Id > 1)
            .ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var revoked = organizations
            .Where(item => item.IsActive && !enabled.Contains(item.Id))
            .Select(item => item.Id)
            .ToArray();
        foreach (var organization in organizations)
        {
            organization.IsActive = enabled.Contains(organization.Id);
        }

        if (revoked.Length > 0)
        {
            var sessions = await context.PatronSessions
                .Where(item => revoked.Contains(item.EffectiveOrganizationId) && item.RevokedUtc == null)
                .ToListAsync(cancellationToken);
            foreach (var session in sessions)
            {
                session.RevokedUtc = now;
            }
        }
    }

    private static async Task ApplyWholeSetsAsync(
        AsapDbContext context,
        int organizationId,
        JsonElement workflow,
        JsonElement patron,
        CancellationToken cancellationToken)
    {
        if (TryGetAny(workflow, out var creators, "commonAuthorsList", "commonCreators", "commonCreatorsList"))
        {
            var values = ParseValues(creators);
            await ReplaceCommonCreatorsAsync(context, organizationId, values, cancellationToken);
        }

        if (TryGetAny(workflow, out var patronCodes, "allowedPatronCodeIds", "patronCodeIds"))
        {
            var values = ParseValues(patronCodes);
            await ReplacePatronCodesAsync(context, organizationId, values, cancellationToken);
        }

        if (TryGetAny(patron, out var publicationOptions, "publicationOptions", "publicationOptionSet"))
        {
            var values = ParseOptions(publicationOptions);
            await ReplacePublicationOptionsAsync(context, organizationId, values, cancellationToken);
        }
    }

    private static async Task ReplaceCommonCreatorsAsync(
        AsapDbContext context,
        int organizationId,
        IReadOnlyList<string> values,
        CancellationToken cancellationToken)
    {
        var existingSet = await context.CommonCreatorSets
            .SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        var terms = await context.CommonCreatorTerms
            .Where(item => item.OrganizationId == organizationId)
            .ToListAsync(cancellationToken);
        if (organizationId != 1 && values.Count == 0)
        {
            context.CommonCreatorTerms.RemoveRange(terms);
            if (existingSet is not null) context.CommonCreatorSets.Remove(existingSet);
            return;
        }

        if (existingSet is null)
        {
            context.CommonCreatorSets.Add(new CommonCreatorSet { OrganizationId = organizationId });
        }
        context.CommonCreatorTerms.RemoveRange(terms);
        context.CommonCreatorTerms.AddRange(values.Select((value, index) => new CommonCreatorTerm
        {
            OrganizationId = organizationId,
            Value = value,
            SortOrder = (index + 1) * 10
        }));
    }

    private static async Task ReplacePatronCodesAsync(
        AsapDbContext context,
        int organizationId,
        IReadOnlyList<string> values,
        CancellationToken cancellationToken)
    {
        var existingSet = await context.PatronCodeEligibilitySets
            .SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        var members = await context.PatronCodeEligibilityMembers
            .Where(item => item.OrganizationId == organizationId)
            .ToListAsync(cancellationToken);
        if (organizationId != 1 && values.Count == 0)
        {
            context.PatronCodeEligibilityMembers.RemoveRange(members);
            if (existingSet is not null) context.PatronCodeEligibilitySets.Remove(existingSet);
            return;
        }

        if (existingSet is null)
        {
            context.PatronCodeEligibilitySets.Add(new PatronCodeEligibilitySet { OrganizationId = organizationId });
        }
        context.PatronCodeEligibilityMembers.RemoveRange(members);
        context.PatronCodeEligibilityMembers.AddRange(values.Select(value => new PatronCodeEligibilityMember
        {
            OrganizationId = organizationId,
            PatronCodeId = value
        }));
    }

    private static async Task ReplacePublicationOptionsAsync(
        AsapDbContext context,
        int organizationId,
        IReadOnlyList<SetOption> values,
        CancellationToken cancellationToken)
    {
        var existingSet = await context.PublicationOptionSets
            .SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        var options = await context.PublicationOptions
            .Where(item => item.OrganizationId == organizationId)
            .ToListAsync(cancellationToken);
        if (organizationId != 1 && values.Count == 0)
        {
            context.PublicationOptions.RemoveRange(options);
            if (existingSet is not null) context.PublicationOptionSets.Remove(existingSet);
            return;
        }

        if (existingSet is null)
        {
            context.PublicationOptionSets.Add(new PublicationOptionSet { OrganizationId = organizationId });
        }
        context.PublicationOptions.RemoveRange(options);
        context.PublicationOptions.AddRange(values.Select(value => new PublicationOption
        {
            OrganizationId = organizationId,
            OptionKey = value.Key,
            Label = value.Label,
            IsEnabled = value.Enabled,
            SortOrder = value.SortOrder
        }));
    }

    private static async Task ApplyProvidersAsync(
        AsapDbContext context,
        int organizationId,
        JsonElement workflow,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var found = TryGetAny(payload, out var providerValue, "providers", "externalSearchProviders") ||
                    TryGetAny(workflow, out providerValue, "providers", "externalSearchProviders");
        var inputs = found
            ? ParseProviders(providerValue)
            : ParseLegacyProviders(workflow, payload);
        if (inputs.Count == 0) return;

        var systemProviders = await context.ExternalSearchProviders
            .Where(item => item.OrganizationId == 1)
            .ToListAsync(cancellationToken);
        foreach (var input in inputs)
        {
            var provider = input.Id.HasValue
                ? systemProviders.SingleOrDefault(item => item.Id == input.Id.Value)
                : systemProviders.SingleOrDefault(item => item.ProviderKey == input.Key);
            if (provider is null)
            {
                if (organizationId != 1)
                {
                    throw new InvalidOperationException("A library may only override an existing external provider.");
                }

                var key = Clean(input.Key);
                var label = Clean(input.Label);
                var url = Clean(input.UrlTemplate);
                if (key is null || label is null || url is null)
                {
                    throw new InvalidOperationException("A system external provider requires a key, label, and URL template.");
                }

                provider = new ExternalSearchProvider
                {
                    OrganizationId = 1,
                    ProviderKey = key,
                    Label = label,
                    UrlTemplate = url,
                    IsEnabled = input.IsEnabled ?? false,
                    SortOrder = input.SortOrder ?? ((systemProviders.Count + 1) * 10)
                };
                context.ExternalSearchProviders.Add(provider);
                systemProviders.Add(provider);
            }

            if (organizationId == 1)
            {
                if (input.IsEnabled.HasValue) provider.IsEnabled = input.IsEnabled.Value;
                if (input.Label is not null) provider.Label = RequireText(input.Label, "Provider label");
                if (input.UrlTemplate is not null) provider.UrlTemplate = RequireText(input.UrlTemplate, "Provider URL template");
                if (input.SortOrder.HasValue) provider.SortOrder = input.SortOrder.Value;
                continue;
            }

            var existing = await context.ExternalSearchProviderOverrides
                .SingleOrDefaultAsync(item => item.LibraryOrganizationId == organizationId && item.ExternalSearchProviderId == provider.Id, cancellationToken);
            if (input.Reset || input.Overridden == false)
            {
                if (existing is not null) context.ExternalSearchProviderOverrides.Remove(existing);
                continue;
            }

            var desiredEnabled = input.IsEnabled ?? provider.IsEnabled;
            var desiredLabel = input.Label is null ? provider.Label : Clean(input.Label) ?? provider.Label;
            var desiredUrl = input.UrlTemplate is null ? provider.UrlTemplate : Clean(input.UrlTemplate) ?? provider.UrlTemplate;
            bool? nextEnabled = desiredEnabled == provider.IsEnabled ? null : desiredEnabled;
            var nextLabel = string.Equals(desiredLabel, provider.Label, StringComparison.Ordinal) ? null : desiredLabel;
            var nextUrl = string.Equals(desiredUrl, provider.UrlTemplate, StringComparison.Ordinal) ? null : desiredUrl;
            if (nextEnabled is null && nextLabel is null && nextUrl is null)
            {
                if (existing is not null) context.ExternalSearchProviderOverrides.Remove(existing);
                continue;
            }

            existing ??= new ExternalSearchProviderOverride
            {
                LibraryOrganizationId = organizationId,
                ExternalSearchProviderId = provider.Id
            };
            var overrideRow = existing;
            if (context.Entry(overrideRow).State == EntityState.Detached)
            {
                context.ExternalSearchProviderOverrides.Add(overrideRow);
            }
            overrideRow.IsEnabled = nextEnabled;
            overrideRow.Label = nextLabel;
            overrideRow.UrlTemplate = nextUrl;
        }
    }

    private async Task ApplyFormatsAsync(
        AsapDbContext context,
        int organizationId,
        JsonElement payload,
        JsonElement patron,
        CancellationToken cancellationToken)
    {
        var hasFormats = TryGetAny(payload, out var value, "formats", "materialFormats") ||
                         TryGetAny(patron, out value, "formats", "materialFormats");
        var hasRules = TryGetAny(payload, out var rulesValue, "formatRules", "patronFormatRules") ||
                       TryGetAny(patron, out rulesValue, "formatRules", "patronFormatRules");
        if (!hasFormats && !hasRules &&
            !TryGetAny(patron, out _, "formatLabels", "formatOrder", "availableFormats")) return;
        if (hasFormats && value.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("formats must be an array.");

        var systemFormats = await context.MaterialFormats
            .Where(item => item.OwnerOrganizationId == 1)
            .ToListAsync(cancellationToken);
        var customFormats = organizationId == 1
            ? []
            : await context.MaterialFormats
                .Where(item => item.OwnerOrganizationId == organizationId)
                .ToListAsync(cancellationToken);
        if (hasFormats)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                await ApplyFormatObjectAsync(context, organizationId, item, systemFormats, customFormats, cancellationToken);
            }
        }
        if (hasRules)
        {
            foreach (var (code, item) in EnumerateFormatRules(rulesValue))
            {
                var target = (organizationId != 1 ? customFormats.SingleOrDefault(format => format.Code == code) : null) ??
                             systemFormats.SingleOrDefault(format => format.Code == code);
                if (target is null) throw new InvalidOperationException($"The format rule for {code} is outside the selected scope.");
                if (organizationId == 1 || target.OwnerOrganizationId == organizationId)
                {
                    ApplyOwnedFormat(target, item, allowCode: false);
                }
                else
                {
                    var overrideRow = await context.MaterialFormatOverrides.SingleOrDefaultAsync(
                        row => row.LibraryOrganizationId == organizationId && row.MaterialFormatId == target.Id,
                        cancellationToken);
                    if (overrideRow is null)
                    {
                        overrideRow = new MaterialFormatOverride
                        {
                            LibraryOrganizationId = organizationId,
                            MaterialFormatId = target.Id
                        };
                        context.MaterialFormatOverrides.Add(overrideRow);
                    }
                    ApplyFormatOverride(overrideRow, target, item);
                    if (IsEmpty(overrideRow)) context.MaterialFormatOverrides.Remove(overrideRow);
                }
            }
        }
        await ApplyLegacyFormatMapsAsync(context, organizationId, patron, systemFormats, customFormats, cancellationToken);
    }

    private static async Task ApplyFormatObjectAsync(
        AsapDbContext context,
        int organizationId,
        JsonElement item,
        IReadOnlyList<MaterialFormat> systemFormats,
        IReadOnlyList<MaterialFormat> customFormats,
        CancellationToken cancellationToken)
    {
        var id = GetLong(item, "id");
        var code = Clean(GetString(item, "code"));
        var owner = GetInt(item, "ownerOrganizationId") ?? (GetBool(item, "custom") == true ? organizationId : 1);
        if (organizationId == 1)
        {
            var target = (id.HasValue ? systemFormats.SingleOrDefault(format => format.Id == id.Value) : null) ??
                         (code is null ? null : systemFormats.SingleOrDefault(format => format.Code == code));
            if (target is null)
            {
                if (owner != 1) throw new InvalidOperationException("System settings cannot create a library-owned format.");
                target = CreateFormat(item, code, 1);
                context.MaterialFormats.Add(target);
                if (systemFormats is List<MaterialFormat> mutableSystemFormats) mutableSystemFormats.Add(target);
            }
            ApplyOwnedFormat(target, item, allowCode: false);
            if (GetBool(item, "deleted") == true || GetBool(item, "delete") == true)
            {
                target.IsEnabled = false;
            }
            return;
        }

        var custom = id.HasValue
            ? customFormats.SingleOrDefault(format => format.Id == id.Value)
            : code is null ? null : customFormats.SingleOrDefault(format => format.Code == code);
        if (owner == organizationId || GetBool(item, "custom") == true)
        {
            if (custom is null)
            {
                if (code is null || systemFormats.Any(format => format.Code == code))
                {
                    throw new InvalidOperationException("A custom format code must be present and must not collide with a system format.");
                }
                custom = CreateFormat(item, code, organizationId);
                context.MaterialFormats.Add(custom);
                if (customFormats is List<MaterialFormat> mutableCustomFormats) mutableCustomFormats.Add(custom);
            }
            ApplyOwnedFormat(custom, item, allowCode: false);
            return;
        }

        var system = (id.HasValue ? systemFormats.SingleOrDefault(format => format.Id == id.Value) : null) ??
                     (code is null ? null : systemFormats.SingleOrDefault(format => format.Code == code));
        if (system is null)
        {
            throw new InvalidOperationException("The selected system format is not available in this library scope.");
        }

        var existing = await context.MaterialFormatOverrides
            .SingleOrDefaultAsync(itemRow => itemRow.LibraryOrganizationId == organizationId && itemRow.MaterialFormatId == system.Id, cancellationToken);
        if (GetBool(item, "reset") == true || GetBool(item, "useSystemDefault") == true || GetBool(item, "overridden") == false)
        {
            if (existing is not null) context.MaterialFormatOverrides.Remove(existing);
            return;
        }
        existing ??= new MaterialFormatOverride
        {
            LibraryOrganizationId = organizationId,
            MaterialFormatId = system.Id
        };
        var wasDetached = context.Entry(existing).State == EntityState.Detached;
        ApplyFormatOverride(existing, system, item);
        if (IsEmpty(existing))
        {
            if (!wasDetached) context.MaterialFormatOverrides.Remove(existing);
        }
        else if (wasDetached)
        {
            context.MaterialFormatOverrides.Add(existing);
        }
    }

    private static async Task ApplyLegacyFormatMapsAsync(
        AsapDbContext context,
        int organizationId,
        JsonElement patron,
        IReadOnlyList<MaterialFormat> systemFormats,
        IReadOnlyList<MaterialFormat> customFormats,
        CancellationToken cancellationToken)
    {
        var labels = TryGetAny(patron, out var labelValue, "formatLabels") && labelValue.ValueKind == JsonValueKind.Object
            ? labelValue
            : default;
        var order = TryGetAny(patron, out var orderValue, "formatOrder") ? ParseValues(orderValue) : [];
        var available = TryGetAny(patron, out var availableValue, "availableFormats")
            ? ParseValues(availableValue).ToHashSet(StringComparer.Ordinal)
            : null;
        if (labels.ValueKind != JsonValueKind.Object && order.Count == 0 && available is null) return;

        var formats = systemFormats.Concat(customFormats).ToArray();
        foreach (var format in formats)
        {
            var itemLabel = labels.ValueKind == JsonValueKind.Object && labels.TryGetProperty(format.Code, out var label)
                ? label.GetString()
                : null;
            var position = order.Select((value, index) => (value, index))
                .Where(item => string.Equals(item.value, format.Code, StringComparison.Ordinal))
                .Select(item => (int?)item.index)
                .FirstOrDefault();
            int? itemOrder = position.HasValue ? (position.Value + 1) * 10 : null;
            var itemEnabled = available?.Contains(format.Code);
            if (organizationId == 1 || format.OwnerOrganizationId == organizationId)
            {
                if (itemLabel is not null) format.Label = RequireText(itemLabel, "Format label");
                if (itemOrder.HasValue) format.SortOrder = itemOrder.Value;
                if (itemEnabled.HasValue) format.IsEnabled = itemEnabled.Value;
            }
            else
            {
                var row = await context.MaterialFormatOverrides.SingleOrDefaultAsync(
                    item => item.LibraryOrganizationId == organizationId && item.MaterialFormatId == format.Id,
                    cancellationToken);
                row ??= new MaterialFormatOverride { LibraryOrganizationId = organizationId, MaterialFormatId = format.Id };
                if (itemLabel is not null) row.Label = Same(itemLabel, format.Label) ? null : Clean(itemLabel);
                if (itemOrder.HasValue) row.SortOrder = itemOrder.Value == format.SortOrder ? null : itemOrder.Value;
                if (itemEnabled.HasValue) row.IsEnabled = itemEnabled.Value == format.IsEnabled ? null : itemEnabled.Value;
                if (IsEmpty(row))
                {
                    if (context.Entry(row).State != EntityState.Detached) context.MaterialFormatOverrides.Remove(row);
                }
                else if (context.Entry(row).State == EntityState.Detached)
                {
                    context.MaterialFormatOverrides.Add(row);
                }
            }
        }
    }

    private static async Task ApplyCustomFieldsAsync(
        AsapDbContext context,
        int organizationId,
        JsonElement patron,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (organizationId == 1) return;
        var hasDefinitions = TryGetAny(payload, out var definitions, "customFields", "additionalFieldDefinitions") ||
                             TryGetAny(patron, out definitions, "customFields", "additionalFieldDefinitions");
        if (hasDefinitions)
        {
            if (definitions.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("customFields must be an array.");
            var existing = await context.PatronCustomFields
                .Where(item => item.LibraryOrganizationId == organizationId)
                .ToListAsync(cancellationToken);
            var keep = new HashSet<long>();
            foreach (var item in definitions.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = GetLong(item, "id");
                var key = Clean(GetString(item, "key") ?? GetString(item, "fieldKey"));
                if (key is null) throw new InvalidOperationException("Each custom field requires a stable key.");
                var field = (id.HasValue ? existing.SingleOrDefault(row => row.Id == id.Value) : null) ??
                            existing.SingleOrDefault(row => row.FieldKey == key);
                var isNewField = field is null;
                field ??= new PatronCustomField
                {
                    LibraryOrganizationId = organizationId,
                    FieldKey = key,
                    FieldType = "text",
                    Label = key,
                    IsEnabled = true,
                    SortOrder = (existing.Count + 1) * 10
                };
                if (isNewField)
                {
                    context.PatronCustomFields.Add(field);
                    // Options reference the generated field key, so materialize the field
                    // before replacing its option set inside this already-open transaction.
                    await context.SaveChangesAsync(cancellationToken);
                }
                keep.Add(field.Id);
                field.FieldKey = key;
                field.FieldType = NormalizeFieldType(GetString(item, "type") ?? GetString(item, "fieldType") ?? field.FieldType);
                field.Label = RequireText(GetString(item, "label") ?? field.Label, "Custom field label");
                if (HasProperty(item, "helpText")) field.HelpText = Clean(GetString(item, "helpText"));
                if (GetBool(item, "enabled").HasValue) field.IsEnabled = GetBool(item, "enabled")!.Value;
                if (GetInt(item, "sortOrder").HasValue) field.SortOrder = GetInt(item, "sortOrder")!.Value;

                if (TryGetAny(item, out var options, "options") && options.ValueKind == JsonValueKind.Array)
                {
                    var oldOptions = await context.PatronCustomFieldOptions
                        .Where(row => row.PatronCustomFieldId == field.Id)
                        .ToListAsync(cancellationToken);
                    context.PatronCustomFieldOptions.RemoveRange(oldOptions);
                    context.PatronCustomFieldOptions.AddRange(ParseOptions(options).Select(option => new PatronCustomFieldOption
                    {
                        PatronCustomFieldId = field.Id,
                        OptionKey = option.Key,
                        Label = option.Label,
                        IsEnabled = option.Enabled,
                        SortOrder = option.SortOrder
                    }));
                }
            }

            var removed = existing.Where(item => item.Id != 0 && !keep.Contains(item.Id)).ToArray();
            if (removed.Length > 0)
            {
                var removedIds = removed.Select(item => item.Id).ToArray();
                context.MaterialFormatCustomFieldRules.RemoveRange(
                    await context.MaterialFormatCustomFieldRules
                        .Where(item => item.LibraryOrganizationId == organizationId && removedIds.Contains(item.PatronCustomFieldId))
                        .ToListAsync(cancellationToken));
                context.PatronCustomFieldOptions.RemoveRange(
                    await context.PatronCustomFieldOptions.Where(item => removedIds.Contains(item.PatronCustomFieldId)).ToListAsync(cancellationToken));
                context.PatronCustomFields.RemoveRange(removed);
            }
        }

        var hasRules = TryGetAny(payload, out var rules, "formatRules", "patronFormatRules") ||
                       TryGetAny(patron, out rules, "formatRules", "patronFormatRules");
        if (!hasRules && (TryGetAny(payload, out var formats, "formats", "materialFormats") ||
                          TryGetAny(patron, out formats, "formats", "materialFormats")) && formats.ValueKind == JsonValueKind.Array)
        {
            hasRules = formats.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Object && HasProperty(item, "customFields"));
            if (hasRules) rules = formats;
        }
        if (!hasRules) return;

        var currentRules = await context.MaterialFormatCustomFieldRules
            .Where(item => item.LibraryOrganizationId == organizationId)
            .ToListAsync(cancellationToken);
        context.MaterialFormatCustomFieldRules.RemoveRange(currentRules);
        var fieldsByKey = await context.PatronCustomFields
            .Where(item => item.LibraryOrganizationId == organizationId)
            .ToDictionaryAsync(item => item.FieldKey, StringComparer.Ordinal, cancellationToken);
        var formatsInScope = await context.MaterialFormats
            .Where(item => item.OwnerOrganizationId == 1 || item.OwnerOrganizationId == organizationId)
            .ToListAsync(cancellationToken);
        foreach (var (formatCode, formatRule) in EnumerateFormatRules(rules))
        {
            var format = formatsInScope.SingleOrDefault(item => item.Code == formatCode);
            if (format is null) throw new InvalidOperationException($"Format rule {formatCode} is outside the selected library scope.");
            if (!TryGetAny(formatRule, out var custom, "customFields") || custom.ValueKind != JsonValueKind.Object) continue;
            foreach (var fieldProperty in custom.EnumerateObject())
            {
                if (!fieldsByKey.TryGetValue(fieldProperty.Name, out var field)) continue;
                var rule = fieldProperty.Value;
                var mode = GetString(rule, "mode") ?? "hidden";
                if (mode is not ("required" or "optional" or "hidden"))
                {
                    throw new InvalidOperationException("Custom field mode must be required, optional, or hidden.");
                }
                if (mode == "hidden") continue;
                context.MaterialFormatCustomFieldRules.Add(new MaterialFormatCustomFieldRule
                {
                    LibraryOrganizationId = organizationId,
                    MaterialFormatId = format.Id,
                    PatronCustomFieldId = field.Id,
                    Mode = mode,
                    LabelOverride = Clean(GetString(rule, "labelOverride") ?? GetString(rule, "label"))
                });
            }
        }
    }

    private async Task ApplyAutoClaimRulesAsync(
        AsapDbContext context,
        int organizationId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (organizationId == 1 ||
            !TryGetAny(payload, out var value, "autoClaimRules", "formatClaimRules")) return;
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("autoClaimRules must be an array.");

        var formats = await context.MaterialFormats
            .Where(item => item.OwnerOrganizationId == 1 || item.OwnerOrganizationId == organizationId)
            .ToListAsync(cancellationToken);
        var active = await context.FormatAutoClaimRules
            .Where(item => item.LibraryOrganizationId == organizationId && item.IsActive)
            .ToListAsync(cancellationToken);
        var desired = new Dictionary<long, long?>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var formatId = GetLong(item, "materialFormatId") ?? GetLong(item, "formatId");
            if (!formatId.HasValue || formats.All(format => format.Id != formatId.Value))
            {
                throw new InvalidOperationException("Each auto-claim rule must reference a format in the selected library scope.");
            }
            if (GetBool(item, "active") == false || GetBool(item, "isActive") == false)
            {
                desired.Remove(formatId.Value);
                continue;
            }
            var staffId = GetLong(item, "staffUserId") ?? GetLong(item, "staffId");
            if (!staffId.HasValue) throw new InvalidOperationException("An active auto-claim rule requires a staff user.");
            if (desired.ContainsKey(formatId.Value)) throw new InvalidOperationException("Only one active auto-claim rule is allowed per format.");
            var staff = context.StaffUsers.Local.SingleOrDefault(itemRow => itemRow.Id == staffId.Value);
            if (staff is null || !staffEligibility.IsAssignmentEligible(staff, organizationId))
            {
                throw new InvalidOperationException("The auto-claim staff user is not active or is outside the selected scope.");
            }
            desired[formatId.Value] = staffId.Value;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var rule in active)
        {
            if (!desired.TryGetValue(rule.MaterialFormatId, out var staffId) || staffId != rule.StaffUserId)
            {
                rule.IsActive = false;
                rule.DeactivatedUtc = now;
            }
        }
        foreach (var pair in desired)
        {
            if (active.Any(rule => rule.MaterialFormatId == pair.Key && rule.IsActive && rule.StaffUserId == pair.Value)) continue;
            context.FormatAutoClaimRules.Add(new FormatAutoClaimRule
            {
                LibraryOrganizationId = organizationId,
                MaterialFormatId = pair.Key,
                StaffUserId = pair.Value,
                IsActive = true,
                CreatedUtc = now
            });
        }
    }

    private static async Task ApplyTemplatesAsync(
        AsapDbContext context,
        int organizationId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (TryGetAny(payload, out var templates, "templates", "emailTemplates"))
        {
            if (templates.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("templates must be an array.");
            foreach (var item in templates.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    await ApplyTemplateObjectAsync(context, organizationId, item, null, cancellationToken);
                }
            }
            return;
        }

        if (!TryGetAny(payload, out var emails, "emails", "email")) return;
        if (emails.ValueKind != JsonValueKind.Object) return;
        foreach (var property in emails.EnumerateObject())
        {
            if (property.Name is "fromAddress" or "fromName" or "postmarkToken" or "serverToken" or "rejection_templates") continue;
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                await ApplyTemplateObjectAsync(context, organizationId, property.Value, property.Name, cancellationToken);
            }
        }
        if (emails.TryGetProperty("rejection_templates", out var rejectionTemplates) && rejectionTemplates.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in rejectionTemplates.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    await ApplyTemplateObjectAsync(context, organizationId, item, null, cancellationToken);
                }
            }
        }
    }

    private static async Task ApplyTemplateObjectAsync(
        AsapDbContext context,
        int organizationId,
        JsonElement item,
        string? legacyKey,
        CancellationToken cancellationToken)
    {
        var itemOrganization = GetInt(item, "organizationId");
        if (itemOrganization.HasValue && itemOrganization.Value != organizationId && organizationId != 1) return;
        var key = Clean(GetString(item, "templateKey") ?? GetString(item, "key") ?? legacyKey);
        var sourceId = GetLong(item, "sourceTemplateId") ?? GetLong(item, "sourceId");
        var reset = GetBool(item, "reset") == true || GetBool(item, "useSystemDefault") == true || GetBool(item, "overridden") == false;
        var isCustom = GetBool(item, "isCustom") == true || GetBool(item, "custom") == true;
        if (organizationId == 1)
        {
            if (sourceId.HasValue || isCustom) throw new InvalidOperationException("System templates cannot use library lineage or custom ownership.");
            if (key is null) throw new InvalidOperationException("A system template requires a template key.");
            var template = await context.EmailTemplates.SingleOrDefaultAsync(
                row => row.OrganizationId == 1 && row.TemplateKey == key, cancellationToken);
            template ??= new EmailTemplate
            {
                OrganizationId = 1,
                TemplateKey = key,
                IsCustom = false,
                IsHidden = false
            };
            if (template.Id == 0) context.EmailTemplates.Add(template);
            ApplyTemplateContent(template, item, requireContent: true);
            return;
        }

        if (isCustom || (!sourceId.HasValue && key is not null && GetBool(item, "libraryCustom") == true))
        {
            if (key is null) throw new InvalidOperationException("A custom template requires a template key.");
            var custom = await context.EmailTemplates.SingleOrDefaultAsync(
                row => row.OrganizationId == organizationId && row.IsCustom && row.TemplateKey == key, cancellationToken);
            if (reset)
            {
                if (custom is not null) context.EmailTemplates.Remove(custom);
                return;
            }
            custom ??= new EmailTemplate
            {
                OrganizationId = organizationId,
                TemplateKey = key,
                IsCustom = true,
                IsHidden = false
            };
            if (custom.Id == 0) context.EmailTemplates.Add(custom);
            ApplyTemplateContent(custom, item, requireContent: true);
            return;
        }

        EmailTemplate? source = sourceId.HasValue
            ? await context.EmailTemplates.SingleOrDefaultAsync(row => row.Id == sourceId.Value && row.OrganizationId == 1, cancellationToken)
            : key is null
                ? null
                : await context.EmailTemplates.SingleOrDefaultAsync(row => row.OrganizationId == 1 && row.TemplateKey == key, cancellationToken);
        if (source is null) throw new InvalidOperationException("A library template override must reference a system template.");
        var overrideRow = await context.EmailTemplates.SingleOrDefaultAsync(
            row => row.OrganizationId == organizationId && row.SourceTemplateId == source.Id, cancellationToken);
        if (reset)
        {
            if (overrideRow is not null) context.EmailTemplates.Remove(overrideRow);
            return;
        }
        overrideRow ??= new EmailTemplate
        {
            OrganizationId = organizationId,
            TemplateKey = source.TemplateKey,
            SourceTemplateId = source.Id,
            IsCustom = false,
            IsHidden = false
        };
        if (overrideRow.Id == 0) context.EmailTemplates.Add(overrideRow);
        ApplyTemplateContent(overrideRow, item, requireContent: false);
    }

    private static void ApplyTemplateContent(EmailTemplate template, JsonElement item, bool requireContent)
    {
        if (HasProperty(item, "subject")) template.SubjectTemplate = GetString(item, "subject");
        else if (HasProperty(item, "subjectTemplate")) template.SubjectTemplate = GetString(item, "subjectTemplate");
        if (HasProperty(item, "body")) template.BodyTemplate = GetString(item, "body");
        else if (HasProperty(item, "bodyTemplate")) template.BodyTemplate = GetString(item, "bodyTemplate");
        if (HasProperty(item, "displayName")) template.DisplayName = Clean(GetString(item, "displayName"));
        if (GetBool(item, "enabled").HasValue) template.IsHidden = !GetBool(item, "enabled")!.Value;
        if (GetBool(item, "hidden").HasValue) template.IsHidden = GetBool(item, "hidden")!.Value;
        if (GetBool(item, "isHidden").HasValue) template.IsHidden = GetBool(item, "isHidden")!.Value;
        if (GetInt(item, "sortOrder").HasValue) template.SortOrder = GetInt(item, "sortOrder")!.Value;
        if (requireContent && (string.IsNullOrWhiteSpace(template.SubjectTemplate) || string.IsNullOrWhiteSpace(template.BodyTemplate)))
        {
            throw new InvalidOperationException("A system or custom email template requires both subject and body content.");
        }
    }

    private async Task ApplyBrandingAsync(
        AsapDbContext context,
        int organizationId,
        JsonElement patron,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var found = TryGetAny(payload, out var branding, "branding") || TryGetAny(patron, out branding, "branding");
        if (!found)
        {
            if (!HasProperty(patron, "logoAlt") && !HasProperty(patron, "logoAltText")) return;
            branding = patron;
        }

        var hasLogoData = HasProperty(branding, "logoData");
        var hasAlt = HasProperty(branding, "altText") || HasProperty(branding, "logoAlt") || HasProperty(branding, "logoAltText");
        var clearLogo = GetBool(branding, "clearLogo") == true || GetBool(branding, "removeLogo") == true;
        if (!hasLogoData && !hasAlt && !clearLogo) return;
        var row = await context.Branding.SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        row ??= new Branding { OrganizationId = organizationId };
        if (row.OrganizationId == organizationId && context.Entry(row).State == EntityState.Detached)
        {
            context.Branding.Add(row);
        }
        if (hasAlt)
        {
            row.LogoAltText = Clean(GetString(branding, "altText") ?? GetString(branding, "logoAlt") ?? GetString(branding, "logoAltText"));
        }
        if (clearLogo)
        {
            row.LogoData = null;
            row.LogoContentType = null;
            row.LogoFileName = null;
        }
        if (hasLogoData && !clearLogo)
        {
            var encoded = Clean(GetString(branding, "logoData"));
            if (encoded is null)
            {
                throw new InvalidOperationException("logoData must contain a base64 encoded PNG, JPEG, or GIF image.");
            }

            byte[] data;
            try { data = Convert.FromBase64String(encoded); }
            catch (FormatException) { throw new InvalidOperationException("logoData must be valid base64."); }
            var contentType = Clean(GetString(branding, "contentType") ?? GetString(branding, "logoContentType"));
            if (!LogoImageValidator.TryValidate(data, contentType, out var logoInfo, out var logoError))
            {
                throw new InvalidOperationException(logoError);
            }
            row.LogoData = data;
            row.LogoContentType = logoInfo!.ContentType;
            row.LogoFileName = Clean(GetString(branding, "fileName") ?? GetString(branding, "logoFileName")) ?? "logo";
        }
        row.UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime;
    }

    private static async Task RemoveEmptyOverrideRowsAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken)
    {
        var workflows = await context.WorkflowSettings.Where(item => item.OrganizationId == organizationId).ToListAsync(cancellationToken);
        context.WorkflowSettings.RemoveRange(workflows.Where(IsEmpty));
        var patrons = await context.PatronSettings.Where(item => item.OrganizationId == organizationId).ToListAsync(cancellationToken);
        context.PatronSettings.RemoveRange(patrons.Where(IsEmpty));
        var emails = await context.EmailSettings.Where(item => item.OrganizationId == organizationId).ToListAsync(cancellationToken);
        context.EmailSettings.RemoveRange(emails.Where(IsEmpty));
        var providers = await context.ExternalSearchProviderOverrides.Where(item => item.LibraryOrganizationId == organizationId).ToListAsync(cancellationToken);
        context.ExternalSearchProviderOverrides.RemoveRange(providers.Where(IsEmpty));
        var formats = await context.MaterialFormatOverrides.Where(item => item.LibraryOrganizationId == organizationId).ToListAsync(cancellationToken);
        context.MaterialFormatOverrides.RemoveRange(formats.Where(IsEmpty));
        var branding = await context.Branding.SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        if (branding is not null && IsEmpty(branding)) context.Branding.Remove(branding);
    }

    private static bool IsEmpty(WorkflowSettings value) =>
        value.SuggestionLimit is null && value.SuggestionLimitMessage is null &&
        value.OutstandingTimeoutEnabled is null && value.OutstandingTimeoutDays is null && value.OutstandingTimeoutSendEmail is null &&
        value.OutstandingTimeoutRejectionTemplateId is null && value.HoldPickupTimeoutEnabled is null && value.HoldPickupTimeoutDays is null &&
        value.PendingHoldTimeoutEnabled is null && value.PendingHoldTimeoutDays is null && value.AdditionalCopyTimeoutEnabled is null &&
        value.AdditionalCopyTimeoutDays is null && value.AutoPromote is null && value.CommonAuthorsEnabled is null &&
        value.CommonAuthorsLabel is null && value.CommonAuthorsHelp is null && value.CommonAuthorsMessage is null &&
        value.AllowPatronAutoholdOptOut is null && value.AllowAnyRegisteredCardLogin is null &&
        value.PatronCodeEligibilityEnabled is null && value.PatronCodeEligibilityMessage is null;

    private static bool IsEmpty(PatronSettings value) =>
        PatronTextValues(value).All(item => item is null);

    private static IEnumerable<string?> PatronTextValues(PatronSettings value) =>
    [
        value.PageTitle, value.BarcodeLabel, value.PinLabel, value.LoginPrompt, value.LoginNote,
        value.SuggestionFormNote, value.NoEmailMessage, value.SuccessTitle, value.SuccessMessage,
        value.AlreadySubmittedMessage, value.EbookMessage, value.EaudiobookMessage,
        value.SuggestionStatusLabel, value.OutstandingPurchaseStatusLabel, value.PendingHoldStatusLabel,
        value.HoldPlacedStatusLabel, value.ClosedStatusLabel, value.RejectedStatusLabel,
        value.HoldCompletedStatusLabel, value.HoldNotPickedUpStatusLabel, value.ManualStatusLabel,
        value.SilentStatusLabel
    ];

    private static bool IsEmpty(EmailSettings value) =>
        value.ProtectedServerToken is null && value.FromAddress is null && value.FromName is null;

    private static bool IsEmpty(ExternalSearchProviderOverride value) =>
        value.IsEnabled is null && value.Label is null && value.UrlTemplate is null;

    private static bool IsEmpty(MaterialFormatOverride value) =>
        value.Label is null && value.SortOrder is null && value.IsEnabled is null && value.MessageBehavior is null &&
        value.Message is null && value.TitleMode is null && value.TitleLabel is null && value.AuthorMode is null &&
        value.AuthorLabel is null && value.IdentifierMode is null && value.IdentifierLabel is null &&
        value.PublicationMode is null && value.PublicationLabel is null;

    private static bool IsEmpty(Branding value) =>
        value.LogoData is null && value.LogoContentType is null && value.LogoFileName is null && value.LogoAltText is null;

    private static void ApplyOwnedFormat(MaterialFormat row, JsonElement item, bool allowCode)
    {
        if (allowCode && HasProperty(item, "code")) row.Code = RequireText(GetString(item, "code"), "Format code");
        if (HasProperty(item, "label")) row.Label = RequireText(GetString(item, "label"), "Format label");
        if (GetInt(item, "sortOrder").HasValue) row.SortOrder = GetInt(item, "sortOrder")!.Value;
        var enabled = GetBool(item, "isEnabled") ?? GetBool(item, "enabled");
        if (enabled.HasValue) row.IsEnabled = enabled.Value;
        if (GetBool(item, "deleted") == true || GetBool(item, "delete") == true) row.IsEnabled = false;
        if (HasProperty(item, "messageBehavior")) row.MessageBehavior = NormalizeMessageBehavior(GetString(item, "messageBehavior"));
        if (HasProperty(item, "message")) row.Message = Clean(GetString(item, "message"));
        ApplyOwnedField(item, "title", value => row.TitleMode = value.Mode, value => row.TitleLabel = value.Label, row.TitleMode ?? "required", row.TitleLabel ?? "Title", forceRequired: true);
        ApplyOwnedField(item, "author", value => row.AuthorMode = value.Mode, value => row.AuthorLabel = value.Label, row.AuthorMode ?? "optional", row.AuthorLabel ?? "Author", forceRequired: false);
        ApplyOwnedField(item, "identifier", value => row.IdentifierMode = value.Mode, value => row.IdentifierLabel = value.Label, row.IdentifierMode ?? "optional", row.IdentifierLabel ?? "Identifier number", forceRequired: false);
        ApplyOwnedField(item, "publication", value => row.PublicationMode = value.Mode, value => row.PublicationLabel = value.Label, row.PublicationMode ?? "optional", row.PublicationLabel ?? "Publication Timing", forceRequired: false);
    }

    private static MaterialFormat CreateFormat(JsonElement item, string? code, int ownerOrganizationId)
    {
        var normalizedCode = RequireText(code, "Format code");
        var row = new MaterialFormat
        {
            OwnerOrganizationId = ownerOrganizationId,
            Code = normalizedCode,
            Label = Clean(GetString(item, "label")) ?? normalizedCode,
            SortOrder = GetInt(item, "sortOrder") ?? 10,
            IsEnabled = GetBool(item, "isEnabled") ?? GetBool(item, "enabled") ?? true,
            MessageBehavior = "none",
            TitleMode = "required",
            TitleLabel = "Title",
            AuthorMode = "optional",
            AuthorLabel = "Author",
            IdentifierMode = "optional",
            IdentifierLabel = "Identifier number",
            PublicationMode = "optional",
            PublicationLabel = "Publication Timing"
        };
        ApplyOwnedFormat(row, item, allowCode: false);
        return row;
    }

    private static void ApplyFormatOverride(MaterialFormatOverride row, MaterialFormat baseline, JsonElement item)
    {
        if (HasProperty(item, "label")) row.Label = Same(GetString(item, "label"), baseline.Label) ? null : Clean(GetString(item, "label"));
        if (GetInt(item, "sortOrder").HasValue) row.SortOrder = GetInt(item, "sortOrder") == baseline.SortOrder ? null : GetInt(item, "sortOrder");
        var enabled = GetBool(item, "isEnabled") ?? GetBool(item, "enabled");
        if (enabled.HasValue) row.IsEnabled = enabled.Value == baseline.IsEnabled ? null : enabled.Value;
        if (HasProperty(item, "messageBehavior"))
        {
            var value = NormalizeMessageBehavior(GetString(item, "messageBehavior"));
            row.MessageBehavior = Same(value, baseline.MessageBehavior ?? "none") ? null : value;
        }
        if (HasProperty(item, "message"))
        {
            var value = Clean(GetString(item, "message"));
            row.Message = Same(value, Clean(baseline.Message)) ? null : value;
        }
        ApplyOverrideField(item, "title", baseline.TitleMode ?? "required", baseline.TitleLabel ?? "Title", value => row.TitleMode = value.Mode, value => row.TitleLabel = value.Label);
        ApplyOverrideField(item, "author", baseline.AuthorMode ?? "optional", baseline.AuthorLabel ?? "Author", value => row.AuthorMode = value.Mode, value => row.AuthorLabel = value.Label);
        ApplyOverrideField(item, "identifier", baseline.IdentifierMode ?? "optional", baseline.IdentifierLabel ?? "Identifier number", value => row.IdentifierMode = value.Mode, value => row.IdentifierLabel = value.Label);
        ApplyOverrideField(item, "publication", baseline.PublicationMode ?? "optional", baseline.PublicationLabel ?? "Publication Timing", value => row.PublicationMode = value.Mode, value => row.PublicationLabel = value.Label);
    }

    private static void ApplyOwnedField(
        JsonElement item,
        string name,
        Action<(string Mode, string Label)> modeSetter,
        Action<(string Mode, string Label)> labelSetter,
        string defaultMode,
        string defaultLabel,
        bool forceRequired)
    {
        if (!TryGetAny(item, out var field, name) || field.ValueKind != JsonValueKind.Object) return;
        var mode = NormalizeFieldMode(GetString(field, "mode") ?? defaultMode, forceRequired);
        var label = Clean(GetString(field, "label")) ?? defaultLabel;
        var value = (Mode: mode, Label: label);
        modeSetter(value);
        labelSetter(value);
    }

    private static void ApplyOverrideField(
        JsonElement item,
        string name,
        string baselineMode,
        string baselineLabel,
        Action<(string Mode, string Label)> modeSetter,
        Action<(string Mode, string Label)> labelSetter)
    {
        if (!TryGetAny(item, out var field, name) || field.ValueKind != JsonValueKind.Object) return;
        var mode = NormalizeFieldMode(GetString(field, "mode") ?? baselineMode, name == "title");
        var label = Clean(GetString(field, "label")) ?? baselineLabel;
        modeSetter((Same(mode, baselineMode) ? null! : mode, Same(label, baselineLabel) ? null! : label));
        labelSetter((Same(mode, baselineMode) ? null! : mode, Same(label, baselineLabel) ? null! : label));
    }

    private static string NormalizeMessageBehavior(string? value)
    {
        var normalized = Clean(value) ?? "none";
        return normalized is "none" or "message" or "ebookMessage" or "eaudiobookMessage"
            ? normalized
            : throw new InvalidOperationException("Message behavior is invalid.");
    }

    private static string NormalizeFieldMode(string value, bool forceRequired)
    {
        var normalized = Clean(value) ?? "optional";
        if (forceRequired) return "required";
        return normalized is "required" or "optional" or "hidden"
            ? normalized
            : throw new InvalidOperationException("Format field mode is invalid.");
    }

    private static string NormalizeFieldType(string value) =>
        value is "text" or "textarea" or "select"
            ? value
            : throw new InvalidOperationException("Custom field type is invalid.");

    private static IReadOnlyList<ProviderInput> ParseProviders(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return [];
        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => new ProviderInput(
                Clean(GetString(item, "key") ?? GetString(item, "providerKey")),
                GetLong(item, "id"),
                GetBool(item, "isEnabled") ?? GetBool(item, "enabled"),
                GetString(item, "label"),
                GetString(item, "urlTemplate") ?? GetString(item, "url"),
                GetBool(item, "reset") == true || GetBool(item, "useSystemDefault") == true,
                GetBool(item, "overridden"),
                GetInt(item, "sortOrder")))
            .ToArray();
    }

    private static IReadOnlyList<ProviderInput> ParseLegacyProviders(JsonElement workflow, JsonElement payload)
    {
        var result = new List<ProviderInput>();
        for (var index = 1; index <= 4; index++)
        {
            var enabled = GetBool(workflow, $"externalSearch{index}Enabled") ?? GetBool(payload, $"externalSearch{index}Enabled");
            var label = GetString(workflow, $"externalSearch{index}Label") ?? GetString(payload, $"externalSearch{index}Label");
            var url = GetString(workflow, $"externalSearch{index}UrlTemplate") ?? GetString(payload, $"externalSearch{index}UrlTemplate");
            if (!enabled.HasValue && label is null && url is null) continue;
            result.Add(new ProviderInput($"external_search_{index}", null, enabled, label, url, false, null, index * 10));
        }
        return result;
    }

    private static IReadOnlyList<SetOption> ParseOptions(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? string.Empty;
            if (text.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(text);
                return ParseOptions(document.RootElement);
            }
            return ParseValues(value).Select((label, index) => new SetOption(OptionKey(label, index), label, true, (index + 1) * 10)).ToArray();
        }
        if (value.ValueKind != JsonValueKind.Array) return [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SetOption>();
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            var label = item.ValueKind == JsonValueKind.String
                ? Clean(item.GetString())
                : Clean(GetString(item, "label") ?? GetString(item, "value") ?? GetString(item, "name"));
            if (label is null) continue;
            var key = Clean(GetString(item, "id") ?? GetString(item, "key")) ?? OptionKey(label, index);
            if (!seen.Add(key)) throw new InvalidOperationException("Set option IDs must be unique.");
            result.Add(new SetOption(key, label, GetBool(item, "enabled") ?? true, GetInt(item, "sortOrder") ?? ((index + 1) * 10)));
            index++;
        }
        return result;
    }

    private static IReadOnlyList<string> ParseValues(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? string.Empty;
            return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Clean)
                .Where(item => item is not null)
                .Select(item => item!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        if (value.ValueKind != JsonValueKind.Array) return [];
        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                ? Clean(item.GetString())
                : Clean(GetString(item, "id") ?? GetString(item, "key") ?? GetString(item, "value") ?? GetString(item, "name")))
            .Where(item => item is not null)
            .Select(item => item!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<(string Code, JsonElement Rule)> EnumerateFormatRules(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject()) yield return (property.Name, property.Value);
            yield break;
        }
        if (value.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in value.EnumerateArray())
        {
            var code = Clean(GetString(item, "code") ?? GetString(item, "format"));
            if (code is not null) yield return (code, item);
        }
    }

    private static string NormalizeOrigin(string value)
    {
        var normalized = Clean(value) ?? throw new InvalidOperationException("Embed origins cannot be blank.");
        if (normalized.StartsWith("https://*.", StringComparison.OrdinalIgnoreCase))
        {
            if (normalized.Contains('/', StringComparison.Ordinal) || normalized.Contains('?', StringComparison.Ordinal) || normalized.Contains('#', StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Wildcard embed origins may not include a path or query.");
            }
            return "https://*." + normalized[10..].ToLowerInvariant();
        }
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            (uri.Scheme == "http" && uri.Host is not ("localhost" or "127.0.0.1" or "[::1]")))
        {
            throw new InvalidOperationException("Embed origins must be HTTPS origins, with localhost allowed for HTTP development origins.");
        }
        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        return $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}{port}";
    }

    private static string OptionKey(string label, int index) =>
        new string(label.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray()).Trim('_') switch
        {
            { Length: > 0 } value => value,
            _ => $"option_{index + 1}"
        };

    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);

    private static string RequireText(string? value, string label) =>
        Clean(value) ?? throw new InvalidOperationException($"{label} cannot be blank.");

    private sealed record SetOption(string Key, string Label, bool Enabled, int SortOrder);

    private sealed record ProviderInput(
        string? Key,
        long? Id,
        bool? IsEnabled,
        string? Label,
        string? UrlTemplate,
        bool Reset,
        bool? Overridden,
        int? SortOrder);

    private sealed record EffectiveRejectionTemplate(
        long ReferenceId,
        long SourceTemplateId,
        string TemplateKey,
        bool IsHidden,
        string? Subject,
        string? Body);

    private static async Task<long?> ResolveTemplateReferenceAsync(
        AsapDbContext context,
        string? value,
        int organizationId,
        CancellationToken cancellationToken)
    {
        var normalized = Clean(value);
        if (normalized is null) return null;
        var rows = await LoadTemplateRowsIncludingPendingAsync(context, cancellationToken);
        EffectiveRejectionTemplate? template;
        if (long.TryParse(normalized, out var id))
        {
            template = ResolveEffectiveTemplate(rows, id, organizationId);
        }
        else
        {
            var forceSystem = normalized.StartsWith("system:", StringComparison.OrdinalIgnoreCase);
            var key = forceSystem ? normalized[7..] : normalized;
            if (!forceSystem && organizationId != 1)
            {
                var custom = rows.Where(item => item.OrganizationId == organizationId &&
                        item.IsCustom && string.Equals(item.TemplateKey, key, StringComparison.Ordinal))
                    .ToArray();
                if (custom.Length > 1)
                {
                    throw new InvalidOperationException("The timeout rejection template reference is ambiguous in this library scope.");
                }
                template = custom.Length == 1
                    ? ResolveEffectiveTemplate(rows, custom[0].Id, organizationId)
                    : null;
                if (template is null)
                {
                    var system = rows.Where(item => item.OrganizationId == 1 &&
                            string.Equals(item.TemplateKey, key, StringComparison.Ordinal))
                        .ToArray();
                    if (system.Length > 1)
                    {
                        throw new InvalidOperationException("The timeout rejection template reference is ambiguous at system scope.");
                    }
                    template = system.Length == 1
                        ? ResolveEffectiveTemplate(rows, system[0].Id, organizationId)
                        : null;
                }
            }
            else
            {
                var system = rows.Where(item => item.OrganizationId == 1 &&
                        string.Equals(item.TemplateKey, key, StringComparison.Ordinal))
                    .ToArray();
                if (system.Length > 1)
                {
                    throw new InvalidOperationException("The timeout rejection template reference is ambiguous at system scope.");
                }
                template = system.Length == 1
                    ? ResolveEffectiveTemplate(rows, system[0].Id, organizationId)
                    : null;
            }
        }
        if (template is null || !IsEligibleRejectionTemplate(template))
        {
            throw new InvalidOperationException("The timeout rejection template is unavailable, hidden, or outside the selected configuration scope.");
        }
        return template.ReferenceId;
    }

    private static bool IsEligibleRejectionTemplate(EffectiveRejectionTemplate template) =>
        template.TemplateKey.StartsWith("rejection:", StringComparison.OrdinalIgnoreCase) &&
        !template.IsHidden &&
        !string.IsNullOrWhiteSpace(template.Subject) &&
        !string.IsNullOrWhiteSpace(template.Body);

    private static EffectiveRejectionTemplate? ResolveEffectiveTemplate(
        IReadOnlyList<EmailTemplate> rows,
        long requestedId,
        int organizationId)
    {
        var requested = rows.SingleOrDefault(item => item.Id == requestedId);
        if (requested is null) return null;
        if (requested.OrganizationId == 1)
        {
            var overrideRow = organizationId == 1
                ? null
                : rows.SingleOrDefault(item => item.OrganizationId == organizationId &&
                    !item.IsCustom && item.SourceTemplateId == requested.Id);
            return ToEffectiveTemplate(requested, overrideRow, requested.Id);
        }
        if (requested.OrganizationId != organizationId) return null;
        if (requested.IsCustom || requested.SourceTemplateId is null)
        {
            return new EffectiveRejectionTemplate(
                requested.Id,
                requested.Id,
                requested.TemplateKey,
                requested.IsHidden,
                Clean(requested.SubjectTemplate),
                Clean(requested.BodyTemplate));
        }
        var source = rows.SingleOrDefault(item => item.OrganizationId == 1 && item.Id == requested.SourceTemplateId.Value);
        return source is null ? null : ToEffectiveTemplate(source, requested, requested.Id);
    }

    private static EffectiveRejectionTemplate ToEffectiveTemplate(
        EmailTemplate source,
        EmailTemplate? overrideRow,
        long referenceId) => new(
        referenceId,
        source.Id,
        source.TemplateKey,
        source.IsHidden || overrideRow?.IsHidden == true,
        Clean(overrideRow?.SubjectTemplate) ?? Clean(source.SubjectTemplate),
        Clean(overrideRow?.BodyTemplate) ?? Clean(source.BodyTemplate));

    private static async Task<List<EmailTemplate>> LoadTemplateRowsIncludingPendingAsync(
        AsapDbContext context,
        CancellationToken cancellationToken)
    {
        var rows = await context.EmailTemplates.ToListAsync(cancellationToken);
        foreach (var entry in context.ChangeTracker.Entries<EmailTemplate>())
        {
            var existing = entry.Entity.Id == 0
                ? null
                : rows.SingleOrDefault(item => item.Id == entry.Entity.Id);
            if (entry.State == EntityState.Deleted)
            {
                if (existing is not null) rows.Remove(existing);
                continue;
            }
            if (existing is not null)
            {
                var index = rows.IndexOf(existing);
                rows[index] = entry.Entity;
            }
            else if (entry.State == EntityState.Added)
            {
                rows.Add(entry.Entity);
            }
        }
        return rows;
    }

    private static async Task<List<WorkflowSettings>> LoadWorkflowRowsIncludingPendingAsync(
        AsapDbContext context,
        CancellationToken cancellationToken)
    {
        var rows = await context.WorkflowSettings.ToListAsync(cancellationToken);
        foreach (var entry in context.ChangeTracker.Entries<WorkflowSettings>())
        {
            var existing = rows.SingleOrDefault(item => item.OrganizationId == entry.Entity.OrganizationId);
            if (entry.State == EntityState.Deleted)
            {
                if (existing is not null) rows.Remove(existing);
                continue;
            }
            if (existing is not null)
            {
                var index = rows.IndexOf(existing);
                rows[index] = entry.Entity;
            }
            else if (entry.State == EntityState.Added)
            {
                rows.Add(entry.Entity);
            }
        }
        return rows;
    }

    private static async Task ValidateRejectionTemplateReferencesAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken)
    {
        var templates = await LoadTemplateRowsIncludingPendingAsync(context, cancellationToken);
        var workflows = await LoadWorkflowRowsIncludingPendingAsync(context, cancellationToken);
        var selected = organizationId == 1
            ? workflows
            : workflows.Where(item => item.OrganizationId == organizationId).ToList();
        foreach (var workflow in selected)
        {
            if (!workflow.OutstandingTimeoutRejectionTemplateId.HasValue) continue;
            var template = ResolveEffectiveTemplate(
                templates,
                workflow.OutstandingTimeoutRejectionTemplateId.Value,
                workflow.OrganizationId);
            if (template is null || !IsEligibleRejectionTemplate(template))
            {
                throw new InvalidOperationException("The effective timeout rejection template is unavailable, hidden, or outside the selected configuration scope.");
            }
        }
    }
}
