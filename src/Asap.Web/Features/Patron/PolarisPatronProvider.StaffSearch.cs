using System.Text.Json;
using Asap.Web.Features.Staff;
using Clc.Polaris.Api;
using Clc.Polaris.Api.Models;

namespace Asap.Web.Features.Patron;

public sealed partial class PolarisPatronProvider
{
    public async Task<IReadOnlyList<PatronSnapshot>> SearchPatronsAsync(
        string query,
        CancellationToken cancellationToken)
    {
        try
        {
            var (client, settings) = await CreateClientAsync(cancellationToken);
            var request = PapiRestRequest.Get(
                $"/protected/v1/1033/100/{settings.OrganizationIdForRequests ?? 1}/{ProtectedToken.Placeholder}/search/patrons/boolean");
            request.QueryParameters.Add("q", "PATNF=" + QuoteSearch(CleanSearch(query)));
            request.QueryParameters.Add("sortby", "PATNF");
            request.QueryParameters.Add("patronsperpage", 10);
            request.QueryParameters.Add("page", 1);
            var response = await client.ExecutePapiAsync<PatronSearchResult>(request, cancellationToken);
            var data = response.Data;
            if (response.Response?.IsSuccessStatusCode != true || data is null ||
                !IsCoherentPatronSearchResponse(response.Response.Content, data))
            {
                throw new PolarisOperationalException(
                    "polaris_patron_search_failed",
                    "Polaris patron search was unavailable.");
            }

            if (data.PAPIErrorCode == -1)
            {
                return [];
            }

            var results = new List<PatronSnapshot>();
            foreach (var barcode in data.PatronSearchRows
                         .Select(row => Clean(row.Barcode))
                         .OfType<string>()
                         .Distinct(StringComparer.Ordinal)
                         .Take(10))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    results.Add(await LoadPatronAsync(client, barcode, string.Empty, cancellationToken));
                }
                catch (PolarisOperationalException exception) when (
                    exception.Code == "polaris_patron_not_found")
                {
                    // Search rows can become stale; a definitive missing patron is not selectable.
                    // Other provider/data-integrity failures must remain failures rather than
                    // becoming an ordinary no-match result.
                }
            }

            return results;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not PolarisOperationalException)
        {
            throw Operational("polaris_patron_search_failed", exception);
        }
    }

    private static bool IsCoherentPatronSearchResponse(
        string? content,
        PatronSearchResult data)
    {
        try
        {
            using var document = JsonDocument.Parse(content ?? string.Empty);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetUniqueProperty(root, "PAPIErrorCode", out var codeElement) ||
                codeElement.ValueKind != JsonValueKind.Number ||
                !codeElement.TryGetInt32(out var code) ||
                code != data.PAPIErrorCode || code < -1 ||
                !TryGetUniqueProperty(root, "PatronSearchRows", out var rows) ||
                rows.ValueKind != JsonValueKind.Array ||
                data.PatronSearchRows is null ||
                data.PatronSearchRows.Count != rows.GetArrayLength())
            {
                return false;
            }

            if (rows.GetArrayLength() == 0)
            {
                return code == 0 || code == -1 && HasNoSearchErrorMessage(root);
            }

            return code >= 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
