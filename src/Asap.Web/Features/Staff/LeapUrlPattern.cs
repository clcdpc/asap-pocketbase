namespace Asap.Web.Features.Staff;

public static class LeapUrlPattern
{
    public const string BibPlaceholder = "{{bibid}}";
    public const string PatronPlaceholder = "{{patron-id}}";
    public const string PatronCompatibilityPlaceholder = "{{patronId}}";

    public static string? ValidateBibPattern(string? value) =>
        Validate(value, BibPlaceholder, "Leap BIB URL pattern", allowPatronCompatibilityPlaceholder: false);

    public static string? ValidatePatronPattern(string? value) =>
        Validate(value, PatronPlaceholder, "Leap patron URL pattern", allowPatronCompatibilityPlaceholder: true);

    public static string? BuildBibUrl(string? pattern, string? bibId)
    {
        if (string.IsNullOrWhiteSpace(bibId) || ValidateBibPattern(pattern) is not null)
        {
            return null;
        }

        return pattern!.Trim().Replace(
            BibPlaceholder,
            Uri.EscapeDataString(bibId.Trim()),
            StringComparison.Ordinal);
    }

    public static string? BuildPatronUrl(string? pattern, string? patronId)
    {
        if (string.IsNullOrWhiteSpace(patronId) || ValidatePatronPattern(pattern) is not null)
        {
            return null;
        }

        var encodedPatronId = Uri.EscapeDataString(patronId.Trim());
        return pattern!.Trim()
            .Replace(PatronPlaceholder, encodedPatronId, StringComparison.Ordinal)
            .Replace(PatronCompatibilityPlaceholder, encodedPatronId, StringComparison.Ordinal);
    }

    private static string? Validate(
        string? value,
        string requiredPlaceholder,
        string label,
        bool allowPatronCompatibilityPlaceholder)
    {
        var pattern = value?.Trim() ?? string.Empty;
        if (pattern.Length == 0)
        {
            return null;
        }

        if (!pattern.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !pattern.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return $"{label} must begin with http:// or https://.";
        }

        if (!pattern.Contains(requiredPlaceholder, StringComparison.Ordinal) &&
            !(allowPatronCompatibilityPlaceholder &&
              pattern.Contains(PatronCompatibilityPlaceholder, StringComparison.Ordinal)))
        {
            return allowPatronCompatibilityPlaceholder
                ? $"{label} must include {PatronPlaceholder} or {PatronCompatibilityPlaceholder}."
                : $"{label} must include {requiredPlaceholder}.";
        }

        var candidate = pattern
            .Replace(BibPlaceholder, "placeholder", StringComparison.Ordinal)
            .Replace(PatronPlaceholder, "placeholder", StringComparison.Ordinal)
            .Replace(PatronCompatibilityPlaceholder, "placeholder", StringComparison.Ordinal);
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            uri.Host.Contains("placeholder", StringComparison.Ordinal))
        {
            return $"{label} must be a valid HTTP(S) URL.";
        }

        return null;
    }
}
