using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Asap.Web.Infrastructure.Configuration;

namespace Asap.Web.Features.Email;

public sealed class PostmarkEmailSender(
    HttpClient httpClient,
    ExternalConfiguration configuration,
    IEmailServerTokenResolver tokenResolver,
    ILogger<PostmarkEmailSender> logger) : IEmailSender
{
    private const string Endpoint = "email";

    public async Task<EmailTransportReadiness> CheckReadinessAsync(
        int organizationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsLiveMode(configuration.EmailTransport.Mode))
        {
            return new EmailTransportReadiness(true, EmailDeliveryModes.Capture, "non_delivery_mode");
        }

        var resolution = await tokenResolver.ResolveAsync(organizationId, cancellationToken);
        return resolution.IsConfigured
            ? new EmailTransportReadiness(true, EmailDeliveryModes.Live)
            : new EmailTransportReadiness(false, EmailDeliveryModes.Live, resolution.Code);
    }

    public async Task<EmailSendResult> SendAsync(
        EmailEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsLiveMode(configuration.EmailTransport.Mode))
        {
            return EmailSendResult.NotConfigured;
        }

        var resolution = await tokenResolver.ResolveAsync(envelope.OrganizationId, cancellationToken);
        if (!resolution.IsConfigured)
        {
            return EmailSendResult.NotConfigured with
            {
                DeliveryMode = EmailDeliveryModes.Live,
                ErrorCode = resolution.Code
            };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Add("X-Postmark-Server-Token", resolution.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                From = FormatFrom(envelope.FromAddress, envelope.FromName),
                To = envelope.ToAddress,
                Subject = envelope.Subject,
                TextBody = envelope.BodyText,
                HtmlBody = envelope.BodyHtml
            }),
            Encoding.UTF8,
            "application/json");

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new EmailTransportException(
                "provider_unavailable",
                exception.GetType().Name,
                isAmbiguous: true,
                exception);
        }

        using (response)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var code = KnownFailureCode(response.StatusCode);
                var ambiguous = (int)response.StatusCode >= 500 ||
                    response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
                throw new EmailTransportException(
                    code,
                    $"Postmark returned HTTP {(int)response.StatusCode}.",
                    ambiguous);
            }

            if (!TryReadSuccess(responseBody, out var messageId, out var providerError))
            {
                if (providerError)
                {
                    throw new EmailTransportException(
                        "provider_rejected",
                        "Postmark rejected the message.",
                        isAmbiguous: false);
                }

                logger.LogWarning("Postmark returned a successful response without a usable message ID.");
                throw new EmailTransportException(
                    "provider_malformed_response",
                    "Postmark returned an unusable success response.",
                    isAmbiguous: true);
            }

            return new EmailSendResult(
                EmailSendOutcome.Sent,
                messageId,
                EmailDeliveryModes.Live);
        }
    }

    private static bool IsLiveMode(string? mode) =>
        string.Equals(mode, EmailDeliveryModes.Live, StringComparison.OrdinalIgnoreCase);

    private static string FormatFrom(string address, string? name) =>
        string.IsNullOrWhiteSpace(name) ? address : $"{name.Trim()} <{address}>";

    private static bool TryReadSuccess(
        string responseBody,
        out string? messageId,
        out bool providerError)
    {
        messageId = null;
        providerError = false;
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            if (root.TryGetProperty("ErrorCode", out var errorCode) &&
                errorCode.ValueKind == JsonValueKind.Number &&
                errorCode.TryGetInt32(out var numericErrorCode) &&
                numericErrorCode != 0)
            {
                providerError = true;
                return false;
            }

            if (!root.TryGetProperty("MessageID", out var messageIdElement) ||
                messageIdElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            messageId = messageIdElement.GetString()?.Trim();
            return !string.IsNullOrWhiteSpace(messageId);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string KnownFailureCode(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "provider_authentication_failed",
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => "provider_rejected",
        HttpStatusCode.TooManyRequests => "provider_rate_limited",
        _ => "provider_unavailable"
    };
}
