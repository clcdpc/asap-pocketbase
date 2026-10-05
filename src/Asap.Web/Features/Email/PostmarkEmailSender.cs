using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Email;

public sealed class PostmarkEmailSender(
    IDbContextFactory<AsapDbContext> contextFactory,
    IntegrationCredentialProtector credentialProtector,
    IHttpClientFactory httpClientFactory) : IEmailSender
{
    private static readonly Uri SendUri = new("https://api.postmarkapp.com/email");
    private static readonly Uri ServerUri = new("https://api.postmarkapp.com/server");

    public async Task<EmailTransportReadiness> CheckReadinessAsync(
        int organizationId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CheckProviderReadinessAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new EmailOperationalException("Postmark readiness could not be checked.", exception);
        }
    }

    private async Task<EmailTransportReadiness> CheckProviderReadinessAsync(CancellationToken cancellationToken)
    {
        var token = await ReadSystemTokenAsync(cancellationToken);
        if (token is null)
        {
            return EmailTransportReadiness.NotConfigured;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var request = new HttpRequestMessage(HttpMethod.Get, ServerUri);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation("X-Postmark-Server-Token", token);
        using var response = await httpClientFactory.CreateClient("Postmark")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return EmailTransportReadiness.NotConfigured;
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new EmailOperationalException("Postmark server readiness could not be checked.");
        }

        await using var content = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var result = await JsonDocument.ParseAsync(content, cancellationToken: timeout.Token);
        if (result.RootElement.ValueKind != JsonValueKind.Object ||
            !result.RootElement.TryGetProperty("ID", out var id) ||
            id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var serverId) || serverId <= 0)
        {
            throw new EmailOperationalException("Postmark server readiness response was invalid.");
        }
        if (!result.RootElement.TryGetProperty("DeliveryType", out var deliveryType) ||
            deliveryType.ValueKind != JsonValueKind.String)
        {
            throw new EmailOperationalException("Postmark server delivery type was unavailable.");
        }
        return deliveryType.GetString() switch
        {
            "Live" => EmailTransportReadiness.Configured,
            "Sandbox" => EmailTransportReadiness.Sandbox,
            _ => throw new EmailOperationalException("Postmark server delivery type was unknown.")
        };
    }

    public async Task<EmailSendResult> SendAsync(EmailEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var token = await ReadSystemTokenAsync(cancellationToken);
        if (token is null)
        {
            return EmailSendResult.NotConfigured;
        }

        var from = string.IsNullOrWhiteSpace(envelope.FromName)
            ? new MailAddress(envelope.FromAddress).Address
            : new MailAddress(envelope.FromAddress, envelope.FromName).ToString();
        var body = JsonSerializer.Serialize(new
        {
            From = from,
            To = new MailAddress(envelope.ToAddress).Address,
            envelope.Subject,
            TextBody = envelope.BodyText,
            HtmlBody = envelope.BodyHtml
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, SendUri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("X-Postmark-Server-Token", token);
        using var response = await httpClientFactory.CreateClient("Postmark")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Postmark rejected the email request.");
        }

        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var result = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
        var root = result.RootElement;
        if (!root.TryGetProperty("ErrorCode", out var errorCode) || errorCode.GetInt32() != 0 ||
            !root.TryGetProperty("MessageID", out var messageId) ||
            string.IsNullOrWhiteSpace(messageId.GetString()))
        {
            throw new InvalidOperationException("Postmark did not confirm email acceptance.");
        }
        return new EmailSendResult(messageId.GetString()!);
    }

    private async Task<string?> ReadSystemTokenAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var protectedToken = await context.EmailSettings.AsNoTracking()
            .Where(item => item.OrganizationId == 1)
            .Select(item => item.ProtectedServerToken)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(protectedToken))
        {
            return null;
        }

        try
        {
            var token = credentialProtector.Unprotect(protectedToken).Trim();
            return token.Length == 0 || token == "POSTMARK_API_TEST" ? null : token;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}
