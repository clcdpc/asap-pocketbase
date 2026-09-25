using System.Text.Json;
using System.Text.RegularExpressions;
using Asap.Web.Features.Staff;
using Clc.Polaris.Api;
using Clc.Polaris.Api.Models;

namespace Asap.Web.Features.Patron;

public sealed partial class PolarisPatronProvider
{
    public async Task<StaffBibSearchResult> SearchBibsAsync(
        string mode, string query, string title, string author, CancellationToken cancellationToken)
    {
        query = CleanSearch(query);
        title = CleanSearch(title);
        author = CleanSearch(author);
        var searches = mode switch
        {
            "identifier" => new[]
            {
                (Path: "keyword/ISBN", Query: NormalizeIdentifier(query), Sort: "PDTI", AuthorFilter: ""),
                (Path: "keyword/UPC", Query: NormalizeIdentifier(query), Sort: "PDTI", AuthorFilter: ""),
                (Path: "keyword/LCCN", Query: NormalizeIdentifier(query), Sort: "PDTI", AuthorFilter: "")
            },
            "title" => new[] { (Path: "keyword/TI", Query: title.Length > 0 ? title : query, Sort: "RELEVANCE", AuthorFilter: "") },
            "author" => new[] { (Path: "keyword/AU", Query: author.Length > 0 ? author : query, Sort: "AU", AuthorFilter: "") },
            "title_author" => new[]
            {
                (Path: "boolean", Query: $"TI={QuoteSearch(title)} AND AU={QuoteSearch(author)}", Sort: "PDTI", AuthorFilter: ""),
                (Path: "keyword/TI", Query: title, Sort: "RELEVANCE", AuthorFilter: author)
            },
            _ => throw new ArgumentException("Invalid Polaris search mode.", nameof(mode))
        };

        try
        {
            var (client, settings) = await CreateClientAsync(cancellationToken);
            var branch = settings.PickupOrganizationId is > 0
                ? settings.PickupOrganizationId.Value
                : settings.OrganizationIdForRequests is > 0 ? settings.OrganizationIdForRequests.Value : 1;
            var results = new List<StaffBibSearchRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var totalMatches = 0;
            var failed = false;
            foreach (var search in searches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = PapiRestRequest.Get($"/public/v1/1033/100/{branch}/search/bibs/{search.Path}");
                request.BlockStaffOverride = true;
                request.QueryParameters.Add("q", search.Query);
                request.QueryParameters.Add("sortby", search.Sort);
                request.QueryParameters.Add("bibsperpage", 10);
                request.QueryParameters.Add("page", 1);
                request.QueryParameters.Add("notran", 1);
                var response = await client.ExecutePapiAsync<BibSearchResult>(request, cancellationToken);
                var inspected = InspectSearchResponse(response);
                if (inspected.Failure.HasValue)
                {
                    failed = true;
                    continue;
                }
                if (inspected.Data!.PAPIErrorCode == -1)
                {
                    continue;
                }

                using var document = JsonDocument.Parse(response.Response!.Content!);
                if (!TryGetProperty(document.RootElement, "BibSearchRows", out var rows) ||
                    rows.ValueKind != JsonValueKind.Array)
                {
                    failed = true;
                    continue;
                }
                totalMatches = Math.Max(totalMatches, inspected.Data.TotalRecordsFound);
                foreach (var row in rows.EnumerateArray().Take(10))
                {
                    var bibId = SearchText(row, "ControlNumber", "BibID", "BibliographicRecordID");
                    var materialType = SearchText(row, "PrimaryTypeOfMaterial", "TypeOfMaterial");
                    if (!int.TryParse(bibId, out var numericBib) || numericBib <= 0 ||
                        materialType is "36" or "41" || seen.Contains(bibId))
                    {
                        continue;
                    }
                    var result = new StaffBibSearchRow(
                        bibId,
                        SearchText(row, "DisplayTitle", "FullTitle", "Title", "SortTitle"),
                        SearchText(row, "Author", "PrimaryAuthor", "AuthorDisplay", "SortAuthor"),
                        SearchText(row, "PublicationDate", "PublicationYear", "PublishDate", "Date"),
                        SearchText(row, "MaterialTypeDescription", "MaterialType", "Format", "TypeOfMaterial"),
                        SearchText(row, "ISBN", "ISSN", "UPC", "Identifier"));
                    if (search.AuthorFilter.Length > 0 &&
                        !(result.Author ?? string.Empty).Contains(search.AuthorFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    seen.Add(bibId);
                    results.Add(result);
                }
                if (results.Count >= 10)
                {
                    break;
                }
            }
            if (results.Count == 0 && failed)
            {
                throw new PolarisOperationalException("polaris_bib_search_failed", "Polaris BIB search was unavailable.");
            }
            return new StaffBibSearchResult(results.Take(10).ToArray(), Math.Max(totalMatches, results.Count));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PolarisOperationalException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw Operational("polaris_bib_search_failed", exception);
        }
    }

    public async Task<StaffBibHoldingsSummary> GetBibHoldingsAsync(
        int bibId, int organizationId, CancellationToken cancellationToken)
    {
        try
        {
            var (client, _) = await CreateClientAsync(cancellationToken);
            var response = await client.HoldingsGetAsync(bibId, cancellationToken);
            var data = response.Data;
            if (response.Response?.IsSuccessStatusCode != true || data is null ||
                !TryReadPapiErrorCode(response.Response.Content, out var code) ||
                code != data.PAPIErrorCode || code < -1)
            {
                throw new PolarisOperationalException("polaris_bib_holdings_failed", "Polaris holdings were unavailable.");
            }
            if (code == -1)
            {
                return new StaffBibHoldingsSummary(0, 0, 0, false, false);
            }
            var organizations = await LoadOrganizationsAsync(client, cancellationToken);
            var mine = 0;
            var other = 0;
            var holdable = false;
            var holdableHere = false;
            foreach (var row in data.BibHoldingsGetRows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!int.TryParse(row.LocationID, out var locationId) || locationId <= 0)
                {
                    continue;
                }
                var owner = ResolveHomeLibrary(organizations, locationId)?.OrganizationID ?? locationId;
                var canHold = row.Holdable?.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "y";
                if (owner == organizationId)
                {
                    mine++;
                    holdableHere |= canHold;
                }
                else
                {
                    other++;
                }
                holdable |= canHold;
            }
            return new StaffBibHoldingsSummary(mine, other, mine + other, holdable, holdableHere);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PolarisOperationalException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw Operational("polaris_bib_holdings_failed", exception);
        }
    }

    private static string CleanSearch(string? value) => Regex.Replace(value ?? string.Empty, "\\s+", " ").Trim();

    private static string NormalizeIdentifier(string value) =>
        Regex.Replace(value, "[\\s\\-_.:/]+", string.Empty).ToUpperInvariant();

    private static string QuoteSearch(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string? SearchText(JsonElement row, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(row, name, out var value) && Clean(value.ToString()) is { } text)
            {
                return text;
            }
        }
        return null;
    }
}
