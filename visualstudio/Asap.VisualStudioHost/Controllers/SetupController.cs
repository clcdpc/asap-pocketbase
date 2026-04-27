using Asap.VisualStudioHost.Services;
using Microsoft.AspNetCore.Mvc;

namespace Asap.VisualStudioHost.Controllers;

[ApiController]
[Route("api/asap/setup")]
public sealed class SetupController : ControllerBase
{
    private readonly InMemoryAsapStore _store;

    public SetupController(InMemoryAsapStore store)
    {
        _store = store;
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        return Ok(new { setupRequired = !_store.SetupCompleted });
    }

    [HttpPost]
    public IActionResult Setup([FromBody] Dictionary<string, object>? payload)
    {
        _store.MarkSetupComplete();
        var username = payload is not null && payload.TryGetValue("username", out var value)
            ? value?.ToString() ?? "admin"
            : "admin";
        var user = _store.ResolveOrCreateStaff(username);

        return Ok(new
        {
            token = $"local-{Guid.NewGuid():N}",
            record = ToRecord(user),
            bootstrapMessage = "Visual Studio local mode setup complete."
        });
    }

    [HttpPost("test-polaris")]
    public IActionResult TestPolaris()
    {
        return Ok(new { success = true, message = "Polaris test is simulated in Visual Studio mode." });
    }

    private static object ToRecord(Models.StaffUser user)
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
