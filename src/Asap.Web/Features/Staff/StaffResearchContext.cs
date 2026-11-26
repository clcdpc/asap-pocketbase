using Asap.Web.Features.Patron;

namespace Asap.Web.Features.Staff;

public sealed record StaffSearchProvider(string Key, string Label, string UrlTemplate, int SortOrder)
{
    internal static StaffSearchProvider[] Enabled(IEnumerable<EffectiveExternalSearchProvider> providers) =>
        providers.Where(item => item.IsEnabled)
            .Select(item => new StaffSearchProvider(item.Key, item.Label, item.UrlTemplate, item.SortOrder))
            .ToArray();
}

// Staff-only read metadata. Patron IDs come from the pinned request identity or
// the current verified patron; legacy requests without that identity stay plain text.
public sealed record StaffResearchContext(string? LeapBibUrlPattern, string? LeapPatronUrlPattern, int? PatronId)
{
    public IReadOnlyList<StaffSearchProvider> ExternalSearchProviders { get; init; } = [];
}
