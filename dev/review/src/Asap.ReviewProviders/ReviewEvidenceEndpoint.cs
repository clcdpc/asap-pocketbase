using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Asap.ReviewProviders;

// Only the review HostingStartup loads this middleware. It exposes one fixed,
// read-only synthetic projection, never an arbitrary SQL or administration API.
internal sealed class ReviewEvidenceEndpoint : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, following) =>
        {
            if (context.Request.Path != "/_review/evidence")
            {
                await following(context);
                return;
            }
            context.Response.Headers.CacheControl = "no-store";
            if (!HttpMethods.IsGet(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                var expected = await File.ReadAllTextAsync("/review/evidence/token", timeout.Token);
                var supplied = context.Request.Headers["X-Asap-Review-Proof"].ToString();
                if (expected.Length < 32 || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(supplied)))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                var evidence = await AcceptanceEvidence.ReadAsync(timeout.Token);
                using var identity = JsonDocument.Parse(await File.ReadAllTextAsync("/review/identity.json", timeout.Token));
                await context.Response.WriteAsJsonAsync(new
                {
                    identity = identity.RootElement,
                    webHostname = Environment.MachineName,
                    project = Environment.GetEnvironmentVariable("REVIEW_PROJECT_NAME"),
                    authenticationMode = Environment.GetEnvironmentVariable("REVIEW_AUTH_MODE"),
                    evidence
                }, timeout.Token);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // SQL/configuration failures can contain secrets. Return no exception text.
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            }
        });
        next(app);
    };
}
