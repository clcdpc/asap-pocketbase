using Asap.Web.Models;

namespace Asap.Web.Repositories;

public interface ISuggestionRepository
{
    Task<IReadOnlyList<Suggestion>> GetOpenAsync(CancellationToken cancellationToken);
    Task<int> CreateAsync(SuggestionCreateInput input, CancellationToken cancellationToken);
}
