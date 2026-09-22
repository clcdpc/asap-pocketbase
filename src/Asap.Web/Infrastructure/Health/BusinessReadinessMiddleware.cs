namespace Asap.Web.Infrastructure.Health;

public sealed class BusinessReadinessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IReadinessService readiness)
    {
        if (context.Request.Path.StartsWithSegments("/api/asap"))
        {
            var result = await readiness.CheckAsync(context.RequestAborted);
            if (!result.IsReady)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(
                    new { message = "The service is temporarily unavailable." },
                    context.RequestAborted);
                return;
            }
        }

        await next(context);
    }
}
