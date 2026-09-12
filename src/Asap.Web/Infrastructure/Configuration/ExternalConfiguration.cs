namespace Asap.Web.Infrastructure.Configuration;

public sealed class ExternalConfiguration
{
    public DeploymentEnvironmentOptions Environment { get; set; } = new();

    public ConnectionStringOptions ConnectionStrings { get; set; } = new();

    public AuthenticationOptions Authentication { get; set; } = new();

    public ApplicationOptions Application { get; set; } = new();

    public EmailSafetyOptions EmailSafety { get; set; } = new();

    public HangfireOptions Hangfire { get; set; } = new();
}

public sealed class DeploymentEnvironmentOptions
{
    public string? Name { get; set; }

    public bool? IsNonProduction { get; set; }

    public string? UiBannerText { get; set; }
}

public sealed class ConnectionStringOptions
{
    public string? AsapDatabase { get; set; }

    public string? HangfireDatabase { get; set; }
}

public sealed class AuthenticationOptions
{
    public EntraOptions Entra { get; set; } = new();
}

public sealed class EntraOptions
{
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public List<string>? AllowedTenantIds { get; set; }

    public InitialSuperAdminOptions InitialSuperAdmin { get; set; } = new();
}

public sealed class InitialSuperAdminOptions
{
    public string? TenantId { get; set; }

    public string? ObjectId { get; set; }

    public string? UserPrincipalName { get; set; }

    public string? DisplayName { get; set; }

    public string? NotificationEmail { get; set; }
}

public sealed class ApplicationOptions
{
    public string? BusinessTimeZone { get; set; }

    public string? DataProtectionKeysPath { get; set; }

    public string? DataProtectionKeyEncryptionCertificateThumbprint { get; set; }

    public string? LogPath { get; set; }
}

public sealed class EmailSafetyOptions
{
    public List<string>? AllowedRecipientDomains { get; set; }
}

public sealed class HangfireOptions
{
    public Dictionary<string, string>? Schedules { get; set; }

    public ProcessingLimitsOptions ProcessingLimits { get; set; } = new();
}

public sealed class ProcessingLimitsOptions
{
    public ProcessingLimit? Default { get; set; }

    public ProcessingLimit? Timeouts { get; set; }

    public Dictionary<string, ProcessingLimit>? Queues { get; set; }
}

public sealed class ProcessingLimit
{
    public int? PageSize { get; set; }

    public int? MaxPerRun { get; set; }
}
