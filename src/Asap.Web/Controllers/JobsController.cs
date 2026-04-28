using Asap.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Asap.Web.Controllers;

[ApiController]
[Route("api/v1/jobs")]
public sealed class JobsController(IOutstandingTimeoutProcessor processor) : ControllerBase
{
    [HttpPost("outstanding-timeout/run")]
    public async Task<IActionResult> RunOutstandingTimeout(CancellationToken cancellationToken)
    {
        var updated = await processor.ProcessAsync(cancellationToken);
        return Ok(new { updated });
    }
}
