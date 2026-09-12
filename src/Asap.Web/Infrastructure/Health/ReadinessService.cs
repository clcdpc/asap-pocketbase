using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Development;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Infrastructure.Health;

public sealed class ReadinessService(
    ConfigurationLoadResult configurationResult,
    RuntimeInitializationState initializationState,
    IServiceProvider services,
    ILogger<ReadinessService> logger) : IReadinessService
{
    public async Task<ReadinessResult> CheckAsync(CancellationToken cancellationToken)
    {
        if (!configurationResult.IsValid)
        {
            return ReadinessResult.NotReady("configuration_invalid");
        }

        if (!initializationState.IsHealthy)
        {
            return ReadinessResult.NotReady(initializationState.ErrorCode!);
        }

        var factory = services.GetService<IDbContextFactory<AsapDbContext>>();
        if (factory is null)
        {
            return ReadinessResult.NotReady("database_not_configured");
        }

        try
        {
            await using var context = await factory.CreateDbContextAsync(cancellationToken);
            var version = await context.SchemaVersions
                .AsNoTracking()
                .Where(item => item.Id == 1)
                .Select(item => (int?)item.Version)
                .SingleOrDefaultAsync(cancellationToken);

            return version == SchemaVersion.ExpectedVersion
                ? ReadinessResult.Ready
                : ReadinessResult.NotReady("schema_version_mismatch");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "SQL readiness check failed.");
            return ReadinessResult.NotReady("database_unavailable");
        }
    }
}
