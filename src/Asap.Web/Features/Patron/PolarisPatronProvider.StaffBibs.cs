using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Asap.Web.Features.Staff;
using Clc.Polaris.Api;
using Clc.Polaris.Api.Models;
using Clc.Rest;

namespace Asap.Web.Features.Patron;

public sealed partial class PolarisPatronProvider
{
    private const int StaffBibSearchLimit = 10;

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
                request.QueryParameters.Add("bibsperpage", StaffBibSearchLimit);
                request.QueryParameters.Add("page", 1);
                request.QueryParameters.Add("notran", 1);
                StaffSearchAttempt inspected;
                try
                {
                    var response = await client.ExecutePapiAsync<BibSearchResult>(request, cancellationToken);
                    inspected = InspectStaffSearchResponse(response);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    failed = true;
                    continue;
                }

                if (inspected.Kind == StaffSearchAttemptKind.Operational)
                {
                    failed = true;
                    continue;
                }

                if (inspected.Kind == StaffSearchAttemptKind.DefinitiveEmpty)
                {
                    continue;
                }

                totalMatches = Math.Max(totalMatches, inspected.Data!.TotalRecordsFound);
                foreach (var candidate in inspected.StaffRows!)
                {
                    var row = candidate.Result;
                    var bibId = row.BibId;
                    if (candidate.MaterialType is "36" or "41" || seen.Contains(bibId))
                    {
                        continue;
                    }
                    if (search.AuthorFilter.Length > 0 &&
                        !(row.Author ?? string.Empty).Contains(search.AuthorFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    seen.Add(bibId);
                    results.Add(row);
                }

                if (results.Count >= StaffBibSearchLimit)
                {
                    break;
                }
            }
            if (results.Count == 0 && failed)
            {
                throw new PolarisOperationalException("polaris_bib_search_failed", "Polaris BIB search was unavailable.");
            }
            var rankedResults = results
                .Select((row, index) => new
                {
                    Row = row,
                    Order = index,
                    Score = ScoreStaffBib(row, query, title, author)
                })
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Order)
                .Take(StaffBibSearchLimit)
                .Select(item => item.Row)
                .ToArray();
            return new StaffBibSearchResult(rankedResults, Math.Max(totalMatches, results.Count));
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

