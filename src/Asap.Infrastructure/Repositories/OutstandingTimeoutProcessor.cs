using Asap.Core.Services;

namespace Asap.Infrastructure.Repositories;

public sealed class OutstandingTimeoutProcessor(ITitleRequestRepository repository) : IOutstandingTimeoutProcessor
{
    public Task<int> ProcessAsync(CancellationToken cancellationToken) => repository.TimeoutOutstandingAsync(cancellationToken);
}
