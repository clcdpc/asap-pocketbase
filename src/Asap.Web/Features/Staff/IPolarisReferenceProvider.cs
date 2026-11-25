using Asap.Web.Features.Patron;

namespace Asap.Web.Features.Staff;

public interface IPolarisReferenceProvider
{
    Task<PolarisConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PolarisTom>> GetPromotionTomsAsync(CancellationToken cancellationToken) =>
        throw new PolarisOperationalException("polaris_unavailable", "Polaris TOM definitions are unavailable.");

    Task<IReadOnlyList<PolarisOrganizationSnapshot>> GetOrganizationsAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PolarisPatronCodeSnapshot>> GetPatronCodesAsync(
        CancellationToken cancellationToken);
}

public sealed record PolarisConnectionTestResult(
    bool IsConnected,
    int OrganizationCount,
    string? SafeErrorCode = null);

public sealed record PolarisOrganizationSnapshot(
    int Id,
    string DisplayName,
    string? Abbreviation,
    int? OrganizationCodeId,
    int? ParentOrganizationId);

public sealed record PolarisTom(int Id, string SearchCode, string Description)
{
    public static bool IsValidCode(string? code) => code is { Length: > 0 and <= 32 } &&
        char.IsAsciiLetter(code[0]) && code.All(char.IsAsciiLetterOrDigit);
}

public sealed record PolarisPatronCodeSnapshot(
    int Id,
    string Description);
