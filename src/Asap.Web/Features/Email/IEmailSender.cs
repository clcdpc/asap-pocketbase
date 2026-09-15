namespace Asap.Web.Features.Email;

public interface IEmailSender
{
    Task<EmailTransportReadiness> CheckReadinessAsync(
        int organizationId,
        CancellationToken cancellationToken);

    Task<EmailSendResult> SendAsync(EmailEnvelope envelope, CancellationToken cancellationToken);
}

public sealed record EmailTransportReadiness(bool IsConfigured)
{
    public static EmailTransportReadiness Configured { get; } = new(true);

    public static EmailTransportReadiness NotConfigured { get; } = new(false);
}

public sealed record EmailEnvelope(
    long OutboxId,
    int OrganizationId,
    string? BusinessKey,
    string ToAddress,
    string FromAddress,
    string? FromName,
    string Subject,
    string? BodyText,
    string? BodyHtml);

public enum EmailSendOutcome
{
    Sent,
    NotConfigured
}

public sealed record EmailSendResult(EmailSendOutcome Outcome, string? ProviderMessageId)
{
    public EmailSendResult(string providerMessageId) : this(EmailSendOutcome.Sent, providerMessageId)
    {
    }

    public static EmailSendResult NotConfigured { get; } =
        new(EmailSendOutcome.NotConfigured, null);
}
