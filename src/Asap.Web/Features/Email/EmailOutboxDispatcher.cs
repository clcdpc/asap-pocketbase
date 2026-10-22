using Hangfire;

namespace Asap.Web.Features.Email;

public sealed class EmailOutboxDispatcher(IBackgroundJobClient jobs) : IEmailOutboxDispatcher
{
    public void Enqueue(long outboxId) =>
        jobs.Enqueue<EmailOutboxJobs>(
            worker => worker.DeliverAsync(outboxId, CancellationToken.None));
}
