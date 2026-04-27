namespace Asap.VisualStudioHost.Models;

public sealed class AsapConfig
{
    public string LogoUrl { get; set; } = "/jpl.png";

    public string LogoAlt { get; set; } = "Library logo";

    public List<string> PublicationOptions { get; set; } =
    [
        "Already published",
        "Coming soon",
        "Published a while back"
    ];

    public Dictionary<string, object> UiText { get; set; } = new();
}
