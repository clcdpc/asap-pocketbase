using Asap.VisualStudioHost.Models;
using Asap.VisualStudioHost.Services;
using Microsoft.AspNetCore.Mvc;

namespace Asap.VisualStudioHost.Controllers;

[ApiController]
[Route("api/asap/staff")]
public sealed class StaffController : ControllerBase
{
    private readonly InMemoryAsapStore _store;

    public StaffController(InMemoryAsapStore store)
    {
        _store = store;
    }

    [HttpPost("login")]
    public IActionResult Login([FromBody] Dictionary<string, string>? payload)
    {
        var username = payload?.GetValueOrDefault("username") ?? "admin";
        var password = payload?.GetValueOrDefault("password");

        if (!_store.VerifyStaffPassword(password))
        {
            return Unauthorized(new { message = "Invalid credentials." });
        }

        var user = _store.ResolveOrCreateStaff(username);
        _store.MarkSetupComplete();

        return Ok(new
        {
            token = $"staff-{Guid.NewGuid():N}",
            record = ToRecord(user),
            meta = new
            {
                bootstrapAdmin = string.Equals(user.Role, "super_admin", StringComparison.OrdinalIgnoreCase),
                bootstrapMessage = "Visual Studio local mode authenticated."
            }
        });
    }

    [HttpGet("title-requests")]
    public IActionResult TitleRequests()
    {
        return Ok(new { items = _store.Requests });
    }

    [HttpPost("title-requests/{id}/action")]
    public IActionResult RequestAction(string id, [FromBody] Dictionary<string, object>? payload)
    {
        var request = _store.Requests.FirstOrDefault(x => x.Id == id);
        if (request is null)
        {
            return NotFound(new { message = "Request not found." });
        }

        var nextStatus = payload?.GetValueOrDefault("status")?.ToString();
        if (!string.IsNullOrWhiteSpace(nextStatus))
        {
            request.Status = nextStatus;
        }

        request.LastChecked = DateTime.UtcNow;
        request.EditedBy = "Visual Studio User";

        return Ok(new { success = true, item = request });
    }

    [HttpPost("patron-lookup")]
    public IActionResult PatronLookup([FromBody] Dictionary<string, string>? payload)
    {
        var barcode = payload?.GetValueOrDefault("barcode") ?? "000000";
        return Ok(new
        {
            success = true,
            patron = new
            {
                name = "Local Patron",
                barcode,
                email = "patron@example.com",
                libraryOrgName = "Main Library"
            }
        });
    }

    [HttpPost("suggestions")]
    public IActionResult AddSuggestion([FromBody] Dictionary<string, string>? payload)
    {
        var request = new TitleRequest
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
        _store.Requests.Add(request);
        return Ok(new { success = true, item = request });
    }

    [HttpPost("bib-lookup")]
    public IActionResult BibLookup()
    {
        return Ok(new
        {
            success = true,
            items = new[]
            {
                new { bibId = "1000001", title = "Sample Catalog Record", author = "Local Author" }
            }
        });
    }

    [HttpPost("test-polaris")]
    public IActionResult TestPolaris()
    {
        return Ok(new { success = true, message = "Polaris test simulated in Visual Studio mode." });
    }

    [HttpPost("test-smtp")]
    public IActionResult TestSmtp()
    {
        return Ok(new { success = true, message = "SMTP test simulated in Visual Studio mode." });
    }

    [HttpGet("users")]
    public IActionResult Users()
    {
        return Ok(new { users = _store.StaffUsers });
    }

    [HttpPost("users/{id}/role")]
    public IActionResult UpdateRole(string id, [FromBody] Dictionary<string, string>? payload)
    {
        var user = _store.StaffUsers.FirstOrDefault(x => x.Id == id);
        if (user is null)
        {
            return NotFound(new { message = "User not found." });
        }

        var nextRole = payload?.GetValueOrDefault("role");
        if (!string.IsNullOrWhiteSpace(nextRole))
        {
            user.Role = nextRole;
        }

        return Ok(new { success = true, user });
    }

    [HttpPost("organizations/sync")]
    public IActionResult SyncOrganizations()
    {
        return Ok(new { success = true, count = _store.Organizations.Count });
    }

    [HttpGet("settings/library")]
    public IActionResult GetLibrarySettings([FromQuery] string? orgId)
    {
        var id = string.IsNullOrWhiteSpace(orgId) ? "system" : orgId;
        var settings = _store.GetLibrarySettings(id);
        return Ok(new { isOverride = settings.IsOverride, ui_text = settings.UiText, emails = settings.Emails, workflow = settings.Workflow });
    }

    [HttpPost("settings/library")]
    public IActionResult SaveLibrarySettings([FromBody] Dictionary<string, object>? payload)
    {
        var orgId = payload?.GetValueOrDefault("orgId")?.ToString() ?? "system";
        _store.LibrarySettingsByOrg[orgId] = new LibrarySettings
        {
            IsOverride = true,
            UiText = payload?.GetValueOrDefault("ui_text") as Dictionary<string, object> ?? new Dictionary<string, object>(),
            Emails = payload?.GetValueOrDefault("emails") as Dictionary<string, object> ?? new Dictionary<string, object>(),
            Workflow = payload?.GetValueOrDefault("workflow") as Dictionary<string, object> ?? new Dictionary<string, object>()
        };

        return Ok(new { success = true });
    }

    [HttpDelete("settings/library")]
    public IActionResult ResetLibrarySettings([FromQuery] string? orgId)
    {
        var id = string.IsNullOrWhiteSpace(orgId) ? "system" : orgId;
        _store.LibrarySettingsByOrg.Remove(id);
        return Ok(new { success = true });
    }

    private static object ToRecord(StaffUser user)
    {
        return new
        {
            id = user.Id,
            username = user.Username,
            displayName = user.DisplayName,
            role = user.Role,
            scope = user.Scope,
            collectionName = "staff_users",
            libraryOrgId = user.LibraryOrgId,
            libraryOrgName = user.LibraryOrgName,
            identityKey = user.IdentityKey
        };
    }
}
