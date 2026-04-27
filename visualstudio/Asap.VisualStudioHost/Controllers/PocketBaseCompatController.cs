using Asap.VisualStudioHost.Services;
using Microsoft.AspNetCore.Mvc;

namespace Asap.VisualStudioHost.Controllers;

[ApiController]
[Route("api/collections")]
public sealed class PocketBaseCompatController : ControllerBase
{
    private readonly InMemoryAsapStore _store;

    public PocketBaseCompatController(InMemoryAsapStore store)
    {
        _store = store;
    }

    [HttpGet("app_settings/records/{id}")]
    public IActionResult GetAppSettings(string id)
    {
        if (!string.Equals(id, "settings0000001", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { message = "Record not found." });
        }

        return Ok(_store.AppSettings);
    }

    [HttpPatch("app_settings/records/{id}")]
    [HttpPost("app_settings/records/{id}")]
    public IActionResult UpdateAppSettings(string id, [FromBody] Dictionary<string, object> payload)
    {
        if (!string.Equals(id, "settings0000001", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { message = "Record not found." });
        }

        foreach (var kvp in payload)
        {
            _store.AppSettings[kvp.Key] = kvp.Value;
        }

        return Ok(_store.AppSettings);
    }

    [HttpGet("polaris_organizations/records")]
    public IActionResult GetOrganizations()
    {
        return Ok(new { items = _store.Organizations, page = 1, perPage = _store.Organizations.Count, totalItems = _store.Organizations.Count, totalPages = 1 });
    }
}
