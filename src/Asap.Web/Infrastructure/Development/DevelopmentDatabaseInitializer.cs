using Asap.Web.Infrastructure.Configuration;

namespace Asap.Web.Infrastructure.Development;

public sealed class DevelopmentDatabaseInitializer(
    ExternalConfiguration configuration,
    IWebHostEnvironment environment,
    DacpacDeploymentService deployment,
    RuntimeInitializationState state,
    ILogger<DevelopmentDatabaseInitializer> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
        {
            return Task.CompletedTask;
        }

        try
        {
            var dacpacPath = Path.Combine(AppContext.BaseDirectory, "Database", "Asap.Database.dacpac");
            if (!File.Exists(dacpacPath))
            {
                throw new FileNotFoundException("The application DACPAC was not copied to the output.", dacpacPath);
            }

            deployment.Deploy(configuration.ConnectionStrings.AsapDatabase!, dacpacPath);
            logger.LogInformation("Development database DACPAC deployment completed.");
        }
        catch (Exception exception)
        {
            state.MarkFailed("development_database_deployment_failed");
            logger.LogError(exception, "Development database DACPAC deployment failed; readiness remains unhealthy.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
