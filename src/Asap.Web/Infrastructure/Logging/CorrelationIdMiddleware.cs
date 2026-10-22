using System.Text.RegularExpressions;
using NLog;

namespace Asap.Web.Infrastructure.Logging;

public sealed partial class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers[HeaderName].FirstOrDefault();
        var correlationId = supplied is not null && SafeCorrelationIdRegex().IsMatch(supplied)
            ? supplied
            : Guid.NewGuid().ToString("N");

        context.TraceIdentifier = correlationId;
        context.Response.Headers.Append(HeaderName, correlationId);

        using (ScopeContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeCorrelationIdRegex();
}
