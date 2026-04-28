using Microsoft.AspNetCore.Mvc;

namespace Asap.Web.Controllers;

public sealed class HomeController : Controller
{
    public IActionResult Index() => View();
}
