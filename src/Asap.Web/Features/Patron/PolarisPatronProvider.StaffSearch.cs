using System.Text.Json;
using System.Text.RegularExpressions;
using Asap.Web.Features.Staff;
using Clc.Polaris.Api;
using Clc.Polaris.Api.Models;

namespace Asap.Web.Features.Patron;

public sealed partial class PolarisPatronProvider
{
    public async Task<StaffBibHoldingsSummary> GetBibHoldingsAsync(
        int bibId, int organizationId, CancellationToken cancellationToken)
    {
        try
        {
            var (client, _) = await CreateClientAsync(cancellationToken);
            var response = await client.HoldingsGetAsync(bibId, cancellationToken);
            var data = response.Data;
            if (response.Response?.IsSuccessStatusCode != true || data is null ||
                !TryReadPapiErrorCode(response.Response.Content, out var errorCode) ||
                errorCode != data.PAPIErrorCode || errorCode < -1)
            {
                throw new PolarisOperationalException("polaris_bib_holdings_failed", "Polaris holdings were unavailable.");
            }
            if (errorCode == -1 || data.BibHoldingsGetRows.Count == 0)
            {
                return new(0, 0, 0, false, false);
            }
            var organizations = await LoadOrganizationsAsync(client, cancellationToken);
            var mine = 0;
            var others = 0;
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
                // The pinned workflow counts item rows, not the ItemsTotal display field.
                if (owner == organizationId)
                {
                    mine++;
                    holdableHere |= canHold;
                }
                else
                {
                    others++;
                }
                holdable |= canHold;
            }
            return new(mine, others, mine + others, holdable, holdableHere);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not PolarisOperationalException)
        {
            throw Operational("polaris_bib_holdings_failed", exception);
        }
    }

    public async Task<IReadOnlyList<PatronSnapshot>> SearchPatronsAsync(
        string query, CancellationToken cancellationToken)
    {
        try
        {
            var (client, settings) = await CreateClientAsync(cancellationToken);
            var request = PapiRestRequest.Get($"/protected/v1/1033/100/{settings.OrganizationIdForRequests ?? 1}/{ProtectedToken.Placeholder}/search/patrons/boolean");
            request.QueryParameters.Add("q", "PATNF=" + QuoteSearch(CleanSearch(query)));
            request.QueryParameters.Add("sortby", "PATNF");
            request.QueryParameters.Add("patronsperpage", 10);
            request.QueryParameters.Add("page", 1);
            var response = await client.ExecutePapiAsync<PatronSearchResult>(request, cancellationToken);
            var data = response.Data;
            if (response.Response?.IsSuccessStatusCode != true || data is null ||
                !TryReadPapiErrorCode(response.Response.Content, out var errorCode) || errorCode != data.PAPIErrorCode ||
                data.PAPIErrorCode < -1)
            {
                throw new PolarisOperationalException("polaris_patron_search_failed", "Polaris patron search was unavailable.");
            }
            if (data.PAPIErrorCode == -1)
            {
                return [];
            }
            var results = new List<PatronSnapshot>();
            foreach (var barcode in data.PatronSearchRows.Select(row => Clean(row.Barcode))
                         .OfType<string>().Distinct(StringComparer.Ordinal).Take(10))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    results.Add(await LoadPatronAsync(client, barcode, string.Empty, cancellationToken));
                }
                catch (PolarisOperationalException exception) when (
                    exception.Code is "polaris_patron_not_found" or "polaris_home_library_missing")
                {
                    // Stale search rows and patrons with unresolved scope cannot be offered to staff.
                }
            }
            return results;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not PolarisOperationalException)
        {
            throw Operational("polaris_patron_search_failed", exception);
        }
    }

    public async Task<StaffBibSearchResult> SearchBibsAsync(
        string mode, string query, string title, string author, CancellationToken cancellationToken)
    {
        query = CleanSearch(query);
        title = CleanSearch(Clean(title) ?? query);
        author = CleanSearch(author);
        try
        {
            var (client, settings) = await CreateClientAsync(cancellationToken);
            var branch = settings.PickupOrganizationId is > 0
                ? settings.PickupOrganizationId : settings.OrganizationIdForRequests;
            var requests = new List<(string Path, string Query, string Sort, string? FilterAuthor)>();
            switch (mode)
            {
                case "identifier":
                    var normalizedIdentifier = NormalizeSearchIdentifier(query);
                    var identifier = Regex.IsMatch(normalizedIdentifier, "^[A-Z0-9]+$") ? normalizedIdentifier : query;
                    foreach (var qualifier in new[] { "ISBN", "UPC", "LCCN" })
                    {
                        requests.Add(($"keyword/{qualifier}", identifier, "PDTI", null));
                    }
                    break;
                case "author":
                    requests.Add(("keyword/AU", Clean(author) ?? query, "AU", null));
                    break;
                case "title_author":
                    if (title.Length > 0 && author.Length > 0)
                    {
                        requests.Add(("boolean", $"TI={QuoteSearch(title)} AND AU={QuoteSearch(author)}", "PDTI", null));
                    }
                    requests.Add(("keyword/TI", title, "RELEVANCE", author));
                    break;
                case "title":
                    requests.Add(("keyword/TI", title, "RELEVANCE", null));
                    break;
                default:
                    throw new ArgumentException("Invalid Polaris search mode.", nameof(mode));
            }

            var results = new List<StaffBibSearchRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var total = 0;
            PolarisOperationalException? failure = null;
            foreach (var search in requests)
            {
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
                    failure = new PolarisOperationalException("polaris_bib_search_failed", "Polaris BIB search was unavailable.");
                    continue;
                }
                if (inspected.Data!.PAPIErrorCode == -1)
                {
                    continue;
                }
                using var document = JsonDocument.Parse(response.Response!.Content!);
                TryGetProperty(document.RootElement, "BibSearchRows", out var rows);
                total = Math.Max(total, Math.Max(inspected.Data.TotalRecordsFound, rows.GetArrayLength()));
                foreach (var row in rows.EnumerateArray().Take(10))
                {
                    var bibId = SearchValue(row, "ControlNumber", "BibID", "BibliographicRecordID", "RecordID");
                    var tom = SearchValue(row, "PrimaryTypeOfMaterial");
                    if (bibId.Length == 0 || seen.Contains(bibId) || tom is "36" or "41")
                    {
                        continue;
                    }
                    var result = new StaffBibSearchRow(bibId,
                        SearchValue(row, "DisplayTitle", "FullTitle", "Title", "SortTitle"),
                        SearchValue(row, "Author", "PrimaryAuthor", "AuthorDisplay", "SortAuthor"),
                        SearchValue(row, "PublicationDate", "PublicationYear", "PublishDate", "PublishedDate", "Date"),
                        BibFormat(row, tom),
                        SearchValue(row, "ISBN", "ISSN", "UPC", "Identifier"));
                    if (!string.IsNullOrEmpty(search.FilterAuthor) &&
                        !SearchLabel(result.Author).Contains(SearchLabel(search.FilterAuthor), StringComparison.Ordinal))
                    {
                        continue;
                    }
                    results.Add(result);
                    seen.Add(bibId);
                }
                if (results.Count >= 10)
                {
                    break;
                }
            }
            if (results.Count == 0 && failure is not null)
            {
                throw failure;
            }
            return new StaffBibSearchResult(results.OrderByDescending(row => ScoreBib(row, query, title, author)).Take(10).ToArray(), total);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not PolarisOperationalException)
        {
            throw Operational("polaris_bib_search_failed", exception);
        }
    }

    private static string CleanSearch(string value) => Regex.Replace(value, "\\s+", " ").Trim();
    private static string NormalizeSearchIdentifier(string value) => Regex.Replace(value, "[\\s\\-_.:/]+", string.Empty).ToUpperInvariant();
    private static string QuoteSearch(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    private static string SearchLabel(string? value) => (value ?? string.Empty).TrimEnd(':').Trim().ToLowerInvariant();

    private static string SearchValue(JsonElement row, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(row, name, out var value) && Clean(value.ToString()) is { } text)
            {
                return text;
            }
        }
        return string.Empty;
    }

    private static string BibFormat(JsonElement row, string tom)
    {
        var description = SearchValue(row, "MaterialTypeDescription", "MaterialType", "materialTypeDesc");
        if (description.Length > 0 && !description.All(char.IsDigit))
        {
            return description;
        }
        // The pinned UI's standard Polaris material labels; no new format taxonomy.
        return tom switch
        {
            "1" => "Book", "3" => "Periodical", "10" => "Sound Recording", "14" => "Musical Score",
            "15" => "Map", "19" => "Computer File", "33" => "DVD", "36" => "eBook", "37" => "Audio Book",
            "41" => "eAudiobook", "52" => "Audio Book on CD", "53" => "Large Print",
            _ => Clean(SearchValue(row, "Format", "TypeOfMaterial")) ?? Clean(description) ?? Clean(tom) ?? "Unknown"
        };
    }

    private static double ScoreBib(StaffBibSearchRow row, string query, string title, string author)
    {
        var score = 0d;
        var identifier = NormalizeSearchIdentifier(query);
        var rowIdentifier = NormalizeSearchIdentifier(row.Identifier ?? string.Empty);
        if (identifier.Length > 0 && rowIdentifier.Contains(identifier, StringComparison.Ordinal))
        {
            score += 200;
        }
        if (title.Length > 0)
        {
            score += SearchLabel(row.Title) == SearchLabel(title) ? 100 :
                SearchLabel(row.Title).StartsWith(SearchLabel(title), StringComparison.Ordinal) ? 40 : 0;
        }
        if (author.Length > 0 && SearchLabel(row.Author).Contains(SearchLabel(author), StringComparison.Ordinal))
        {
            score += 30;
        }
        var digits = Regex.Replace(row.Publication ?? string.Empty, "\\D", string.Empty);
        if (int.TryParse(digits[..Math.Min(4, digits.Length)], out var year) && year > 1900 && year <= DateTime.UtcNow.Year)
        {
            score += Math.Min(5, (year - 1900) / 20d);
        }
        return score;
    }
}
