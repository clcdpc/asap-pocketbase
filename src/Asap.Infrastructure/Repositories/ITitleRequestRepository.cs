using Asap.Core.Models;

namespace Asap.Infrastructure.Repositories;

public interface ITitleRequestRepository
{
    Task<IReadOnlyList<TitleRequest>> ListAsync(string? status, string? libraryOrgId, int page, int pageSize, CancellationToken cancellationToken);
    Task<bool> RejectAsync(RejectTitleRequestCommand command, CancellationToken cancellationToken);
    Task<int> TimeoutOutstandingAsync(CancellationToken cancellationToken);
}
