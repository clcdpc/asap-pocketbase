using Asap.Core.Models;
using Asap.Core.Services;

namespace Asap.Infrastructure.Repositories;

public sealed class TitleRequestService(ITitleRequestRepository repository) : ITitleRequestService
{
    public Task<IReadOnlyList<TitleRequest>> GetTitleRequestsAsync(string? status, string? libraryOrgId, int page, int pageSize, CancellationToken cancellationToken)
        => repository.ListAsync(status, libraryOrgId, page, pageSize, cancellationToken);

    public async Task RejectAsync(RejectTitleRequestCommand command, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.RequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.CloseReason);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.StaffUserId);

        var updated = await repository.RejectAsync(command, cancellationToken);
        if (!updated)
        {
            throw new InvalidOperationException($"Unable to reject request '{command.RequestId}'.");
        }
    }
}
