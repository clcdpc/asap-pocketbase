using System.Text;
using System.Text.Encodings.Web;

namespace Asap.Web.Features.Email;

public sealed class FileEmailSender : IEmailSender
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string outputDirectory;

    public FileEmailSender(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        this.outputDirectory = Path.GetFullPath(outputDirectory);
    }

    public Task<EmailTransportReadiness> CheckReadinessAsync(
        int organizationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(EmailTransportReadiness.Configured);
    }

    public async Task<EmailSendResult> SendAsync(
        EmailEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(outputDirectory);

        var fileId = $"{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}-{envelope.OutboxId}-{Guid.NewGuid():N}";
        var finalPath = Path.Combine(outputDirectory, $"{fileId}.html");
        var temporaryPath = Path.Combine(outputDirectory, $".{fileId}.tmp");

        try
        {
            var document = BuildDocument(envelope);
            await File.WriteAllTextAsync(temporaryPath, document, Utf8WithoutBom, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, finalPath);
            return new EmailSendResult($"file:{fileId}");
        }
        catch
        {
            File.Delete(temporaryPath);
            throw;
        }
    }

    private static string BuildDocument(EmailEnvelope envelope)
    {
        var encoder = HtmlEncoder.Default;
        var from = string.IsNullOrWhiteSpace(envelope.FromName)
            ? envelope.FromAddress
            : $"{envelope.FromName} <{envelope.FromAddress}>";
        var bodyHtml = envelope.BodyHtml ?? string.Empty;
        var bodyText = envelope.BodyText is null
            ? string.Empty
            : $"<section aria-label=\"Plain text alternative\"><h2>Plain text alternative</h2><pre>{encoder.Encode(envelope.BodyText)}</pre></section>";

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>{{encoder.Encode(envelope.Subject)}}</title>
              <style>
                body { font-family: system-ui, sans-serif; margin: clamp(.75rem, 4vw, 2rem); color: #202124; }
                table { border-collapse: collapse; margin-bottom: 2rem; table-layout: fixed; width: 100%; }
                th, td { border: 1px solid #b9bec4; overflow-wrap: anywhere; padding: .5rem .75rem; text-align: left; vertical-align: top; }
                th { width: 8rem; }
                th { background: #f1f3f4; }
                pre { white-space: pre-wrap; }
              </style>
            </head>
            <body>
              <h1>Email preview</h1>
              <table>
                <tbody>
                  <tr><th scope="row">Outbox ID</th><td>{{envelope.OutboxId}}</td></tr>
                  <tr><th scope="row">Organization ID</th><td>{{envelope.OrganizationId}}</td></tr>
                  <tr><th scope="row">Business key</th><td>{{encoder.Encode(envelope.BusinessKey ?? string.Empty)}}</td></tr>
                  <tr><th scope="row">To</th><td>{{encoder.Encode(envelope.ToAddress)}}</td></tr>
                  <tr><th scope="row">From</th><td>{{encoder.Encode(from)}}</td></tr>
                  <tr><th scope="row">Subject</th><td>{{encoder.Encode(envelope.Subject)}}</td></tr>
                </tbody>
              </table>
              <main>{{bodyHtml}}{{bodyText}}</main>
            </body>
            </html>
            """;
    }
}
