namespace Asap.Web.Features.Email;

public interface IEmailOutboxDispatcher
{
    void Enqueue(long outboxId);
}
