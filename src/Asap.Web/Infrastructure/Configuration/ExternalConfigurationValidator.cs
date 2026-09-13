using System.Globalization;
using System.Net.Mail;
using System.Text.RegularExpressions;
using Cronos;
using Microsoft.Data.SqlClient;

namespace Asap.Web.Infrastructure.Configuration;

public static partial class ExternalConfigurationValidator
{
    public static readonly IReadOnlySet<string> RequiredScheduleKeys = new HashSet<string>(
        [
            "WorkflowProcessing",
            "IdentifierProcessing",
            "OrganizationRefresh",
            "WeeklyStaffSummary",
            "EmailOutboxSweep",
            "PatronSessionCleanup",
            "EmailPayloadCleanup"
        ],
        StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> RequiredQueueKeys = new HashSet<string>(
        [
            "IdentifierProcessing",
            "PurchasePromotion",
            "HoldPlacement",
            "FulfillmentTracking",
            "OutstandingTimeout",
            "PendingHoldTimeout",
            "HoldPickupTimeout",
            "AdditionalCopyTimeout"
        ],
        StringComparer.Ordinal);

    public static IReadOnlyList<string> Validate(ExternalConfiguration value)
    {
        var errors = new List<string>();

        if (value.Environment is null ||
            value.ConnectionStrings is null ||
            value.Authentication?.Entra is null ||
            value.Application is null ||
            value.EmailSafety is null ||
            value.PatronLoginRateLimit is null ||
            value.Hangfire?.ProcessingLimits is null)
        {
            errors.Add("configuration_section_missing");
            return errors;
        }

        RequireText(value.Environment.Name, "environment_name_missing", errors);
        if (value.Environment.IsNonProduction is null)
        {
            errors.Add("environment_is_nonproduction_missing");
        }

        ValidateConnectionString(value.ConnectionStrings.AsapDatabase, "asap_database", errors);
        ValidateConnectionString(value.ConnectionStrings.HangfireDatabase, "hangfire_database", errors);
        ValidateEntra(value.Authentication.Entra, errors);
        ValidateApplication(value.Application, errors);
        ValidateRecipientDomains(value.EmailSafety.AllowedRecipientDomains, errors);
        ValidatePatronLoginRateLimit(value.PatronLoginRateLimit, errors);
        ValidateHangfire(value.Hangfire, errors);

        return errors;
    }

    public static bool IsValidRecipientDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain) || domain != domain.Trim() || domain.Length > 253)
        {
            return false;
        }

        if (domain.Contains('*', StringComparison.Ordinal) ||
            domain.Contains('@', StringComparison.Ordinal) ||
            domain.Contains("://", StringComparison.Ordinal) ||
            domain.StartsWith(".", StringComparison.Ordinal) ||
            domain.EndsWith(".", StringComparison.Ordinal) ||
            domain.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        string ascii;
        try
        {
            ascii = new IdnMapping().GetAscii(domain);
        }
        catch (ArgumentException)
        {
            return false;
        }

        return ascii.Split('.').All(label =>
            label.Length is >= 1 and <= 63 &&
            DomainLabelRegex().IsMatch(label) &&
            label[0] != '-' &&
            label[^1] != '-');
    }

    private static void ValidateConnectionString(
        string? connectionString,
        string name,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            errors.Add($"{name}_connection_missing");
            return;
        }

        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(builder.DataSource) || string.IsNullOrWhiteSpace(builder.InitialCatalog))
            {
                errors.Add($"{name}_connection_invalid");
            }

            if (!builder.IntegratedSecurity)
            {
                errors.Add($"{name}_integrated_security_required");
            }
        }
        catch (ArgumentException)
        {
            errors.Add($"{name}_connection_invalid");
        }
    }

    private static void ValidateEntra(EntraOptions entra, ICollection<string> errors)
    {
        ValidateGuid(entra.ClientId, "entra_client_id_invalid", errors);
        if (string.IsNullOrWhiteSpace(entra.ClientSecret) ||
            entra.ClientSecret.StartsWith("REPLACE-", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("entra_client_secret_missing");
        }

        var tenantIds = entra.AllowedTenantIds;
        if (tenantIds is null || tenantIds.Count == 0)
        {
            errors.Add("entra_allowed_tenants_missing");
        }
        else
        {
            foreach (var tenantId in tenantIds)
            {
                ValidateGuid(tenantId, "entra_allowed_tenant_invalid", errors);
            }

            if (tenantIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != tenantIds.Count)
            {
                errors.Add("entra_allowed_tenant_duplicate");
            }
        }

        var admin = entra.InitialSuperAdmin;
        if (admin is null)
        {
            errors.Add("initial_admin_missing");
            return;
        }
        ValidateGuid(admin.TenantId, "initial_admin_tenant_invalid", errors);
        ValidateGuid(admin.ObjectId, "initial_admin_object_invalid", errors);
        RequireText(admin.UserPrincipalName, "initial_admin_upn_missing", errors);
        RequireText(admin.DisplayName, "initial_admin_display_name_missing", errors);

        if (!IsEmail(admin.UserPrincipalName) || !IsEmail(admin.NotificationEmail))
        {
            errors.Add("initial_admin_email_invalid");
        }

        if (tenantIds is not null &&
            admin.TenantId is not null &&
            !tenantIds.Contains(admin.TenantId, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add("initial_admin_tenant_not_allowed");
        }
    }

    private static void ValidateApplication(ApplicationOptions application, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(application.BusinessTimeZone))
        {
            errors.Add("business_timezone_missing");
        }
        else
        {
            try
            {
                _ = TimeZoneInfo.FindSystemTimeZoneById(application.BusinessTimeZone);
            }
            catch (TimeZoneNotFoundException)
            {
                errors.Add("business_timezone_invalid");
            }
            catch (InvalidTimeZoneException)
            {
                errors.Add("business_timezone_invalid");
            }
        }

        ValidateAbsolutePath(application.DataProtectionKeysPath, "data_protection_path_invalid", errors);
        ValidateAbsolutePath(application.LogPath, "log_path_invalid", errors);
        RequireText(
            application.DataProtectionKeyEncryptionCertificateThumbprint,
            "data_protection_certificate_missing",
            errors);
    }

    private static void ValidateRecipientDomains(List<string>? domains, ICollection<string> errors)
    {
        if (domains is null)
        {
            return;
        }

        foreach (var domain in domains)
        {
            if (!IsValidRecipientDomain(domain))
            {
                errors.Add("recipient_domain_invalid");
            }
        }

        if (domains.Distinct(StringComparer.OrdinalIgnoreCase).Count() != domains.Count)
        {
            errors.Add("recipient_domain_duplicate");
        }
    }

    private static void ValidatePatronLoginRateLimit(
        PatronLoginRateLimitOptions options,
        ICollection<string> errors)
    {
        if (options.PermitLimit is < 1 or > 10_000)
        {
            errors.Add("patron_login_rate_limit_permit_invalid");
        }

        if (options.WindowSeconds is < 1 or > 86_400)
        {
            errors.Add("patron_login_rate_limit_window_invalid");
        }
    }

    private static void ValidateHangfire(HangfireOptions hangfire, ICollection<string> errors)
    {
        ValidateExactKeys(hangfire.Schedules?.Keys, RequiredScheduleKeys, "hangfire_schedules", errors);
        if (hangfire.Schedules is not null)
        {
            foreach (var schedule in hangfire.Schedules.Values)
            {
                if (string.IsNullOrWhiteSpace(schedule))
                {
                    errors.Add("hangfire_schedule_invalid");
                    continue;
                }

                try
                {
                    _ = CronExpression.Parse(schedule, CronFormat.Standard);
                }
                catch (CronFormatException)
                {
                    errors.Add("hangfire_schedule_invalid");
                }
            }
        }

        if (hangfire.ProcessingLimits.Default is null)
        {
            errors.Add("processing_default_missing");
        }
        else
        {
            ValidateRequiredLimit(hangfire.ProcessingLimits.Default, "processing_default", errors);
        }

        if (hangfire.ProcessingLimits.Timeouts is null)
        {
            errors.Add("processing_timeouts_missing");
        }
        else
        {
            ValidateOptionalLimit(hangfire.ProcessingLimits.Timeouts, "processing_timeouts", errors);
        }

        var queues = hangfire.ProcessingLimits.Queues;
        ValidateExactKeys(queues?.Keys, RequiredQueueKeys, "processing_queues", errors);
        if (queues is not null)
        {
            foreach (var (name, limit) in queues)
            {
                if (limit is null)
                {
                    errors.Add($"processing_queue_{name}_missing");
                }
                else
                {
                    ValidateOptionalLimit(limit, $"processing_queue_{name}", errors);
                }
            }
        }
    }

    private static void ValidateExactKeys(
        IEnumerable<string>? actualKeys,
        IReadOnlySet<string> expectedKeys,
        string name,
        ICollection<string> errors)
    {
        if (actualKeys is null || !expectedKeys.SetEquals(actualKeys))
        {
            errors.Add($"{name}_shape_invalid");
        }
    }

    private static void ValidateRequiredLimit(
        ProcessingLimit limit,
        string name,
        ICollection<string> errors)
    {
        if (limit.PageSize is null || limit.MaxPerRun is null)
        {
            errors.Add($"{name}_missing");
            return;
        }

        ValidateRange(limit.PageSize, limit.MaxPerRun, name, errors);
    }

    private static void ValidateOptionalLimit(
        ProcessingLimit limit,
        string name,
        ICollection<string> errors) =>
        ValidateRange(limit.PageSize, limit.MaxPerRun, name, errors);

    private static void ValidateRange(
        int? pageSize,
        int? maxPerRun,
        string name,
        ICollection<string> errors)
    {
        if (pageSize is not null and (< 1 or > 500))
        {
            errors.Add($"{name}_page_size_invalid");
        }

        if (maxPerRun is not null and (< 1 or > 5000))
        {
            errors.Add($"{name}_max_per_run_invalid");
        }
    }

    private static void ValidateAbsolutePath(string? path, string error, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            errors.Add(error);
        }
    }

    private static void ValidateGuid(string? value, string error, ICollection<string> errors)
    {
        if (!Guid.TryParse(value, out var parsed) || parsed == Guid.Empty)
        {
            errors.Add(error);
        }
    }

    private static void RequireText(string? value, string error, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(error);
        }
    }

    private static bool IsEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            return new MailAddress(value).Address.Equals(value, StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    [GeneratedRegex("^[A-Za-z0-9-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex DomainLabelRegex();
}
