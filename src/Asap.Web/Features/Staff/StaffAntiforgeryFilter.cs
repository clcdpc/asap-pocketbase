using Microsoft.AspNetCore.Antiforgery;

namespace Asap.Web.Features.Staff;

public sealed class StaffAntiforgeryFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.Json(
                new { code = "antiforgery_failed", message = "The request security token is missing or invalid.", operationPhase = "rejected" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        return await next(context);
    }
}
