using Hangfire;

namespace Asap.Web.Features.Patron;

public interface IIdentifierLookupDispatcher
{
    void Enqueue(long requestId, string identifier, int organizationId, byte[] expectedRowVersion);
}

public sealed class IdentifierLookupDispatcher(IBackgroundJobClient jobs) : IIdentifierLookupDispatcher
{
    public void Enqueue(long requestId, string identifier, int organizationId, byte[] expectedRowVersion) =>
        jobs.Enqueue<IdentifierLookupJobs>(worker => worker.ProcessAsync(
            requestId,
            identifier,
            organizationId,
            expectedRowVersion,
            CancellationToken.None));
}

public sealed class IdentifierLookupJobs(PatronSuggestionService processor)
{
    public Task ProcessAsync(
        long requestId,
        string identifier,
        int organizationId,
        byte[] expectedRowVersion,
        CancellationToken cancellationToken) =>
        processor.ProcessIdentifierLookupAsync(
            requestId,
            identifier,
            organizationId,
            expectedRowVersion,
            cancellationToken);
}
