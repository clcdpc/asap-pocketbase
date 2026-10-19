using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asap.Shared;
using Asap.Web.Features.Email;
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
    int? OrganizationCodeId,
    int? ParentOrganizationId,
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

public sealed partial class AdministrationService(
    IDbContextFactory<AsapDbContext> contextFactory,
    PatronConfigurationService patronConfiguration,
    StaffEligibilityService staffEligibility,
    IntegrationCredentialProtector credentialProtector,
    IPolarisReferenceProvider polarisProvider,
    TimeProvider timeProvider)
{
    private static readonly IReadOnlyDictionary<string, string> DuplicateLabelFields =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RequestStatus.Suggestion] = nameof(PatronSettings.SuggestionStatusLabel),
            [RequestStatus.OutstandingPurchase] = nameof(PatronSettings.OutstandingPurchaseStatusLabel),
            [RequestStatus.PendingHold] = nameof(PatronSettings.PendingHoldStatusLabel),
            [RequestStatus.HoldPlaced] = nameof(PatronSettings.HoldPlacedStatusLabel),
            [RequestStatus.Closed] = nameof(PatronSettings.ClosedStatusLabel),
            ["rejected"] = nameof(PatronSettings.RejectedStatusLabel),
            ["hold_completed"] = nameof(PatronSettings.HoldCompletedStatusLabel),
            ["hold_not_picked_up"] = nameof(PatronSettings.HoldNotPickedUpStatusLabel),
            ["manual"] = nameof(PatronSettings.ManualStatusLabel),
            ["silent"] = nameof(PatronSettings.SilentStatusLabel)
        };

    public async Task<AdministrationResult> GetSettingsAsync(
        CurrentStaff actor,
        LibraryScope requestedScope,
        CancellationToken cancellationToken)
    {
        if (!TryResolveScope(actor, requestedScope, out var organizationId, out var failure))
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
        if (organizationId != LibraryScope.SystemOrganizationId && !OrganizationAuthority.IsLibrary(organization))
        {
            return new AdministrationResult("organization_not_found");
        }

        var systemSettings = await context.SystemSettings.AsNoTracking()
            .SingleAsync(item => item.OrganizationId == LibraryScope.SystemOrganizationId, cancellationToken);
        var systemPolaris = await context.PolarisSettings.AsNoTracking()
            .SingleAsync(item => item.OrganizationId == LibraryScope.SystemOrganizationId, cancellationToken);
        var systemWorkflow = await context.WorkflowSettings.AsNoTracking()
            .SingleAsync(item => item.OrganizationId == LibraryScope.SystemOrganizationId, cancellationToken);
        var systemPatron = await context.PatronSettings.AsNoTracking()
            .SingleAsync(item => item.OrganizationId == LibraryScope.SystemOrganizationId, cancellationToken);
        var systemEmail = await context.EmailSettings.AsNoTracking()
            .SingleAsync(item => item.OrganizationId == LibraryScope.SystemOrganizationId, cancellationToken);
        var libraryWorkflow = organizationId == LibraryScope.SystemOrganizationId
            ? null
            : await context.WorkflowSettings.AsNoTracking().SingleOrDefaultAsync(
                item => item.OrganizationId == organizationId, cancellationToken);
        var libraryPatron = organizationId == LibraryScope.SystemOrganizationId
            ? null
            : await context.PatronSettings.AsNoTracking().SingleOrDefaultAsync(
                item => item.OrganizationId == organizationId, cancellationToken);
        var libraryEmail = organizationId == LibraryScope.SystemOrganizationId
            ? null
            : await context.EmailSettings.AsNoTracking().SingleOrDefaultAsync(
                item => item.OrganizationId == organizationId, cancellationToken);
        var effective = await patronConfiguration.GetAsync(context, organizationId, cancellationToken);
        if (effective is null)
        {
            return new AdministrationResult("organization_not_found");
        }

        var origins = await context.PatronEmbedAllowedOrigins.AsNoTracking()
            .Where(item => item.OrganizationId == LibraryScope.SystemOrganizationId)
            .OrderBy(item => item.NormalizedOrigin)
            .Select(item => item.Origin)
            .ToListAsync(cancellationToken);
        var libraryOrganizations = await context.Organizations.AsNoTracking()
            .Where(item => item.Id > LibraryScope.SystemOrganizationId &&
                           item.OrganizationCodeId == OrganizationAuthority.LibraryOrganizationCodeId)
            .OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.IsActive })
            .ToArrayAsync(cancellationToken);
        var enabledLibraryOrgIds = libraryOrganizations.Where(item => item.IsActive).Select(item => item.Id).ToArray();
        var libraryOrgIds = libraryOrganizations.Select(item => item.Id).ToArray();
        var (providers, overriddenProviderIds) = await LoadProvidersAsync(context, organizationId, cancellationToken);
        var (formats, formatMetadata) = await LoadFormatsAsync(context, organizationId, cancellationToken);
        var customFields = organizationId == LibraryScope.SystemOrganizationId
            ? []
            : await LoadCustomFieldsAsync(context, organizationId, cancellationToken);
        var formatRules = organizationId == LibraryScope.SystemOrganizationId
            ? Array.Empty<object>()
            : await LoadRawCustomFieldRulesAsync(context, organizationId, cancellationToken);
        var templates = await LoadTemplatesAsync(context, organizationId, cancellationToken);
        var publicationOptions = await LoadPublicationOptionsAsync(context, organizationId, cancellationToken);
        var commonCreators = await LoadCommonCreatorsAsync(context, organizationId, cancellationToken);
        var patronCodes = await LoadPatronCodesAsync(context, organizationId, cancellationToken);
        var autoClaimRules = organizationId == LibraryScope.SystemOrganizationId
            ? []
            : await LoadAutoClaimRulesAsync(context, organizationId, cancellationToken);
        var autoClaimStaff = organizationId == LibraryScope.SystemOrganizationId
            ? Array.Empty<object>()
            : (await context.StaffUsers.AsNoTracking()
                .Where(item => item.IsActive &&
                    (((item.Role == StaffRole.Staff || item.Role == StaffRole.Admin) && item.OrganizationId == organizationId) ||
                     (item.Role == StaffRole.SuperAdmin && item.OrganizationId == LibraryScope.SystemOrganizationId)))
                .OrderBy(item => item.DisplayName)
                .ThenBy(item => item.UserPrincipalName)
                .ToListAsync(cancellationToken))
                .Where(item => StaffEligibilityService.IsAssignmentEligible(item, organizationId) &&
                    StaffEligibilityService.HasLockedActiveOrganization(context, item))
                .Select(item => (object)new
                {
                    id = item.Id.ToString(),
                    label = item.DisplayName ?? item.UserPrincipalName ?? $"Staff {item.Id}"
                })
                .ToArray();
        var branding = await context.Branding.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        var systemBranding = organizationId == LibraryScope.SystemOrganizationId
            ? branding
            : await context.Branding.AsNoTracking()
                .SingleOrDefaultAsync(item => item.OrganizationId == LibraryScope.SystemOrganizationId, cancellationToken);
        var hasOverrides = organizationId != LibraryScope.SystemOrganizationId && await HasLibraryOverridesAsync(context, organizationId, cancellationToken);
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
        var libraryOverride = organizationId == LibraryScope.SystemOrganizationId
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
            systemSettings = ToSystemSettings(systemSettings, origins, enabledLibraryOrgIds, libraryOrgIds),
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
            formatRules,
            templates,
            autoClaimRules,
            branding = ToBranding(branding)
        };

        var effectiveDto = ToEffectiveConfiguration(effective, systemWorkflow, libraryWorkflow,
            overriddenProviderIds, formatMetadata);
        var result = new AdministrationResult(
            "ok",
            new
            {
                orgId = organizationId == LibraryScope.SystemOrganizationId ? "system" : organizationId.ToString(),
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
                workflow = ToWorkflow(effective, system: libraryWorkflow is null || organizationId == LibraryScope.SystemOrganizationId),
                ui_text = ToEffectivePatronText(effective),
                emails = await ToEffectiveEmailAsync(context, organizationId, effective, cancellationToken),
                templatePlaceholders = PatronEmailTemplateRenderer.SupportedPlaceholders,
                publicPatron = PublicPatronLinkBuilder.Build(systemSettings.StaffApplicationUrl, organizationId, organization.IsActive),
                publicPatronUnavailableReason = organizationId == LibraryScope.SystemOrganizationId
                    ? "Select an active library to get its public patron URL."
                    : !organization.IsActive
                        ? "This library is inactive; activate it before sharing its patron URL."
                        : PublicPatronLinkBuilder.Build(systemSettings.StaffApplicationUrl, organizationId, true) is null
                            ? "Set a valid system Staff URL ending in /staff/ to generate public patron links."
                            : null,
                formatClaimRules = autoClaimRules,
                autoClaimStaff,
                originsForEditor = origins
            });
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<AdministrationResult> SaveSettingsAsync(
        CurrentStaff actor,
        AdministrationSettingsCommand command,
        CancellationToken cancellationToken)
    {
        if (command.BindingError is not null)
        {
            return new AdministrationResult(command.BindingError);
        }
        var payload = command.CollectionEdits;
        if (!TryResolveScope(actor, command.Scope, out var organizationId, out var failure))
        {
            return failure;
        }

        var isReset = organizationId != LibraryScope.SystemOrganizationId && command.Reset;
        var patronCodeValidation = isReset
            ? (Snapshot: (PatronCodeValidation?)null, Failure: (AdministrationResult?)null)
            : await PreparePatronCodeValidationAsync(payload, organizationId, cancellationToken);
        if (patronCodeValidation.Failure is not null)
        {
            return patronCodeValidation.Failure;
        }
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            cancellationToken);
        var autoClaimStaffIds = organizationId != LibraryScope.SystemOrganizationId &&
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
            includeAllOrganizations: organizationId == LibraryScope.SystemOrganizationId,
            additionalStaffIds: autoClaimStaffIds);
        if (locked.Failure is not null)
        {
            return locked.Failure;
        }
        var organization = locked.Organizations[organizationId];

        var expectedVersion = command.Version;
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

        if (patronCodeValidation.Snapshot is not null)
        {
            var patronCodeFailure = await ValidatePatronCodePayloadAsync(
                context, organizationId, patronCodeValidation.Snapshot, cancellationToken);
            if (patronCodeFailure is not null)
            {
                return patronCodeFailure;
            }
        }

        if (!isReset && organizationId != LibraryScope.SystemOrganizationId &&
            await ReferencesDeletedCustomTemplateAsync(context, organizationId, payload, cancellationToken))
        {
            return new AdministrationResult("template_referenced",
                Message: "This rejection template is used by auto-rejection. Change and save that workflow setting before deleting the template.");
        }

        if (isReset)
        {
            await ResetLibrarySettingsInTransactionAsync(context, organizationId, actor, cancellationToken);
        }
        else
        {
            try
            {
                if (organizationId == LibraryScope.SystemOrganizationId)
                {
                    await ApplySystemSettingsAsync(context, command, cancellationToken);
                }
                await ApplyScopedSettingsAsync(context, organizationId, command, cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return new AdministrationResult("stale_version",
                    Message: "A submitted settings row changed. Reload before saving.");
            }
        }

        if (!isReset)
        {
            await AddAuditAsync(
                context,
                actor,
                organizationId,
                organizationId == LibraryScope.SystemOrganizationId ? "system_settings_updated" : "library_settings_updated",
                "Configuration",
                organizationId.ToString(),
                new
                {
                    action = "save",
                    scope = organizationId == LibraryScope.SystemOrganizationId ? "system" : "library",
                    sections = payload.ValueKind == JsonValueKind.Object
                        ? payload.EnumerateObject().Select(item => item.Name).Where(item => item is not "version" and not "orgId").Order().ToArray()
                        : []
                });
        }
        await context.SaveChangesAsync(cancellationToken);
        if (isReset && organizationId != LibraryScope.SystemOrganizationId)
        {
            await ValidateEffectiveRequiredSelectRulesAsync(context, organizationId, cancellationToken);
        }
        var savedVersion = await ComputeSettingsVersionAsync(context, organizationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AdministrationResult(
            "saved",
            new
            {
                orgId = organizationId == LibraryScope.SystemOrganizationId ? "system" : organizationId.ToString(),
                version = savedVersion
            });
    }

    public async Task<AdministrationResult> ResetLibrarySettingsAsync(
        CurrentStaff actor,
        int organizationId,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        if (!TryResolveScope(actor, organizationId > LibraryScope.SystemOrganizationId
                ? LibraryScope.ForLibrary(organizationId) : LibraryScope.System, out var resolved, out var failure) || resolved == LibraryScope.SystemOrganizationId)
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
        await ValidateEffectiveRequiredSelectRulesAsync(context, organizationId, cancellationToken);
        var resetVersion = await ComputeSettingsVersionAsync(context, organizationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AdministrationResult("reset", new { orgId = organizationId.ToString(), version = resetVersion });
    }

    public async Task<AdministrationResult> ListOrganizationsAsync(
        CurrentStaff actor,
        CancellationToken cancellationToken)
    {
        if (actor.Role is not (StaffRole.Admin or StaffRole.SuperAdmin))
        {
            return new AdministrationResult("staff_scope_forbidden");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.Organizations.AsNoTracking();
        if (actor.Role != StaffRole.SuperAdmin)
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
                item.OrganizationCodeId,
                item.ParentOrganizationId,
                item.IsActive,
                item.LastSyncedUtc,
                StaffVersion.Encode(item.RowVersion))).ToArray());
    }

    public async Task<AdministrationResult> ListPatronCodesAsync(
        CurrentStaff actor,
        LibraryScope requestedScope,
        CancellationToken cancellationToken)
    {
        if (!TryResolveScope(actor, requestedScope, out _, out var failure))
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
    }

    public async Task<AdministrationResult> SyncOrganizationsAsync(
        CurrentStaff actor,
        CancellationToken cancellationToken)
    {
        if (actor.Role != StaffRole.SuperAdmin)
        {
            return new AdministrationResult("staff_scope_forbidden");
        }

        IReadOnlyList<PolarisOrganizationSnapshot> snapshots;
        try
        {
            snapshots = await polarisProvider.GetOrganizationsAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (PolarisOperationalException)
        {
            return new AdministrationResult("polaris_unavailable", Message: "Polaris organizations could not be loaded.");
        }
        if (snapshots.Count == 0)
        {
            return new AdministrationResult("polaris_organizations_empty");
        }
        if (snapshots.Any(item => item.Id <= 0) || snapshots.Select(item => item.Id).Distinct().Count() != snapshots.Count)
        {
            return new AdministrationResult("polaris_organizations_invalid",
                Message: "Polaris organizations contain an invalid or repeated organization ID.");
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
        foreach (var snapshot in snapshots)
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
                organization.OrganizationCodeId = snapshot.OrganizationCodeId;
                organization.ParentOrganizationId = snapshot.ParentOrganizationId;
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
                    OrganizationCodeId = snapshot.OrganizationCodeId,
                    ParentOrganizationId = snapshot.ParentOrganizationId,
                    IsActive = false,
                    LastSyncedUtc = now
                };
                context.Organizations.Add(organization);
            }
            else
            {
                organization.DisplayName = snapshot.DisplayName;
                organization.Abbreviation = Clean(snapshot.Abbreviation);
                organization.OrganizationCodeId = snapshot.OrganizationCodeId;
                organization.ParentOrganizationId = snapshot.ParentOrganizationId;
                organization.LastSyncedUtc = now;
            }
            if (!OrganizationAuthority.IsLibrary(snapshot.Id, snapshot.OrganizationCodeId))
            {
                organization.IsActive = false;
                var sessions = await context.PatronSessions
                    .Where(item => item.EffectiveOrganizationId == snapshot.Id && item.RevokedUtc == null)
                    .ToListAsync(cancellationToken);
                foreach (var session in sessions)
                {
                    session.RevokedUtc = now;
                }
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
        if (actor.Role != StaffRole.SuperAdmin || organizationId <= LibraryScope.SystemOrganizationId)
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
            StaffRoleRequirement.SuperAdmin,
            allowNonLibraryTarget: true);
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

        if (active && !OrganizationAuthority.IsLibrary(organization))
        {
            return new AdministrationResult("organization_not_library");
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
        if (actor.Role != StaffRole.SuperAdmin)
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
        if (actor.Role is not (StaffRole.Admin or StaffRole.SuperAdmin))
        {
            return new AdministrationResult("staff_scope_forbidden");
        }
        if (actor.Role != StaffRole.SuperAdmin && organizationId.HasValue && organizationId != actor.OrganizationId)
        {
            return new AdministrationResult("staff_scope_forbidden");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var scope = actor.Role == StaffRole.SuperAdmin ? organizationId : actor.OrganizationId;
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
        LibraryScope requestedScope,
        byte[] data,
        string contentType,
        string fileName,
        string? altText,
        bool clearLogo,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        if (!TryResolveScope(actor, requestedScope, out var organizationId, out var failure))
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
            includeAllOrganizations: organizationId == LibraryScope.SystemOrganizationId);
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
        var isNew = branding is null;
        branding ??= new Branding { OrganizationId = organizationId };
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
        if (altText is not null || (clearLogo && organizationId != LibraryScope.SystemOrganizationId))
        {
            branding.LogoAltText = Clean(altText);
        }
        branding.UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime;
        if (organizationId != LibraryScope.SystemOrganizationId && IsEmpty(branding))
        {
            if (!isNew)
            {
                context.Branding.Remove(branding);
            }
        }
        else if (isNew)
        {
            context.Branding.Add(branding);
        }
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
        if (actor.Role is not (StaffRole.Admin or StaffRole.SuperAdmin))
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
        if (format.OwnerOrganizationId == LibraryScope.SystemOrganizationId)
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
        return new AdministrationResult(
            "format_deleted",
            new { formatId = formatId.ToString(System.Globalization.CultureInfo.InvariantCulture) });
    }

    private static bool TryResolveScope(
        CurrentStaff actor, LibraryScope requested, out int organizationId, out AdministrationResult failure)
    {
        organizationId = requested.OrganizationId ?? 0;
        failure = new AdministrationResult("ok");
        if (requested.Kind == LibraryScopeKind.All)
        {
            failure = new AdministrationResult("organization_invalid");
            return false;
        }
        if (!StaffEligibilityService.RoleMeets(actor.Role, StaffRoleRequirement.Admin) ||
            !StaffEligibilityService.CanAccess(actor, organizationId))
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
        IEnumerable<long>? additionalStaffIds = null,
        bool allowNonLibraryTarget = false)
    {
        if (includeAllOrganizations)
        {
            // System versions include every participation row. Org1 also serializes
            // reference additions, so enumerate and lock that set before any Staff row.
            await LockOrganizationAsync(context, 1, cancellationToken);
            additionalOrganizationIds = additionalOrganizationIds.Concat(
                await context.Organizations.AsNoTracking()
                    .Where(item => item.Id > 1 && item.OrganizationCodeId == OrganizationAuthority.LibraryOrganizationCodeId)
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
        if (!allowNonLibraryTarget && targetOrganizationId != LibraryScope.SystemOrganizationId &&
            !OrganizationAuthority.IsLibrary(organizations[targetOrganizationId]))
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

        int? eligibilityScope = allowNonLibraryTarget && targetOrganizationId != LibraryScope.SystemOrganizationId &&
            !OrganizationAuthority.IsLibrary(organizations[targetOrganizationId])
                ? null
                : targetOrganizationId;
        var eligibility = await staffEligibility.RevalidateLockedAsync(
            context,
            actor,
            eligibilityScope,
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

    private sealed record PatronCodeValidation(
        IReadOnlyList<int> Requested,
        IReadOnlySet<int> Known);

    private async Task<(PatronCodeValidation? Snapshot, AdministrationResult? Failure)> PreparePatronCodeValidationAsync(
        JsonElement payload,
        int organizationId,
        CancellationToken cancellationToken)
    {
        var workflow = GetObject(payload, "workflow");
        if (!TryGetAtRootOrSection(payload, workflow, out var value, "allowedPatronCodeIds", "patronCodeIds"))
        {
            return (null, null);
        }
        if (value.ValueKind == JsonValueKind.Null)
        {
            return (null, organizationId == LibraryScope.SystemOrganizationId
                ? new AdministrationResult("patron_codes_invalid",
                    Message: "System patron-code IDs cannot inherit another scope. Use an empty array to clear them.")
                : null);
        }
        if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Array))
        {
            return (null, new AdministrationResult(
                "patron_codes_invalid",
                Message: "allowedPatronCodeIds must be an array or newline-delimited string."));
        }

        if (!TryParsePatronCodeIds(value, out var requested))
        {
            return (null, new AdministrationResult("patron_codes_invalid",
                Message: "Patron-code IDs must be positive Int32 values."));
        }
        if (requested.Count == 0)
        {
            return (null, null);
        }

        IReadOnlyList<PolarisPatronCodeSnapshot> choices;
        try
        {
            choices = await polarisProvider.GetPatronCodesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PolarisOperationalException exception)
        {
            return (null, new AdministrationResult(
                "patron_codes_unavailable",
                Message: exception.Message));
        }

        var known = choices.Where(item => item.Id > 0).Select(item => item.Id).ToHashSet();
        return (new PatronCodeValidation(requested, known), null);
    }

    private static async Task<AdministrationResult?> ValidatePatronCodePayloadAsync(
        AsapDbContext context,
        int organizationId,
        PatronCodeValidation snapshot,
        CancellationToken cancellationToken)
    {
        var sourceOrganizationId = organizationId;
        if (organizationId != LibraryScope.SystemOrganizationId && !await context.PatronCodeEligibilitySets.AsNoTracking()
                .AnyAsync(item => item.OrganizationId == organizationId, cancellationToken))
        {
            sourceOrganizationId = 1;
        }
        var previouslySelected = await context.PatronCodeEligibilityMembers.AsNoTracking()
            .Where(item => item.OrganizationId == sourceOrganizationId)
            .Select(item => item.PatronCodeId)
            .ToListAsync(cancellationToken);
        var preserved = previouslySelected.ToHashSet();
        var unknown = snapshot.Requested
            .Where(item => !snapshot.Known.Contains(item) && !preserved.Contains(item))
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

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static JsonElement GetObject(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

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

    private static async Task<bool> ReferencesDeletedCustomTemplateAsync(
        AsapDbContext context,
        int organizationId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var deletedKeys = new List<string>();
        void AddDeletedKey(JsonElement item, string? legacyKey = null)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return;
            }
            var key = Clean(GetString(item, "templateKey") ?? GetString(item, "key") ?? legacyKey);
            var sourceId = GetLong(item, "sourceTemplateId") ?? GetLong(item, "sourceId");
            var isCustom = GetBool(item, "isCustom") == true || GetBool(item, "custom") == true ||
                (GetBool(item, "libraryCustom") == true && !sourceId.HasValue && key is not null);
            var reset = GetBool(item, "reset") == true || GetBool(item, "useSystemDefault") == true ||
                GetBool(item, "overridden") == false;
            if (key is not null && isCustom && reset &&
                (GetInt(item, "organizationId") is not int itemOrganization || itemOrganization == organizationId))
            {
                deletedKeys.Add(key);
            }
        }

        if (TryGetAny(payload, out var templates, "templates", "emailTemplates"))
        {
            if (templates.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in templates.EnumerateArray())
                {
                    AddDeletedKey(item);
                }
            }
        }
        else
        {
            var emails = GetObject(payload, "emails", "email");
            if (emails.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in emails.EnumerateObject())
                {
                    if (property.Name == "rejection_templates")
                    {
                        if (property.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in property.Value.EnumerateArray())
                            {
                                AddDeletedKey(item);
                            }
                        }
                        continue;
                    }
                    if (property.Name is not ("fromAddress" or "fromName" or "postmarkToken" or "serverToken"))
                    {
                        AddDeletedKey(property.Value, property.Name);
                    }
                }
            }
        }
        if (deletedKeys.Count == 0)
        {
            return false;
        }

        var templateIds = await context.EmailTemplates.AsNoTracking()
            .Where(item => item.OrganizationId == organizationId && item.IsCustom &&
                deletedKeys.Contains(item.TemplateKey))
            .Select(item => item.Id)
            .ToArrayAsync(cancellationToken);
        return templateIds.Length > 0 && await context.WorkflowSettings.AsNoTracking()
            .AnyAsync(item => item.OrganizationId == organizationId &&
                item.OutstandingTimeoutRejectionTemplateId.HasValue &&
                templateIds.Contains(item.OutstandingTimeoutRejectionTemplateId.Value), cancellationToken);
    }

    private static bool TryGetAny(JsonElement root, out JsonElement value, params string[] names)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            JsonElement? selected = null;
            foreach (var name in names)
            {
                if (root.TryGetProperty(name, out value))
                {
                    if (selected.HasValue)
                    {
                        throw new AdministrationInputException("A settings field was supplied more than once.");
                    }
                    selected = value;
                }
            }
            if (selected.HasValue)
            {
                value = selected.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetAtRootOrSection(
        JsonElement root,
        JsonElement section,
        out JsonElement value,
        params string[] names)
    {
        if (TryGetAny(root, out value, names))
        {
            return true;
        }

        return TryGetAny(section, out value, names);
    }

    private static string? GetString(JsonElement root, string name)
    {
        if (!HasProperty(root, name))
        {
            return null;
        }

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
        if (!HasProperty(root, name))
        {
            return null;
        }

        var value = root.GetProperty(name);
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        return bool.TryParse(GetString(root, name), out var parsed) ? parsed : null;
    }

    private static int? GetInt(JsonElement root, string name)
    {
        if (!HasProperty(root, name))
        {
            return null;
        }

        var value = root.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return int.TryParse(GetString(root, name), out number) ? number : null;
    }

    private static long? GetLong(JsonElement root, string name)
    {
        if (!HasProperty(root, name))
        {
            return null;
        }

        var value = root.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        return long.TryParse(GetString(root, name), out number) ? number : null;
    }

    private static void ApplyText(JsonElement root, string name, Action<string?> setter)
    {
        if (HasProperty(root, name))
        {
            setter(GetString(root, name));
        }
    }


}
