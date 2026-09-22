namespace Asap.Web.Features.Staff;

public interface IPolarisReferenceProvider
{
    Task<PolarisConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken);

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
    int OrganizationCodeId,
    int? ParentOrganizationId);

public sealed record PolarisPatronCodeSnapshot(
    string Id,
    string Description);
