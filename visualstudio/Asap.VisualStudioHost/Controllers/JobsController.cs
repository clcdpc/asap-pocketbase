using Microsoft.AspNetCore.Mvc;

namespace Asap.VisualStudioHost.Controllers;

[ApiController]
[Route("api/asap/jobs")]
public sealed class JobsController : ControllerBase
{
    [HttpPost("hold-check")]
    public IActionResult HoldCheck()
    {
        return Ok(new { success = true, message = "Hold check completed in local mode." });
    }

    [HttpPost("promoter-check")]
    public IActionResult PromoterCheck()
    {
        return Ok(new { success = true, message = "Promoter check completed in local mode." });
    }
}
