namespace Asap.Core.Services;

public interface IOutstandingTimeoutProcessor
{
    Task<int> ProcessAsync(CancellationToken cancellationToken);
}
