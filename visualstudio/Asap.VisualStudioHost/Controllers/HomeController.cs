using Microsoft.AspNetCore.Mvc;

namespace Asap.VisualStudioHost.Controllers;

[ApiController]
public sealed class HomeController : ControllerBase
{
    [HttpGet("/")]
    public IActionResult Root()
    {
        return Redirect("/staff/");
    }
}
