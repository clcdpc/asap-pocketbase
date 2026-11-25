using System.Globalization;
using System.Text.Json;
using Asap.Web.Features.Staff;

namespace Asap.Web.Features.Patron;

public sealed partial class PolarisPatronProvider
{
    private sealed record MaterialReference(DateTimeOffset Expires, IReadOnlyDictionary<int, PolarisTom> Rows, bool HasSearchCodes);
    private readonly SemaphoreSlim materialReferenceGate = new(1, 1);
    private readonly Dictionary<(int OrganizationId, string Host), MaterialReference> materialReferences = [];

    private static PolarisOperationalException TomReferenceFailure() =>
        new("polaris_unavailable", "Polaris TOM search codes could not be verified.");

    public async Task<IReadOnlyList<PolarisTom>> GetPromotionTomsAsync(CancellationToken cancellationToken)
    {
        const int system = PolarisConfigurationValidation.SystemOrganizationId;
        var (client, _) = await CreateClientAsync(system, cancellationToken);
        return (await LoadTomCodesAsync(client, system, cancellationToken)).Values.OrderBy(row => row.Description).ToArray();
    }

    private async Task<IReadOnlyDictionary<int, PolarisTom>> LoadTomCodesAsync(DispatchAwarePapiClient client,
        int organizationId, CancellationToken cancellationToken)
    {
        var reference = await LoadMaterialDefinitionsAsync(client, organizationId, cancellationToken);
        if (!reference.HasSearchCodes)
        {
            throw TomReferenceFailure();
        }
        return reference.Rows;
    }

    private static int? ParseMaterialTypeId(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;

    private async Task<IReadOnlyList<StaffBibSearchRow>> PresentStaffMaterialsAsync(
        DispatchAwarePapiClient client, IReadOnlyList<StaffBibSearchRow> rows, int organizationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!rows.Any(row => row.MaterialTypeId.HasValue || IsNumericMaterialCode(row.Format)))
        {
            return rows;
        }

        var definitions = rows.Any(row => row.MaterialTypeId.HasValue)
            ? (await LoadMaterialDefinitionsAsync(client, organizationId, cancellationToken)).Rows
            : new Dictionary<int, PolarisTom>();
        return rows.Select(row =>
        {
            var label = definitions.GetValueOrDefault(row.MaterialTypeId ?? 0)?.Description ??
                (Clean(row.Format) is { } text && !IsNumericMaterialCode(text) ? text : "Unknown");
            return row with { Format = label };
        }).ToArray();
    }

    private static bool IsNumericMaterialCode(string? value) =>
        Clean(value) is { Length: > 0 } code && code.All(char.IsAsciiDigit);

    private async Task<MaterialReference> LoadMaterialDefinitionsAsync(
        DispatchAwarePapiClient client, int organizationId, CancellationToken cancellationToken)
    {
        await materialReferenceGate.WaitAsync(cancellationToken);
        try
        {
            var now = timeProvider.GetUtcNow();
            var key = (organizationId, client.Hostname);
            materialReferences.TryGetValue(key, out var cached);
            if (cached is not null && cached.Expires > now)
            {
                return cached;
            }
            try
            {
                var response = await client.CallAsync(
                    () => client.MARCTypeOfMaterialsGetAsync(organizationId, cancellationToken), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                using var document = JsonDocument.Parse(response.Response?.Content ?? string.Empty);
                var root = document.RootElement;
                if (response.Response?.IsSuccessStatusCode != true || response.Data is null ||
                    root.ValueKind != JsonValueKind.Object ||
                    !TryGetUniqueProperty(root, "PAPIErrorCode", out var code) ||
                    code.ValueKind != JsonValueKind.Number ||
                    !code.TryGetInt32(out var error) || error < 0 || error != response.Data.PAPIErrorCode ||
                    !TryGetUniqueProperty(root, "MARCTypeOfMaterialsRows", out var rawRows) ||
                    rawRows.ValueKind != JsonValueKind.Array || rawRows.GetArrayLength() == 0)
                {
                    throw new PolarisOperationalException("polaris_material_types_unavailable", "Material definitions were unavailable.");
                }
                var definitions = new Dictionary<int, PolarisTom>();
                foreach (var row in rawRows.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object || HasDuplicateProperties(row) ||
                        !TryGetUniqueProperty(row, "MARCTypeOfMaterialID", out var rawId) ||
                        !TryReadPositiveInt32(rawId, out var id) ||
                        !TryGetUniqueProperty(row, "Description", out var description) ||
                        description.ValueKind != JsonValueKind.String || Clean(description.GetString()) is not { } label ||
                        !definitions.TryAdd(id, new PolarisTom(id,
                            TryGetUniqueProperty(row, "SearchCode", out var searchCode) && searchCode.ValueKind == JsonValueKind.String
                                ? searchCode.GetString()!.ToLowerInvariant() : "", label)))
                    {
                        throw new PolarisOperationalException("polaris_material_types_unavailable", "Material definitions were incomplete.");
                    }
                }
                var validCodes = (error == 0 || error == definitions.Count) && definitions.Values.All(row => PolarisTom.IsValidCode(row.SearchCode)) &&
                    definitions.Values.Select(row => row.SearchCode).Distinct(StringComparer.Ordinal).Count() == definitions.Count;
                return materialReferences[key] = new MaterialReference(validCodes ? now.AddHours(24) : now.AddMinutes(1), definitions, validCodes);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is PolarisOperationalException || IsExpectedProviderFailure(exception))
            {
                logger.LogWarning(exception, "Polaris material descriptions were unavailable for library {OrganizationId}", organizationId);
                // Optional presentation data must not turn a trustworthy search into an empty result.
                // Briefly cache the fallback to bound repeated reference calls during an outage.
                var fallback = cached?.Rows ?? new Dictionary<int, PolarisTom>();
                return materialReferences[key] = new MaterialReference(now.AddMinutes(1), fallback, false);
            }
        }
        finally
        {
            materialReferenceGate.Release();
        }
    }

}