    private static StaffSearchAttempt InspectStaffSearchResponse(
        IRestResponse<BibSearchResult> response)
    {
        if (response.Response?.IsSuccessStatusCode != true || response.Data is null)
        {
            return StaffSearchAttempt.Operational;
        }

        try
        {
            using var document = JsonDocument.Parse(response.Response.Content ?? string.Empty);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetUniqueProperty(root, "PAPIErrorCode", out var codeElement) ||
                codeElement.ValueKind != JsonValueKind.Number ||
                !codeElement.TryGetInt32(out var code) ||
                response.Data.PAPIErrorCode != code ||
                !TryGetUniqueProperty(root, "TotalRecordsFound", out var totalElement) ||
                totalElement.ValueKind != JsonValueKind.Number ||
                !totalElement.TryGetInt32(out var totalRecordsFound) ||
                totalRecordsFound < 0 || response.Data.TotalRecordsFound != totalRecordsFound ||
                !TryGetUniqueProperty(root, "BibSearchRows", out var rows) ||
                rows.ValueKind != JsonValueKind.Array ||
                (rows.GetArrayLength() == 0 && totalRecordsFound != 0) ||
                (rows.GetArrayLength() > 0 && totalRecordsFound < rows.GetArrayLength()) ||
                code < -1 ||
                (code > 0 && code != rows.GetArrayLength()) ||
                !TryReadStaffSearchRows(rows, response.Data, out var staffRows))
            {
                return StaffSearchAttempt.Operational;
            }

            if (code == -1)
            {
                // Preserve Polaris' established -1 empty-search response only for its coherent zero-row shape.
                return totalRecordsFound == 0 && rows.GetArrayLength() == 0 &&
                       HasNoSearchErrorMessage(root)
                    ? StaffSearchAttempt.DefinitiveEmpty(response.Data)
                    : StaffSearchAttempt.Operational;
            }

            return rows.GetArrayLength() == 0
                ? StaffSearchAttempt.DefinitiveEmpty(response.Data)
                : StaffSearchAttempt.WithRows(response.Data, staffRows);
        }
        catch (JsonException)
        {
            return StaffSearchAttempt.Operational;
        }
    }

    private static bool HasNoSearchErrorMessage(JsonElement root)
    {
        var found = false;
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, "ErrorMessage", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (found || property.Value.ValueKind != JsonValueKind.String ||
                !string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                return false;
            }

            found = true;
        }

        return true;
    }

    private static bool TryReadStaffSearchRows(
        JsonElement rows,
        BibSearchResult data,
        out IReadOnlyList<StaffBibSearchCandidate> candidates)
    {
        candidates = [];
        if (rows.ValueKind != JsonValueKind.Array ||
            data.BibSearchRows is null || data.BibSearchRows.Count != rows.GetArrayLength())
        {
            return false;
        }

        var result = new List<StaffBibSearchCandidate>(rows.GetArrayLength());
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !TryResolveStaffBibId(row, out var bibId))
            {
                return false;
            }

            result.Add(new StaffBibSearchCandidate(
                new StaffBibSearchRow(
                    bibId.ToString(CultureInfo.InvariantCulture),
                    SearchText(row, "DisplayTitle", "FullTitle", "Title", "SortTitle"),
                    SearchText(row, "Author", "PrimaryAuthor", "AuthorDisplay", "SortAuthor"),
                    SearchText(row, "PublicationDate", "PublicationYear", "PublishDate", "Date"),
                    SearchText(row, "MaterialTypeDescription", "MaterialType", "Format", "TypeOfMaterial"),
                    SearchText(row, "ISBN", "ISSN", "UPC", "Identifier")),
                SearchText(row, "PrimaryTypeOfMaterial", "TypeOfMaterial")));
        }

        candidates = result;
        return true;
    }

    private static bool TryResolveStaffBibId(JsonElement row, out int bibId)
    {
        bibId = default;
        var found = false;
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in row.EnumerateObject())
        {
            if (!string.Equals(property.Name, "ControlNumber", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(property.Name, "BibID", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(property.Name, "BibliographicRecordID", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!seenNames.Add(property.Name) || !TryReadPositiveInt32(property.Value, out var value) ||
                found && value != bibId)
            {
                return false;
            }

            bibId = value;
            found = true;
        }

        return found;
    }

    private static bool TryReadPositiveInt32(JsonElement value, out int valueAsInt32)
    {
        valueAsInt32 = default;
        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.TryGetInt32(out valueAsInt32) && valueAsInt32 > 0;
        }

        return value.ValueKind == JsonValueKind.String &&
               int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out valueAsInt32) &&
               valueAsInt32 > 0;
    }

    private sealed record StaffBibSearchCandidate(StaffBibSearchRow Result, string? MaterialType);

    public async Task<StaffBibHoldingsSummary> GetBibHoldingsAsync(
        int bibId, int organizationId, CancellationToken cancellationToken)
    {
        try
        {
            var (client, _) = await CreateClientAsync(cancellationToken);
            var response = await client.HoldingsGetAsync(bibId, cancellationToken);
            var data = response.Data;
            if (response.Response?.IsSuccessStatusCode != true || data is null)
            {
                throw new PolarisOperationalException("polaris_bib_holdings_failed", "Polaris holdings were unavailable.");
            }

            using var document = JsonDocument.Parse(response.Response.Content ?? string.Empty);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetUniqueProperty(root, "PAPIErrorCode", out var codeElement) ||
                codeElement.ValueKind != JsonValueKind.Number ||
                !codeElement.TryGetInt32(out var code) ||
                code != data.PAPIErrorCode || code < 0 ||
                !TryValidateHoldingsRows(root, data, out var noHoldings))
            {
                throw new PolarisOperationalException(
                    "polaris_bib_holdings_failed",
                    "Polaris returned an incomplete holdings response.");
            }
            if (noHoldings)
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
                if (!int.TryParse(row.LocationID, NumberStyles.Integer, CultureInfo.InvariantCulture, out var locationId) ||
                    locationId <= 0)
                {
                    throw new PolarisOperationalException(
                        "polaris_bib_holdings_failed",
                        "Polaris returned an invalid holdings location.");
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

    private static bool TryValidateHoldingsRows(
        JsonElement root,
        BibHoldingsGetResult data,
        out bool noHoldings)
    {
        noHoldings = false;
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetUniqueProperty(root, "BibHoldingsGetRows", out var rows) ||
            rows.ValueKind != JsonValueKind.Array ||
            data.BibHoldingsGetRows is null || data.BibHoldingsGetRows.Count != rows.GetArrayLength() ||
            data.PAPIErrorCode > 0 && data.PAPIErrorCode != rows.GetArrayLength())
        {
            return false;
        }

        if (rows.GetArrayLength() == 0)
        {
            noHoldings = true;
            return true;
        }

        var rowIndex = 0;
        foreach (var row in rows.EnumerateArray())
        {
            var dataRow = data.BibHoldingsGetRows[rowIndex++];
            if (row.ValueKind != JsonValueKind.Object || dataRow is null ||
                !TryGetUniqueProperty(row, "LocationID", out var locationId) ||
                !TryReadPositiveInt32(locationId, out var parsedLocationId) ||
                !int.TryParse(dataRow.LocationID, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dataLocationId) ||
                dataLocationId != parsedLocationId ||
                !TryGetUniqueProperty(row, "Holdable", out var rawHoldable) ||
                !TryReadHoldable(rawHoldable, out var parsedHoldable) ||
                !TryReadHoldable(dataRow.Holdable, out var dataHoldable) ||
                dataHoldable != parsedHoldable)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadHoldable(JsonElement value, out bool holdable)
    {
        holdable = default;
        if (value.ValueKind == JsonValueKind.True)
        {
            holdable = true;
            return true;
        }
        if (value.ValueKind == JsonValueKind.False)
        {
            return true;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric))
        {
            if (numeric is 0 or 1)
            {
                holdable = numeric == 1;
                return true;
            }

            return false;
        }

        return value.ValueKind == JsonValueKind.String && TryReadHoldable(value.GetString(), out holdable);
    }

    private static bool TryReadHoldable(string? value, out bool holdable)
    {
        holdable = default;
        switch (value?.Trim().ToLowerInvariant())
        {
            case "true":
            case "1":
            case "yes":
            case "y":
                holdable = true;
                return true;
            case "false":
            case "0":
            case "no":
            case "n":
                return true;
            default:
                return false;
        }
    }

    private enum StaffSearchAttemptKind
    {
        Operational,
        DefinitiveEmpty,
        WithRows
    }

    private sealed record StaffSearchAttempt(
        StaffSearchAttemptKind Kind,
        BibSearchResult? Data,
        IReadOnlyList<StaffBibSearchCandidate>? StaffRows)
    {
        public static StaffSearchAttempt Operational { get; } =
            new(StaffSearchAttemptKind.Operational, null, null);

        public static StaffSearchAttempt DefinitiveEmpty(BibSearchResult data) =>
            new(StaffSearchAttemptKind.DefinitiveEmpty, data, []);

        public static StaffSearchAttempt WithRows(
            BibSearchResult data,
            IReadOnlyList<StaffBibSearchCandidate> staffRows) =>
            new(StaffSearchAttemptKind.WithRows, data, staffRows);
    }

    private static string CleanSearch(string? value) => Regex.Replace(value ?? string.Empty, "\\s+", " ").Trim();

    private static double ScoreStaffBib(StaffBibSearchRow result, string query, string title, string author)
    {
        var score = 0d;
        var targetTitle = NormalizeLabel(title.Length > 0 ? title : query);
        var targetAuthor = NormalizeLabel(author);
        var targetIdentifier = NormalizeIdentifier(query);
        var rowTitle = NormalizeLabel(result.Title);
        var rowAuthor = NormalizeLabel(result.Author);
        var rowIdentifier = NormalizeIdentifier(result.Identifier ?? string.Empty);

        if (targetIdentifier.Length > 0 && rowIdentifier.Contains(targetIdentifier, StringComparison.Ordinal))
        {
            score += 200;
        }

        if (targetTitle.Length > 0 && rowTitle == targetTitle)
        {
            score += 100;
        }
        else if (targetTitle.Length > 0 && rowTitle.StartsWith(targetTitle, StringComparison.Ordinal))
        {
            score += 40;
        }

        if (targetAuthor.Length > 0 && rowAuthor.Contains(targetAuthor, StringComparison.Ordinal))
        {
            score += 30;
        }

        var publicationDigits = Regex.Replace(result.Publication ?? string.Empty, "\\D", string.Empty);
        if (publicationDigits.Length >= 4 &&
            int.TryParse(publicationDigits.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) &&
            year > 1900 && year <= DateTime.UtcNow.Year)
        {
            score += Math.Min(5d, (year - 1900) / 20d);
        }

        return score;
    }

    private static string NormalizeLabel(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.EndsWith(':'))
        {
            normalized = normalized[..^1];
        }
        return normalized.Trim().ToLowerInvariant();
    }

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
