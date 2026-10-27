using System.Globalization;
using System.Text.Json;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Clc.Polaris.Api;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Patron;

public sealed partial class PolarisPatronProvider
{
    private sealed record MaterialDefinition(int Id, string Description, string? SearchCode);
    private sealed record MaterialReference(DateTimeOffset Expires, IReadOnlyDictionary<int, MaterialDefinition> Rows);
    private readonly SemaphoreSlim materialReferenceGate = new(1, 1);
    private readonly Dictionary<(int OrganizationId, string Host), MaterialReference> materialReferences = [];

    private static int? ParseMaterialTypeId(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;

    private async Task<IReadOnlyList<StaffBibSearchRow>> PresentStaffMaterialsAsync(
        DispatchAwarePapiClient client, IReadOnlyList<StaffBibSearchRow> rows, int organizationId, CancellationToken cancellationToken)
    {
        if (!rows.Any(row => row.MaterialTypeId.HasValue || IsNumericMaterialCode(row.Format)))
        {
            return rows;
        }

        var definitions = rows.Any(row => row.MaterialTypeId.HasValue)
            ? await LoadMaterialDefinitionsAsync(client, organizationId, cancellationToken)
            : new Dictionary<int, MaterialDefinition>();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        // This existing setting is system-only, including when the search belongs to a library.
        var iconPattern = await context.SystemSettings.AsNoTracking()
            .Where(item => item.OrganizationId == LibraryScope.SystemOrganizationId)
            .Select(item => item.MaterialTypeIconUrlPattern).SingleOrDefaultAsync(cancellationToken);
        return rows.Select(row =>
        {
            definitions.TryGetValue(row.MaterialTypeId ?? 0, out var definition);
            var label = definition?.Description ??
                (Clean(row.Format) is { } text && !IsNumericMaterialCode(text) ? text : "Unknown");
            return row with
            {
                Format = label,
                MaterialTypeSearchCode = definition?.SearchCode,
                FormatIconUrl = definition is null ? null : MaterialIconUrl(iconPattern, definition)
            };
        }).ToArray();
    }

    private static bool IsNumericMaterialCode(string? value) =>
        Clean(value) is { Length: > 0 } code && code.All(char.IsAsciiDigit);

    private async Task<IReadOnlyDictionary<int, MaterialDefinition>> LoadMaterialDefinitionsAsync(
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
                return cached.Rows;
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
                var definitions = new Dictionary<int, MaterialDefinition>();
                foreach (var row in rawRows.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object || HasDuplicateProperties(row) ||
                        !TryGetUniqueProperty(row, "MARCTypeOfMaterialID", out var rawId) ||
                        !TryReadPositiveInt32(rawId, out var id) ||
                        !TryGetUniqueProperty(row, "Description", out var description) ||
                        description.ValueKind != JsonValueKind.String || Clean(description.GetString()) is not { } label ||
                        !definitions.TryAdd(id, new MaterialDefinition(id, label,
                            TryGetUniqueProperty(row, "SearchCode", out var searchCode) && searchCode.ValueKind == JsonValueKind.String
                                ? Clean(searchCode.GetString()) : null)))
                    {
                        throw new PolarisOperationalException("polaris_material_types_unavailable", "Material definitions were incomplete.");
                    }
                }
                materialReferences[key] = new MaterialReference(now.AddHours(24), definitions);
                return definitions;
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
                var fallback = cached?.Rows ?? new Dictionary<int, MaterialDefinition>();
                materialReferences[key] = new MaterialReference(now.AddMinutes(1), fallback);
                return fallback;
            }
        }
        finally
        {
            materialReferenceGate.Release();
        }
    }

    private static string? MaterialIconUrl(string? pattern, MaterialDefinition definition)
    {
        var id = definition.Id.ToString(CultureInfo.InvariantCulture);
        var padded = definition.Id.ToString("D2", CultureInfo.InvariantCulture);
        var url = pattern?.Trim()
            .Replace("{MARCTypeOfMaterialID}", id, StringComparison.Ordinal)
            .Replace("{MARCTypeOfMaterialID2}", padded, StringComparison.Ordinal)
            .Replace("{id}", id, StringComparison.Ordinal)
            .Replace("{id2}", padded, StringComparison.Ordinal)
            .Replace("{SearchCode}", Uri.EscapeDataString(definition.SearchCode ?? string.Empty), StringComparison.Ordinal);
        if (string.IsNullOrEmpty(url) || url.Any(char.IsControl) || url.Contains('\\') ||
            url.Contains('{') || url.Contains('}'))
        {
            return null;
        }
        if (url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal) &&
            Uri.IsWellFormedUriString(url, UriKind.Relative))
        {
            return url;
        }
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Host.Length > 0
                ? uri.AbsoluteUri : null;
    }
}
