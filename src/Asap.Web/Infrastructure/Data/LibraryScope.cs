using System.Globalization;

namespace Asap.Web.Infrastructure.Data;

public enum LibraryScopeKind { All, System, Library }

// Parse transport values once. System settings and all-library views are distinct.
public readonly record struct LibraryScope
{
    public const int SystemOrganizationId = 1;
    public LibraryScopeKind Kind { get; }
    public int? OrganizationId { get; }

    private LibraryScope(LibraryScopeKind kind, int? organizationId) =>
        (Kind, OrganizationId) = (kind, organizationId);

    public static LibraryScope All => new(LibraryScopeKind.All, null);
    public static LibraryScope System => new(LibraryScopeKind.System, SystemOrganizationId);
    public static LibraryScope ForLibrary(int organizationId) => organizationId > SystemOrganizationId
        ? new(LibraryScopeKind.Library, organizationId)
        : throw new ArgumentOutOfRangeException(nameof(organizationId));

    public static bool TryParse(string? value, LibraryScope defaultScope, out LibraryScope scope)
    {
        var clean = value?.Trim();
        scope = defaultScope;
        if (string.IsNullOrEmpty(clean))
        {
            return true;
        }
        if (clean.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            scope = All;
            return true;
        }
        if (clean.Equals("system", StringComparison.OrdinalIgnoreCase) || clean == "1")
        {
            scope = System;
            return true;
        }
        if (!int.TryParse(clean, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            return false;
        }
        scope = id == SystemOrganizationId ? System : ForLibrary(id);
        return true;
    }

    public string ToTransportValue() => Kind switch
    {
        LibraryScopeKind.All => "all",
        LibraryScopeKind.System => "system",
        _ => OrganizationId!.Value.ToString(CultureInfo.InvariantCulture)
    };
}
