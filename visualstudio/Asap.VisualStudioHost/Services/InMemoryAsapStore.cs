using Asap.VisualStudioHost.Models;

namespace Asap.VisualStudioHost.Services;

public sealed class InMemoryAsapStore
{
    private readonly object _sync = new();

    public InMemoryAsapStore()
    {
        Config = new AsapConfig
        {
            UiText = new Dictionary<string, object>
            {
                ["pageTitle"] = "Material Suggestion",
                ["successTitle"] = "Suggestion Submitted",
                ["successMessage"] = "Your suggestion was submitted successfully.",
                ["alreadySubmittedMessage"] = "This title has already been submitted."
            }
        };

        AppSettings = new Dictionary<string, object>
        {
            ["id"] = "settings0000001",
            ["outstandingTimeoutEnabled"] = true,
            ["outstandingTimeoutDays"] = 30,
            ["allowedStaffUsers"] = string.Empty,
            ["polaris"] = new Dictionary<string, object> { ["autoPromote"] = true }
        };

        Organizations =
        [
            new Dictionary<string, object> { ["id"] = "system", ["name"] = "System", ["parentId"] = "" },
            new Dictionary<string, object> { ["id"] = "main", ["name"] = "Main Library", ["parentId"] = "system" }
        ];

        StaffUsers =
        [
            new StaffUser
            {
                Username = "admin",
                DisplayName = "Admin User",
                Role = "super_admin"
            }
        ];
    }

    public AsapConfig Config { get; }

    public Dictionary<string, object> AppSettings { get; }

    public List<TitleRequest> Requests { get; } = [];

    public List<StaffUser> StaffUsers { get; }

    public List<Dictionary<string, object>> Organizations { get; }

    public Dictionary<string, LibrarySettings> LibrarySettingsByOrg { get; } = new();

    public bool SetupCompleted { get; private set; }

    public bool VerifyStaffPassword(string? password)
    {
        return !string.IsNullOrWhiteSpace(password);
    }

    public StaffUser ResolveOrCreateStaff(string username)
    {
        lock (_sync)
        {
            var normalized = string.IsNullOrWhiteSpace(username) ? "admin" : username.Trim();
            var existing = StaffUsers.FirstOrDefault(x => x.Username.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return existing;
            }

            var created = new StaffUser
            {
                Username = normalized,
                DisplayName = normalized,
                Role = SetupCompleted ? "staff" : "super_admin"
            };
            StaffUsers.Add(created);
            return created;
        }
    }

    public void MarkSetupComplete()
    {
        SetupCompleted = true;
    }

    public LibrarySettings GetLibrarySettings(string orgId)
    {
        lock (_sync)
        {
            if (LibrarySettingsByOrg.TryGetValue(orgId, out var settings))
            {
                return settings;
            }

            return new LibrarySettings
            {
                IsOverride = false,
                UiText = new Dictionary<string, object>(),
                Emails = new Dictionary<string, object>(),
                Workflow = new Dictionary<string, object>()
            };
        }
    }
}
