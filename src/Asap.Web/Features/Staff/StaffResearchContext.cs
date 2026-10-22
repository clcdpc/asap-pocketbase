namespace Asap.Web.Features.Staff;

// Staff-only read metadata. Patron IDs come from the pinned request identity or
// the current verified patron; legacy requests without that identity stay plain text.
public sealed record StaffResearchContext(string? LeapBibUrlPattern, string? LeapPatronUrlPattern, int? PatronId);
