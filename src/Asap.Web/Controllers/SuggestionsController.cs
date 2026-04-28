using Asap.Web.Models;
using Asap.Web.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace Asap.Web.Controllers;

[Route("suggestions")]
public sealed class SuggestionsController(ISuggestionRepository repository) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var suggestions = await repository.GetOpenAsync(cancellationToken);
        return View(suggestions);
    }

    [HttpGet("new")]
    public IActionResult Create() => View(new SuggestionCreateInput());

    [HttpPost("new")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(SuggestionCreateInput input, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(input);
        }

        var createdId = await repository.CreateAsync(input, cancellationToken);
        TempData["SuccessMessage"] = $"Suggestion #{createdId} was created.";
        return RedirectToAction(nameof(Index));
    }
}
