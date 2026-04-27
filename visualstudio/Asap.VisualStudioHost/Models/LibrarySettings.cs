namespace Asap.VisualStudioHost.Models;

public sealed class LibrarySettings
{
    public bool IsOverride { get; set; }

    public Dictionary<string, object> UiText { get; set; } = new();

    public Dictionary<string, object> Emails { get; set; } = new();

    public Dictionary<string, object> Workflow { get; set; } = new();
}
