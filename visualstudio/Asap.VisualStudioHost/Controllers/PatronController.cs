using Asap.VisualStudioHost.Models;
using Asap.VisualStudioHost.Services;
using Microsoft.AspNetCore.Mvc;

namespace Asap.VisualStudioHost.Controllers;

[ApiController]
[Route("api/asap/patron")]
public sealed class PatronController : ControllerBase
{
    private readonly InMemoryAsapStore _store;

    public PatronController(InMemoryAsapStore store)
    {
        _store = store;
    }

    [HttpPost("login")]
    public IActionResult Login([FromBody] Dictionary<string, string>? payload)
    {
        var barcode = payload is not null && payload.TryGetValue("barcode", out var value)
            ? value
            : "000000";
        if (string.IsNullOrWhiteSpace(barcode))
        {
            return Unauthorized(new { message = "Barcode is required." });
        }

        return Ok(new
        {
            token = $"patron-{barcode}",
            ui_text = _store.Config.UiText,
            patron = new { barcode }
        });
    }

    [HttpPost("suggestions")]
    public IActionResult SubmitSuggestion([FromBody] Dictionary<string, string>? payload)
    {
        var suggestion = new TitleRequest
        {
            Barcode = payload?.GetValueOrDefault("barcode") ?? "000000",
            Title = payload?.GetValueOrDefault("title") ?? "Untitled",
            Author = payload?.GetValueOrDefault("author") ?? "Unknown",
            Identifier = payload?.GetValueOrDefault("identifier") ?? string.Empty,
            AgeGroup = payload?.GetValueOrDefault("agegroup") ?? string.Empty,
            Format = payload?.GetValueOrDefault("format") ?? "book",
            Publication = payload?.GetValueOrDefault("publication") ?? "Already published",
            Notes = payload?.GetValueOrDefault("notes") ?? string.Empty,
            Status = "suggestion"
        };

        _store.Requests.Add(suggestion);

        return Ok(new
        {
            success = true,
            id = suggestion.Id,
            message = "Suggestion submitted in local Visual Studio mode."
        });
    }
}
