using Asap.Security;
using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Development;
using Asap.Web.Infrastructure.Health;
using Asap.Web.Infrastructure.Logging;
using Asap.Web.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NLog.Web;

var builder = WebApplication.CreateBuilder(args);

var configurationResult = ExternalConfigurationLoader.Load(
    builder.Configuration,
    builder.Environment.ContentRootPath,
    allowFileWithinContentRoot: builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"));

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

if (externalConfiguration is not null)
{
    builder.Services.AddSingleton(externalConfiguration);
    builder.Services.AddDbContextFactory<AsapDbContext>(options =>
        options.UseSqlServer(externalConfiguration.ConnectionStrings.AsapDatabase));

    builder.Services
        .AddDataProtection()
        .SetApplicationName(SecurityContract.DataProtectionApplicationName)
        .PersistKeysToFileSystem(new DirectoryInfo(externalConfiguration.Application.DataProtectionKeysPath!))
        .ProtectKeysWithCertificate(certificate!);

    builder.Services.AddSingleton<IntegrationCredentialProtector>();
    builder.Services.AddHostedService<DataProtectionInitializer>();
    builder.Services.AddSingleton<RecipientDomainPolicy>();
    builder.Services.AddSingleton<DacpacDeploymentService>();
    builder.Services.AddHostedService<DevelopmentDatabaseInitializer>();
}

var app = builder.Build();

if (!configurationResult.IsValid)
{
    app.Logger.LogError(
        "ASAP startup configuration is invalid. Error codes: {ConfigurationErrors}",
        string.Join(',', configurationResult.Errors));
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseStaticFiles();

app.MapGet("/health/live", () => Results.Json(new { status = "healthy" }));
app.MapGet("/health/ready", async (IReadinessService readiness, CancellationToken cancellationToken) =>
{
    var result = await readiness.CheckAsync(cancellationToken);
    return Results.Json(
        new { status = result.IsReady ? "healthy" : "unhealthy" },
        statusCode: result.IsReady ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
});

app.Run();

public partial class Program;
