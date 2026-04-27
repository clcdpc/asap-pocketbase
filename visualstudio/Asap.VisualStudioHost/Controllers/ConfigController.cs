using Asap.VisualStudioHost.Services;
using Microsoft.AspNetCore.Mvc;

namespace Asap.VisualStudioHost.Controllers;

[ApiController]
[Route("api/asap")]
public sealed class ConfigController : ControllerBase
{
    private readonly InMemoryAsapStore _store;

    public ConfigController(InMemoryAsapStore store)
    {
        _store = store;
    }

    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        return Ok(new
        {
            logoUrl = _store.Config.LogoUrl,
            logoAlt = _store.Config.LogoAlt,
            publicationOptions = _store.Config.PublicationOptions,
            ui_text = _store.Config.UiText,
            closeReasons = new[] { "not_purchased", "already_own", "other" }
        });
    }
}
