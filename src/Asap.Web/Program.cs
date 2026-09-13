using Asap.Security;
using Asap.Web.Features.Email;
using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Development;
using Asap.Web.Infrastructure.Health;
using Asap.Web.Infrastructure.Jobs;
using Asap.Web.Infrastructure.Logging;
using Asap.Web.Infrastructure.Security;
using Hangfire;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using NLog.Web;
using System.Threading.RateLimiting;
using Asap.Web.Features.Patron;

var builder = WebApplication.CreateBuilder(args);

var configurationResult = ExternalConfigurationLoader.Load(
    builder.Configuration,
    builder.Environment.ContentRootPath,
    allowFileWithinContentRoot: builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"),
    allowSqlAuthenticationForTesting: builder.Environment.IsEnvironment("Testing"));

var externalConfiguration = configurationResult.Value;
var certificate = externalConfiguration is null
    ? null
    : CertificateLocator.FindWithPrivateKey(
        externalConfiguration.Application.DataProtectionKeyEncryptionCertificateThumbprint!);

if (externalConfiguration is not null && certificate is null)
{
    configurationResult = new ConfigurationLoadResult(
        null,
        configurationResult.SourcePath,
        ["data_protection_certificate_unavailable"]);
    externalConfiguration = null;
}
else if (externalConfiguration is not null &&
         !Directory.Exists(externalConfiguration.Application.DataProtectionKeysPath))
{
    configurationResult = new ConfigurationLoadResult(
        null,
        configurationResult.SourcePath,
        ["data_protection_path_unavailable"]);
    externalConfiguration = null;
}

NLogConfiguration.Configure(externalConfiguration?.Application.LogPath, builder.Environment.ContentRootPath);
builder.Logging.ClearProviders();
builder.Host.UseNLog();

builder.Services.AddSingleton(configurationResult);
builder.Services.AddSingleton<RuntimeInitializationState>();
builder.Services.AddSingleton<IReadinessService, ReadinessService>();
var patronLoginRateLimit = externalConfiguration?.PatronLoginRateLimit ?? new PatronLoginRateLimitOptions();
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("patron-login", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = patronLoginRateLimit.PermitLimit,
                Window = TimeSpan.FromSeconds(patronLoginRateLimit.WindowSeconds),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        }
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { message = "Too many login attempts. Please try again later." },
            cancellationToken);
    };
});

if (externalConfiguration is not null)
{
    builder.Services.AddSingleton(externalConfiguration);
    builder.Services.AddDbContextFactory<AsapDbContext>(options =>
        options.UseSqlServer(externalConfiguration.ConnectionStrings.AsapDatabase));
    builder.Services.AddHangfire(configuration => configuration
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UseSqlServerStorage(
            externalConfiguration.ConnectionStrings.HangfireDatabase,
            HangfireStorageConfiguration.CreateRuntimeOptions()));

    builder.Services
        .AddDataProtection()
        .SetApplicationName(SecurityContract.DataProtectionApplicationName)
        .PersistKeysToFileSystem(new DirectoryInfo(externalConfiguration.Application.DataProtectionKeysPath!))
        .ProtectKeysWithCertificate(certificate!);

    builder.Services.AddSingleton<IntegrationCredentialProtector>();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddHttpClient("Polaris");
    builder.Services.AddSingleton<PatronConfigurationService>();
    builder.Services.AddSingleton<PatronSessionService>();
    builder.Services.AddSingleton<PatronSuggestionService>();
    if (builder.Environment.IsEnvironment("Testing") &&
        builder.Configuration.GetValue<bool>("Testing:UseDeterministicPatronProvider"))
    {
        builder.Services.AddSingleton<IPatronProvider, Asap.Web.Infrastructure.Testing.DeterministicTestingPatronProvider>();
    }
    else
    {
        builder.Services.AddSingleton<IPatronProvider, PolarisPatronProvider>();
    }
    builder.Services.AddHostedService<DataProtectionInitializer>();
    builder.Services.AddSingleton<RecipientDomainPolicy>();
    builder.Services.AddSingleton(EmailOutboxRuntimeOptions.Default);
    builder.Services.AddTransient<EmailOutboxJobs>();
    builder.Services.AddSingleton<IEmailOutboxDispatcher, EmailOutboxDispatcher>();
    builder.Services.AddSingleton<IHangfireSchemaCompatibilityChecker, HangfireSchemaCompatibilityChecker>();
    builder.Services.AddSingleton<IEmailSender>(_ => new FileEmailSender(
        Path.Combine(builder.Environment.ContentRootPath, ".artifacts", "dev-email")));
    builder.Services.AddSingleton<DacpacDeploymentService>();
    builder.Services.AddHostedService<DevelopmentDatabaseInitializer>();
    builder.Services.AddHostedService<HangfireWorkerHostedService>();
}

var app = builder.Build();

if (!configurationResult.IsValid)
{
    app.Logger.LogError(
        "ASAP startup configuration is invalid. Error codes: {ConfigurationErrors}",
        string.Join(',', configurationResult.Errors));
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseRateLimiter();
app.UseDefaultFiles();
if (externalConfiguration is not null)
{
    app.UseMiddleware<PatronContentSecurityPolicyMiddleware>();
    app.UseMiddleware<BusinessReadinessMiddleware>();
}
app.UseStaticFiles();

app.MapGet("/health/live", () => Results.Json(new { status = "healthy" }));
app.MapGet("/health/ready", async (IReadinessService readiness, CancellationToken cancellationToken) =>
{
    var result = await readiness.CheckAsync(cancellationToken);
    return Results.Json(
        new { status = result.IsReady ? "healthy" : "unhealthy" },
        statusCode: result.IsReady ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
});

if (externalConfiguration is not null)
{
    app.MapPatronEndpoints();
}

app.Run();

public partial class Program;
