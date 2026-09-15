using Asap.Shared;
using Asap.Web.Features.Staff;
using Asap.Web.Features.Email;
using Asap.Web.Infrastructure.Jobs;
using Hangfire;

namespace Asap.Web.Features.Administration;

public sealed record AdministrationVersionInput(string? Version);

public static class AdministrationEndpoints
{
    public static IEndpointRouteBuilder MapAdministrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/asap/staff/settings/library", GetSettingsAsync)
            .RequireAuthorization();
        endpoints.MapPost("/api/asap/staff/settings/library", SaveSettingsAsync)
            .RequireAuthorization()
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        endpoints.MapGet("/api/asap/staff/settings", GetSettingsAsync)
            .RequireAuthorization();
        endpoints.MapPost("/api/asap/staff/settings", SaveSettingsAsync)
            .RequireAuthorization()
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        endpoints.MapPost("/api/asap/staff/settings/reset", ResetSettingsAsync)
            .RequireAuthorization()
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        endpoints.MapPost("/api/asap/staff/settings/logo", SaveLogoAsync)
            .RequireAuthorization()
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        endpoints.MapDelete("/api/asap/staff/settings/logo", DeleteLogoAsync)
            .RequireAuthorization()
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        endpoints.MapDelete("/api/asap/staff/settings/formats/{formatId:long}", DeleteFormatAsync)
            .RequireAuthorization()
            .AddEndpointFilter<StaffAntiforgeryFilter>();

