namespace Asap.Web.Features.Email;

public interface IEmailSender
{
    Task<EmailTransportReadiness> CheckReadinessAsync(
        int organizationId,
        CancellationToken cancellationToken);

    Task<EmailSendResult> SendAsync(EmailEnvelope envelope, CancellationToken cancellationToken);
}

public static class EmailDeliveryModes
{
    public const string Capture = "capture";
    public const string Live = "live";
}

public sealed record EmailTransportReadiness(
    bool IsConfigured,
    string DeliveryMode = EmailDeliveryModes.Capture,
    string? Code = null)
{
    public bool IsLive => string.Equals(DeliveryMode, EmailDeliveryModes.Live, StringComparison.Ordinal);

    public static EmailTransportReadiness Configured { get; } =
        new(true, EmailDeliveryModes.Capture, "non_delivery_mode");

    public static EmailTransportReadiness NotConfigured { get; } =
        new(false, EmailDeliveryModes.Live, "mail_not_configured");
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
    NotConfigured,
    Rejected
}

public sealed record EmailSendResult(
    EmailSendOutcome Outcome,
    string? ProviderMessageId,
    string? DeliveryMode = null,
    string? ErrorCode = null)
{
    public EmailSendResult(string providerMessageId) : this(EmailSendOutcome.Sent, providerMessageId)
    {
    }

    public static EmailSendResult NotConfigured { get; } =
        new(EmailSendOutcome.NotConfigured, null);
}

public sealed class EmailTransportException : Exception
{
    public EmailTransportException(
        string code,
        string detail,
        bool isAmbiguous,
        Exception? innerException = null)
        : base(detail, innerException)
    {
        Code = code;
        IsAmbiguous = isAmbiguous;
    }

    public string Code { get; }

    public bool IsAmbiguous { get; }
}
