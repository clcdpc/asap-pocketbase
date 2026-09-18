namespace Asap.Web.Infrastructure.Health;

public interface IReadinessService
{
    Task<ReadinessResult> CheckAsync(CancellationToken cancellationToken);
}
