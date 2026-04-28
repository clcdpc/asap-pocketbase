using Asap.Core.Models;

namespace Asap.Core.Services;

public interface ITitleRequestService
{
    Task<IReadOnlyList<TitleRequest>> GetTitleRequestsAsync(string? status, string? libraryOrgId, int page, int pageSize, CancellationToken cancellationToken);
    Task RejectAsync(RejectTitleRequestCommand command, CancellationToken cancellationToken);
}
