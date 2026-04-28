using Asap.Core.Services;
using Cronos;

namespace Asap.Web.Background;

public sealed class OutstandingTimeoutHostedService(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    ILogger<OutstandingTimeoutHostedService> logger) : BackgroundService
{
    private readonly CronExpression _schedule = CronExpression.Parse(
        configuration["Jobs:OutstandingTimeoutCron"] ?? "0 * * * *",
        CronFormat.Standard);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var nextUtc = _schedule.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Utc);
            if (!nextUtc.HasValue)
            {
                logger.LogWarning("No future run scheduled for outstanding timeout processor.");
                return;
            }

            var delay = nextUtc.Value - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, stoppingToken);
            }

            using var scope = serviceProvider.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<IOutstandingTimeoutProcessor>();
            var updated = await processor.ProcessAsync(stoppingToken);
            logger.LogInformation("Outstanding timeout processor closed {Count} requests.", updated);
        }
    }
}
