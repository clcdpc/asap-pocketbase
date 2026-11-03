using System.Globalization;
using System.Text.Json;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Administration;

public sealed record AdministrationAuditQuery(
    LibraryScope Scope, string? Search = null, string? Category = null,
    DateTimeOffset? FromUtc = null, DateTimeOffset? BeforeUtc = null,
    string? ActorId = null, string Sort = "newest", int Page = 1, int PageSize = 25);

public sealed record AdministrationAuditDetail(string Label, string Value);
public sealed record AdministrationAuditEntry(
    string Id, string? ActorStaffUserId, string? ActorName, int? OrganizationId,
    string ScopeName, string Action, string Summary, string Category,
    string? TargetType, string? TargetId, string? RecordedTarget,
    IReadOnlyList<AdministrationAuditDetail> Details, DateTime CreatedUtc);

public sealed partial class AdministrationService
{
    private static readonly string[] AuditCategories = ["staff", "settings", "organizations", "workflow", "holds", "other"];

    public async Task<AdministrationResult> ListAuditAsync(
        CurrentStaff actor, AdministrationAuditQuery input, CancellationToken cancellationToken)
    {
        if (actor.Role is not (StaffRole.Admin or StaffRole.SuperAdmin) ||
            actor.Role != StaffRole.SuperAdmin &&
            (input.Scope.Kind != LibraryScopeKind.Library || input.Scope.OrganizationId != actor.OrganizationId))
        {
            return new AdministrationResult("staff_scope_forbidden");
        }
        var search = input.Search?.Trim();
        if (search?.Length > 200 || input.Category is { Length: > 0 } && !AuditCategories.Contains(input.Category) ||
            input.Sort is not ("newest" or "oldest") || input.Page < 1 || input.PageSize is < 1 or > 100 ||
            (long)(input.Page - 1) * input.PageSize > int.MaxValue ||
            input.FromUtc >= input.BeforeUtc || input.ActorId is { Length: > 0 } && input.ActorId != "system" &&
            (!long.TryParse(input.ActorId, NumberStyles.None, CultureInfo.InvariantCulture, out var actorId) || actorId <= 0))
        {
            return new AdministrationResult("audit_query_invalid", Message: "Review the audit filters and date range.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken);
        // Keep the same routing-organization then active-actor lock boundary used by
        // administration writes. URL scopes never substitute for current authority.
        var locked = await LockAndRevalidateActorAsync(context, actor,
            input.Scope.OrganizationId ?? actor.OrganizationId, [], cancellationToken);
        if (locked.Failure is not null)
        {
            return locked.Failure;
        }
        // Query shape and option scopes use the ticket's authority. A valid
        // lifecycle change can still satisfy Admin revalidation, so reject a
        // changed role/binding before projecting any history or filter choices.
        var currentActor = context.StaffUsers.Local.Single(item => item.Id == actor.Id);
        if (currentActor.Role != actor.Role || currentActor.OrganizationId != actor.OrganizationId)
        {
            return new AdministrationResult("staff_session_invalid");
        }

        // Only these recorded, action-specific fields participate in text search.
        // Never search raw DetailsJson: even a count could disclose a hidden secret.
        var authorized = context.Database.SqlQuery<AuditStoredRow>($"""
            SELECT a.[Id], a.[ActorStaffUserId], a.[ActorName], a.[OrganizationId],
                   COALESCE(o.[DisplayName], N'System') AS [ScopeName],
                   a.[Action], a.[TargetType], a.[TargetId], a.[DetailsJson], a.[CreatedUtc],
                   CASE WHEN a.[Action] = N'staff_metadata_updated'
                        THEN JSON_VALUE(a.[DetailsJson], '$.authenticationEmail')
                        WHEN a.[Action] = N'custom_format_deleted'
                        THEN JSON_VALUE(a.[DetailsJson], '$.Label') END AS [RecordedTarget],
                   CASE WHEN a.[TargetType] = N'StaffUser' THEN N'staff'
                        WHEN a.[TargetType] IN (N'Configuration', N'Branding', N'MaterialFormat') THEN N'settings'
                        WHEN a.[TargetType] = N'Organization' THEN N'organizations'
                        WHEN a.[TargetType] = N'WeeklyStaffSummary' THEN N'workflow'
                        WHEN a.[TargetType] = N'hold_placement_operation' THEN N'holds'
                        ELSE N'other' END AS [Category]
            FROM [asap].[AdministrativeAudit] a
            LEFT JOIN [asap].[Organization] o ON o.[Id] = a.[OrganizationId] AND o.[Id] <> 1
            """);
        if (input.Scope.Kind == LibraryScopeKind.System)
        {
            authorized = authorized.Where(item => item.OrganizationId == null || item.OrganizationId == 1);
        }
        else if (input.Scope.Kind == LibraryScopeKind.Library)
        {
            authorized = authorized.Where(item => item.OrganizationId == input.Scope.OrganizationId);
        }

        var scopeOptions = actor.Role == StaffRole.SuperAdmin
            ? await context.Organizations.AsNoTracking()
                .Where(item => item.Id > 1 && item.OrganizationCodeId == OrganizationAuthority.LibraryOrganizationCodeId &&
                    (string.IsNullOrEmpty(search) || item.DisplayName.Contains(search) || item.Id == input.Scope.OrganizationId))
                .OrderBy(item => item.DisplayName).ThenBy(item => item.Id)
                .Select(item => new { item.Id, Name = item.DisplayName }).Take(501).ToListAsync(cancellationToken)
            : await context.Organizations.AsNoTracking().Where(item => item.Id == actor.OrganizationId)
                .Select(item => new { item.Id, Name = item.DisplayName }).ToListAsync(cancellationToken);

        var query = authorized;
        if (!string.IsNullOrEmpty(search))
        {
            var matchingActions = new[] { "staff_created", "staff_reactivated", "staff_metadata_updated", "staff_lifecycle_updated", "staff_deactivated",
                "system_settings_updated", "library_settings_updated", "library_settings_reset", "organizations_synced", "organization_activated",
                "organization_deactivated", "branding_logo_updated", "branding_logo_removed", "custom_format_deleted", "weekly_summary_force_queued",
                "hold_operation_resolved_succeeded", "hold_operation_resolved_not_performed" }
                .Where(action => AuditSummary(action).Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
            query = query.Where(item => item.ActorName != null && item.ActorName.Contains(search) ||
                item.Action.Contains(search) || matchingActions.Contains(item.Action) || item.TargetType != null && item.TargetType.Contains(search) ||
                item.TargetId != null && item.TargetId.Contains(search) ||
                item.RecordedTarget != null && item.RecordedTarget.Contains(search) || item.ScopeName.Contains(search));
        }
        if (!string.IsNullOrEmpty(input.Category))
        {
            query = query.Where(item => item.Category == input.Category);
        }
        if (input.FromUtc is { } from)
        {
            query = query.Where(item => item.CreatedUtc >= from.UtcDateTime);
        }
        if (input.BeforeUtc is { } before)
        {
            query = query.Where(item => item.CreatedUtc < before.UtcDateTime);
        }
        // Narrow choices over the same full authorized search/category/date range,
        // before the actor predicate. Search makes later actors reachable without
        // downloading an unbounded roster. A selected authorized identity survives
        // narrowing, even when it no longer has a matching row in this date range.
        var actors = await query.GroupBy(item => item.ActorStaffUserId)
            .Select(group => new { ActorStaffUserId = group.Key, ActorName = group.Min(item => item.ActorName) })
            .OrderBy(item => item.ActorName).ThenBy(item => item.ActorStaffUserId)
            .Take(101).ToListAsync(cancellationToken);
        var actorsTruncated = actors.Count > 100;
        actors = actors.Take(100).ToList();
        if (!string.IsNullOrEmpty(input.ActorId))
        {
            long? selectedActor = input.ActorId == "system" ? null : long.Parse(input.ActorId, CultureInfo.InvariantCulture);
            if (actors.All(item => item.ActorStaffUserId != selectedActor))
            {
                var retainedActor = await authorized.Where(item => item.ActorStaffUserId == selectedActor)
                    .GroupBy(item => item.ActorStaffUserId)
                    .Select(group => new { ActorStaffUserId = group.Key, ActorName = group.Min(item => item.ActorName) })
                    .SingleOrDefaultAsync(cancellationToken);
                if (retainedActor is not null)
                {
                    actors.Add(retainedActor);
                }
            }
            query = query.Where(item => item.ActorStaffUserId == selectedActor);
        }
        var count = await query.LongCountAsync(cancellationToken);
        var page = Math.Min(input.Page, Math.Max(1, (int)Math.Min(int.MaxValue, (count + input.PageSize - 1) / input.PageSize)));
        var ordered = input.Sort == "oldest"
            ? query.OrderBy(item => item.CreatedUtc).ThenBy(item => item.Id)
            : query.OrderByDescending(item => item.CreatedUtc).ThenByDescending(item => item.Id);
        var rows = await ordered.Skip((page - 1) * input.PageSize).Take(input.PageSize).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AdministrationResult("ok", new
        {
            items = rows.Select(item => new AdministrationAuditEntry(
                item.Id.ToString(CultureInfo.InvariantCulture), item.ActorStaffUserId?.ToString(CultureInfo.InvariantCulture),
                item.ActorName, item.OrganizationId, item.ScopeName, item.Action, AuditSummary(item.Action), item.Category,
                item.TargetType, item.TargetId, item.RecordedTarget, SafeAuditDetails(item.Action, item.DetailsJson),
                DateTime.SpecifyKind(item.CreatedUtc, DateTimeKind.Utc))),
            count, page, pageSize = input.PageSize,
            actors = actors.Select(item => new { id = item.ActorStaffUserId?.ToString(CultureInfo.InvariantCulture) ?? "system",
                name = item.ActorStaffUserId.HasValue ? item.ActorName ?? $"Staff {item.ActorStaffUserId} (name not recorded)" : "Actor not recorded" }),
            actorsTruncated,
            libraries = scopeOptions.Take(500), librariesTruncated = scopeOptions.Count > 500
        });
    }

    private static string AuditSummary(string action) => action switch
    {
        "staff_created" => "Staff account added",
        "staff_reactivated" => "Staff account reactivated",
        "staff_metadata_updated" => "Staff account details updated",
        "staff_lifecycle_updated" => "Staff access updated",
        "staff_deactivated" => "Staff account deactivated",
        "system_settings_updated" => "System settings updated",
        "library_settings_updated" => "Library settings updated",
        "library_settings_reset" => "Library overrides reset",
        "organizations_synced" => "Polaris organizations synchronized",
        "organization_activated" => "Library participation enabled",
        "organization_deactivated" => "Library participation disabled",
        "branding_logo_updated" => "Library logo updated",
        "branding_logo_removed" => "Library logo removed",
        "custom_format_deleted" => "Custom format deleted",
        "weekly_summary_force_queued" => "Weekly summary run requested",
        "hold_operation_resolved_succeeded" => "Hold operation resolved as succeeded",
        "hold_operation_resolved_not_performed" => "Hold operation resolved as not performed",
        _ => action.Replace('_', ' ')
    };

    private static IReadOnlyList<AdministrationAuditDetail> SafeAuditDetails(string action, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        // Allow-list each current writer's scalar/array fields. Recovery journals
        // contain protected provider evidence and nested snapshots; those stay private.
        string[] keys = action switch
        {
            "staff_created" or "staff_reactivated" => ["role"],
            "staff_metadata_updated" => ["previousAuthenticationEmail", "authenticationEmail"],
            "staff_lifecycle_updated" or "staff_deactivated" => ["role", "rulesDeactivated", "openTitleClaimsCleared", "openAdditionalCopyClaimsCleared"],
            "system_settings_updated" or "library_settings_updated" => ["action", "scope", "sections"],
            "library_settings_reset" => ["preserved", "removed"],
            "organizations_synced" => ["received", "changed", "newLibrariesInactive"],
            "organization_activated" or "organization_deactivated" => ["active", "changed", "revokedPatronSessions"],
            "branding_logo_updated" or "branding_logo_removed" => ["clearLogo", "hasAltOverride"],
            "custom_format_deleted" => ["Code", "Label"],
            "weekly_summary_force_queued" => ["manualRunId", "scopeOrganizationId"],
            _ => []
        };
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return [];
        }
        var details = new List<AdministrationAuditDetail>();
        var source = document.RootElement;
        if (action is "hold_operation_resolved_succeeded" or "hold_operation_resolved_not_performed" &&
            source.TryGetProperty("operatorResolution", out var resolution) && resolution.ValueKind == JsonValueKind.Object)
        {
            source = resolution;
            keys = ["operationId", "titleRequestId", "attemptNumber", "phase", "verification", "outcome", "resolvedUtc"];
        }
        foreach (var key in keys)
        {
            if (!source.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }
            var formatted = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
                JsonValueKind.Array when value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String) =>
                    string.Join(", ", value.EnumerateArray().Select(item => item.GetString())),
                _ => null
            };
            if (formatted is not null)
            {
                details.Add(new AdministrationAuditDetail(key, formatted));
            }
        }
        return details;
    }

    private sealed class AuditStoredRow
    {
        public long Id { get; set; }
        public long? ActorStaffUserId { get; set; }
        public string? ActorName { get; set; }
        public int? OrganizationId { get; set; }
        public string ScopeName { get; set; } = "";
        public string Action { get; set; } = "";
        public string Category { get; set; } = "";
        public string? TargetType { get; set; }
        public string? TargetId { get; set; }
        public string? DetailsJson { get; set; }
        public string? RecordedTarget { get; set; }
        public DateTime CreatedUtc { get; set; }
    }
}
