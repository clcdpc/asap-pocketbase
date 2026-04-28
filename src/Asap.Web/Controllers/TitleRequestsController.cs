using Asap.Core.Models;
using Asap.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Asap.Web.Controllers;

[ApiController]
[Route("api/v1/title-requests")]
public sealed class TitleRequestsController(ITitleRequestService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TitleRequest>>> ListAsync(
        [FromQuery] string? status,
        [FromQuery] string? libraryOrgId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > 200 ? 50 : pageSize;

        var data = await service.GetTitleRequestsAsync(status, libraryOrgId, page, pageSize, cancellationToken);
        return Ok(data);
    }

    [HttpPost("{id}/reject")]
    public async Task<IActionResult> RejectAsync(string id, [FromBody] RejectTitleRequestBody body, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body.CloseReason))
        {
            return ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(body.CloseReason)] = ["closeReason is required."]
            });
        }

        await service.RejectAsync(new RejectTitleRequestCommand
        {
            RequestId = id,
            CloseReason = body.CloseReason,
            CloseMessage = body.CloseMessage,
            StaffUserId = body.StaffUserId,
        }, cancellationToken);

        return NoContent();
    }

    public sealed class RejectTitleRequestBody
    {
        public required string CloseReason { get; init; }
        public string? CloseMessage { get; init; }
        public required string StaffUserId { get; init; }
    }
}