        endpoints.MapGet("/api/asap/staff/organizations", ListOrganizationsAsync)
            .RequireAuthorization();
        endpoints.MapPost("/api/asap/staff/organizations/sync", SyncOrganizationsAsync)
            .RequireAuthorization()
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        endpoints.MapPost("/api/asap/staff/organizations/{organizationId:int}/activate", ActivateAsync)
            .RequireAuthorization()
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        endpoints.MapPost("/api/asap/staff/organizations/{organizationId:int}/deactivate", DeactivateAsync)
            .RequireAuthorization()
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        endpoints.MapPost("/api/asap/staff/polaris/test", TestPolarisAsync)
            .RequireAuthorization()
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        endpoints.MapGet("/api/asap/staff/polaris/patron-codes", ListPatronCodesAsync)
            .RequireAuthorization();
        endpoints.MapGet("/api/asap/staff/audit", ListAuditAsync)
            .RequireAuthorization();
        endpoints.MapPost("/api/asap/staff/workflow/run-now", RunWorkflowNowAsync)
            .RequireAuthorization()
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        endpoints.MapPost("/api/asap/staff/workflow/weekly-summary/run-now", RunWeeklySummaryNowAsync)
            .RequireAuthorization()
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        endpoints.MapPost("/api/asap/staff/email-operations/test", QueueTestEmailAsync)
            .RequireAuthorization()
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        endpoints.MapGet("/api/asap/staff/workflow/queues", ListQueueProgressAsync)
            .RequireAuthorization();
        endpoints.MapGet("/api/asap/staff/email-operations", ListEmailOperationsAsync)
            .RequireAuthorization();
        endpoints.MapPost("/api/asap/staff/email-operations/{id:long}/retry", RetryEmailAsync)
            .RequireAuthorization()
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        return endpoints;
    }

    private static async Task<IResult> GetSettingsAsync(
        HttpContext context,
        string? orgId,
        AdministrationService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.GetSettingsAsync(
                StaffAuthenticationEndpoints.RequireCurrentStaff(context),
                orgId,
                cancellationToken);
            return result.Code == "ok" ? Results.Json(result.Data) : ToResult(result);
        }
        catch (InvalidOperationException exception)
        {
            return Invalid(exception);
        }
    }

    private static async Task<IResult> SaveSettingsAsync(
        HttpContext context,
        System.Text.Json.JsonElement payload,
        AdministrationService service,
        CancellationToken cancellationToken)
    {
        try
        {
            return ToResult(await service.SaveSettingsAsync(
                StaffAuthenticationEndpoints.RequireCurrentStaff(context),
                payload,
                cancellationToken));
        }
        catch (InvalidOperationException exception)
        {
            return Invalid(exception);
        }
    }

    private static async Task<IResult> ResetSettingsAsync(
        HttpContext context,
        int organizationId,
        AdministrationVersionInput input,
        AdministrationService service,
        CancellationToken cancellationToken)
    {
        try
        {
            return ToResult(await service.ResetLibrarySettingsAsync(
                StaffAuthenticationEndpoints.RequireCurrentStaff(context),
                organizationId,
                input.Version,
                cancellationToken));
        }
        catch (InvalidOperationException exception)
        {
            return Invalid(exception);
        }
    }

    private static async Task<IResult> SaveLogoAsync(
        HttpContext context,
        string? orgId,
        AdministrationService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var file = form.Files.GetFile("logo");
            var data = file is null ? [] : await ReadFileAsync(file, cancellationToken);
            var clear = string.Equals(form["clearLogo"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);
            var result = await service.SaveLogoAsync(
                StaffAuthenticationEndpoints.RequireCurrentStaff(context),
                orgId,
                data,
                file?.ContentType ?? "image/*",
                file?.FileName ?? "logo",
                form.ContainsKey("logoAlt") ? form["logoAlt"].FirstOrDefault() : null,
                clear,
                form["version"].FirstOrDefault(),
                cancellationToken);
            return ToResult(result);
        }
        catch (InvalidOperationException exception)
        {
            return Invalid(exception);
        }
    }

    private static async Task<IResult> DeleteLogoAsync(
        HttpContext context,
        string? orgId,
        string? version,
        AdministrationService service,
        CancellationToken cancellationToken)
    {
        try
        {
            return ToResult(await service.SaveLogoAsync(
                StaffAuthenticationEndpoints.RequireCurrentStaff(context),
                orgId,
                [],
                "image/*",
                "logo",
                null,
                true,
                version,
                cancellationToken));
        }
        catch (InvalidOperationException exception)
        {
            return Invalid(exception);
        }
    }

    private static async Task<IResult> DeleteFormatAsync(
        HttpContext context,
        long formatId,
        string? version,
        AdministrationService service,
        CancellationToken cancellationToken)
    {
        try
        {
            return ToResult(await service.DeleteCustomFormatAsync(
                StaffAuthenticationEndpoints.RequireCurrentStaff(context),
                formatId,
                version,
                cancellationToken));
        }
        catch (InvalidOperationException exception)
        {
            return Invalid(exception);
        }
    }

    private static async Task<IResult> ListOrganizationsAsync(
        HttpContext context,
        AdministrationService service,
        CancellationToken cancellationToken) =>
        ToResult(await service.ListOrganizationsAsync(
            StaffAuthenticationEndpoints.RequireCurrentStaff(context),
            cancellationToken));

    private static async Task<IResult> SyncOrganizationsAsync(
        HttpContext context,
        AdministrationService service,
        CancellationToken cancellationToken)
    {
        try
        {
            return ToResult(await service.SyncOrganizationsAsync(
                StaffAuthenticationEndpoints.RequireCurrentStaff(context),
                cancellationToken));
        }
        catch (InvalidOperationException exception)
        {
            return Invalid(exception);
        }
    }

    private static async Task<IResult> ActivateAsync(
        HttpContext context,
        int organizationId,
        AdministrationVersionInput input,
        string? reason,
        AdministrationService service,
        CancellationToken cancellationToken) =>
        ToResult(await service.SetOrganizationActiveAsync(
            StaffAuthenticationEndpoints.RequireCurrentStaff(context),
            organizationId,
            true,
            reason,
            input.Version,
            cancellationToken));

    private static async Task<IResult> DeactivateAsync(
        HttpContext context,
        int organizationId,
        AdministrationVersionInput input,
        string? reason,
        AdministrationService service,
        CancellationToken cancellationToken) =>
        ToResult(await service.SetOrganizationActiveAsync(
            StaffAuthenticationEndpoints.RequireCurrentStaff(context),
            organizationId,
            false,
            reason,
            input.Version,
            cancellationToken));

    private static async Task<IResult> TestPolarisAsync(
        HttpContext context,
        AdministrationService service,
        CancellationToken cancellationToken) =>
        ToResult(await service.TestPolarisAsync(
            StaffAuthenticationEndpoints.RequireCurrentStaff(context),
            cancellationToken));

    private static async Task<IResult> ListPatronCodesAsync(
        HttpContext context,
        string? orgId,
        AdministrationService service,
        CancellationToken cancellationToken) =>
        ToResult(await service.ListPatronCodesAsync(
            StaffAuthenticationEndpoints.RequireCurrentStaff(context),
            orgId,
            cancellationToken));

    private static async Task<IResult> ListAuditAsync(
        HttpContext context,
        int? organizationId,
        int? limit,
        AdministrationService service,
        CancellationToken cancellationToken) =>
        ToResult(await service.ListAuditAsync(
            StaffAuthenticationEndpoints.RequireCurrentStaff(context),
            organizationId,
            limit ?? 100,
            cancellationToken));

    private static IResult RunWorkflowNowAsync(
        HttpContext context,
        int? organizationId,
        IBackgroundJobClient jobs)
    {
        var actor = StaffAuthenticationEndpoints.RequireCurrentStaff(context);
        if (actor.Role is not ("admin" or "super_admin") ||
            actor.Role != "super_admin" && organizationId.HasValue && organizationId != actor.OrganizationId ||
            actor.Role != "super_admin" && actor.OrganizationId <= 1)
        {
            return Results.Json(new { code = "staff_scope_forbidden" }, statusCode: StatusCodes.Status403Forbidden);
        }
        var effectiveScope = actor.Role == "super_admin" ? organizationId : actor.OrganizationId;
        var evidence = new StaffJobEvidence(actor.Id, actor.EntraTenantId, actor.EntraObjectId);
        var jobId = jobs.Enqueue<BackgroundWorkflowJobs>(job =>
            job.ProcessManualWorkflowAsync(evidence, effectiveScope ?? 1, CancellationToken.None));
        return Results.Accepted(value: new { code = "queued", jobId, organizationId = effectiveScope ?? 1 });
    }

    private static IResult RunWeeklySummaryNowAsync(
        HttpContext context,
        int? organizationId,
        IBackgroundJobClient jobs,
        bool force = false)
    {
        var actor = StaffAuthenticationEndpoints.RequireCurrentStaff(context);
        if (actor.Role is not ("admin" or "super_admin") ||
            actor.Role != "super_admin" && organizationId.HasValue && organizationId != actor.OrganizationId ||
            actor.Role != "super_admin" && actor.OrganizationId <= 1)
        {
            return Results.Json(new { code = "staff_scope_forbidden" }, statusCode: StatusCodes.Status403Forbidden);
        }

        var effectiveScope = actor.Role == "super_admin" ? organizationId : actor.OrganizationId;
        var evidence = new StaffJobEvidence(actor.Id, actor.EntraTenantId, actor.EntraObjectId);
        if (!force)
        {
            var ordinaryJobId = jobs.Enqueue<BackgroundWorkflowJobs>(job =>
                job.SendWeeklyStaffSummaryAsync(null, effectiveScope, CancellationToken.None));
            return Results.Accepted(value: new
            {
                code = "queued",
                jobId = ordinaryJobId,
                organizationId = effectiveScope ?? 1
            });
        }

        var manualRunId = Guid.NewGuid().ToString("N");
        var jobId = jobs.Enqueue<BackgroundWorkflowJobs>(job =>
            job.SendForcedWeeklyStaffSummaryAsync(evidence, effectiveScope, manualRunId, CancellationToken.None));
        return Results.Accepted(value: new
        {
            code = "queued",
            jobId,
            manualRunId,
            organizationId = effectiveScope ?? 1
        });
    }

    private static async Task<IResult> ListQueueProgressAsync(
        HttpContext context,
        int? organizationId,
        QueueProgressService progress,
        CancellationToken cancellationToken)
    {
        var actor = StaffAuthenticationEndpoints.RequireCurrentStaff(context);
        if (actor.Role != "super_admin" && organizationId.HasValue && organizationId != actor.OrganizationId)
        {
            return Results.Json(new { code = "staff_scope_forbidden" }, statusCode: StatusCodes.Status403Forbidden);
        }
        var scope = organizationId ?? (actor.Role == "super_admin" ? 1 : actor.OrganizationId);
        var items = new List<object>();
        foreach (var queue in QueueNames.Configured.Append(QueueNames.HoldRecovery))
        {
            var snapshot = await progress.GetSnapshotAsync(queue, scope, cancellationToken);
            if (snapshot is not null) items.Add(snapshot);
        }
        return Results.Json(new { scopeOrganizationId = scope, items });
    }

    private static async Task<IResult> ListEmailOperationsAsync(
        HttpContext context,
        int? organizationId,
        string? status,
        EmailOperationsService service,
        CancellationToken cancellationToken)
    {
        var actor = StaffAuthenticationEndpoints.RequireCurrentStaff(context);
        if (!EmailOperationsService.CanOperate(actor))
        {
            return Results.Json(new { code = "staff_scope_forbidden" }, statusCode: StatusCodes.Status403Forbidden);
        }
        if (actor.Role != "super_admin" && organizationId.HasValue && organizationId != actor.OrganizationId)
        {
            return Results.Json(new { code = "staff_scope_forbidden" }, statusCode: StatusCodes.Status403Forbidden);
        }
        var items = await service.ListAsync(actor, organizationId, status, cancellationToken);
        return Results.Json(new { items });
    }

    private static async Task<IResult> QueueTestEmailAsync(
        HttpContext context,
        int? organizationId,
        EmailOperationsService service,
        CancellationToken cancellationToken)
    {
        var result = await service.QueueTestAsync(
            StaffAuthenticationEndpoints.RequireCurrentStaff(context), organizationId, cancellationToken);
        var status = result.Code switch
        {
            "queued" => StatusCodes.Status202Accepted,
            "suppressed" => StatusCodes.Status200OK,
            "staff_scope_forbidden" => StatusCodes.Status403Forbidden,
            "organization_inactive" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        };
        return Results.Json(new { code = result.Code, data = result.Data }, statusCode: status);
    }

    private static async Task<IResult> RetryEmailAsync(
        HttpContext context,
        long id,
        AdministrationVersionInput input,
        EmailOperationsService service,
        CancellationToken cancellationToken)
    {
        var result = await service.RetryAsync(
            StaffAuthenticationEndpoints.RequireCurrentStaff(context), id, input.Version, cancellationToken);
        var status = result.Code switch
        {
            "queued" => StatusCodes.Status202Accepted,
            "not_found" => StatusCodes.Status404NotFound,
            "staff_scope_forbidden" => StatusCodes.Status403Forbidden,
            "stale_version" or "email_not_retryable" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        };
        return Results.Json(new { code = result.Code, data = result.Data }, statusCode: status);
    }

    private static IResult ToResult(AdministrationResult result)
    {
        var statusCode = result.Code switch
        {
            "staff_scope_forbidden" => StatusCodes.Status403Forbidden,
            "organization_not_found" or "format_not_found" => StatusCodes.Status404NotFound,
            "stale_version" or "format_referenced" or "system_format_durable" or "format_version_required" or
                "settings_version_required" or "invalid_settings_version" or "organization_version_required" => StatusCodes.Status409Conflict,
            "staff_session_invalid" => StatusCodes.Status401Unauthorized,
            "polaris_unavailable" or "patron_codes_unavailable" => StatusCodes.Status502BadGateway,
            "ok" or "saved" or "reset" or "synced" or "activated" or "deactivated" or
                "polaris_connected" or "branding_saved" or "format_deleted" => StatusCodes.Status200OK,
            _ => StatusCodes.Status400BadRequest
        };
        return Results.Json(new { code = result.Code, message = result.Message, data = result.Data }, statusCode: statusCode);
    }

    private static IResult Invalid(InvalidOperationException exception) =>
        Results.BadRequest(new { code = "settings_invalid", message = exception.Message });

    private static async Task<byte[]> ReadFileAsync(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length > LogoImageValidator.MaxBytes)
        {
            throw new InvalidOperationException("The logo must be between 1 byte and 2 MB.");
        }
        await using var input = file.OpenReadStream();
        await using var stream = new MemoryStream(Math.Min((int)Math.Max(file.Length, 0), LogoImageValidator.MaxBytes));
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (stream.Length + read > LogoImageValidator.MaxBytes)
            {
                throw new InvalidOperationException("The logo must be between 1 byte and 2 MB.");
            }
            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (stream.Length == 0)
        {
            throw new InvalidOperationException("The logo must be between 1 byte and 2 MB.");
        }
        return stream.ToArray();
    }
}
