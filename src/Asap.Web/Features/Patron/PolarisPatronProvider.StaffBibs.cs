using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Asap.Web.Features.Staff;
using Clc.Polaris.Api;
using Clc.Polaris.Api.Models;
using Clc.Rest;

namespace Asap.Web.Features.Patron;

public sealed partial class PolarisPatronProvider
{
    public async Task<StaffBibSearchResult> SearchBibsAsync(
        string mode, string query, string title, string author, int organizationId, int page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(page);
        query = CleanSearch(query);
        title = CleanSearch(title);
        author = CleanSearch(author);
        var (type, qualifier, term, sort) = mode switch
        {
            "identifier" => (BibSearchTypes.boolean, SearchQualifiers.KW, IdentifierSearch(query), SearchSortOptions.PDTI),
            "title_author" => (BibSearchTypes.boolean, SearchQualifiers.KW,
                PairedSearch(title, author), SearchSortOptions.PDTI),
            "title" => (BibSearchTypes.keyword, SearchQualifiers.TI, title.Length > 0 ? title : query, SearchSortOptions.RELEVANCE),
            "author" => (BibSearchTypes.keyword, SearchQualifiers.AU, author.Length > 0 ? author : query, SearchSortOptions.AU),
            _ => throw new ArgumentException("Invalid Polaris search mode.", nameof(mode))
        };

        try
        {
            var (client, _) = await CreateMemberClientAsync(organizationId, cancellationToken);
            var options = new BibSearchOptions
            {
                Branch = organizationId, SearchType = type, Qualifier = qualifier, Term = term, SortOption = sort,
                Page = page, PageSize = StaffBibSearchResult.PageSize, NoTransaction = true
            };
            var response = await client.CallAsync(() => client.BibSearchAsync(options, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var search = InspectStaffSearchResponse(response, page);
            var visible = search.Results.Where(row => row.MaterialTypeId is not (36 or 41))
                .DistinctBy(row => row.BibId).ToArray();
            var presented = await PresentStaffMaterialsAsync(client, visible, organizationId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return search with { Results = presented };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PolarisOperationalException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedProviderFailure(exception))
        {
            throw Operational("polaris_bib_search_failed", exception);
        }
    }

    private static StaffBibSearchResult InspectStaffSearchResponse(IRestResponse<BibSearchResult> response, int page)
    {
        if (response.Response?.IsSuccessStatusCode != true || response.Data is null)
        {
            throw new PolarisOperationalException("polaris_bib_search_failed", "Polaris BIB search was unavailable.");
        }

        using var document = JsonDocument.Parse(response.Response.Content ?? string.Empty);
        var root = document.RootElement;
        var offset = (page - 1L) * StaffBibSearchResult.PageSize;
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetUniqueProperty(root, "PAPIErrorCode", out var codeElement) ||
            codeElement.ValueKind != JsonValueKind.Number || !codeElement.TryGetInt32(out var code) ||
            response.Data.PAPIErrorCode != code ||
            !TryGetUniqueProperty(root, "TotalRecordsFound", out var totalElement) ||
            totalElement.ValueKind != JsonValueKind.Number || !totalElement.TryGetInt32(out var total) ||
            total < 0 || response.Data.TotalRecordsFound != total ||
            !TryGetUniqueProperty(root, "BibSearchRows", out var rows) || rows.ValueKind != JsonValueKind.Array ||
            rows.GetArrayLength() > StaffBibSearchResult.PageSize ||
            (rows.GetArrayLength() == 0 ? offset < total : offset + rows.GetArrayLength() > total) ||
            code < -1 || (code == -1 && (total != 0 || !HasNoSearchErrorMessage(root))) ||
            (code > 0 && code != rows.GetArrayLength()) ||
            !TryReadStaffSearchRows(rows, response.Data, out var staffRows))
        {
            throw new PolarisOperationalException("polaris_bib_search_failed", "Polaris returned an inconsistent BIB search page.");
        }
        return new StaffBibSearchResult(staffRows, total);
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
        out IReadOnlyList<StaffBibSearchRow> candidates)
    {
        candidates = [];
        if (rows.ValueKind != JsonValueKind.Array ||
            data.BibSearchRows is null || data.BibSearchRows.Count != rows.GetArrayLength())
        {
            return false;
        }

        var result = new List<StaffBibSearchRow>(rows.GetArrayLength());
        var index = 0;
        foreach (var row in rows.EnumerateArray())
        {
            var model = data.BibSearchRows[index++];
            if (row.ValueKind != JsonValueKind.Object || model is null ||
                !TryGetUniqueProperty(row, "ControlNumber", out var control) ||
                !TryReadPositiveInt32(control, out var bibId) || bibId != model.ControlNumber ||
                HasUndocumentedBibIdentityAlias(row) || HasDuplicateProperties(row))
            {
                return false;
            }

            result.Add(new StaffBibSearchRow(bibId, Clean(model.Title), Clean(model.Author), Clean(model.PublicationDate),
                Clean(model.TypeOfMaterial), Clean(model.ISBN) ?? Clean(model.UPC))
            {
                Publisher = Clean(model.Publisher),
                MaterialTypeId = model.PrimaryTypeOfMaterial ?? ParseMaterialTypeId(model.TypeOfMaterial)
            });
        }

        candidates = result;
        return true;
    }

    private static bool HasDuplicateProperties(JsonElement row)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return row.EnumerateObject().Any(property => !names.Add(property.Name));
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

    public async Task<StaffBibHoldingsSummary> GetBibHoldingsAsync(
        int bibId, int organizationId, CancellationToken cancellationToken)
    {
        try
        {
            var (client, _) = await CreateMemberClientAsync(organizationId, cancellationToken);
            var response = await client.CallAsync(() => client.HoldingsGetAsync(bibId, cancellationToken), cancellationToken);
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

                var owner = ResolveHomeLibrary(organizations, locationId)?.Id
                    ?? throw new PolarisOperationalException("polaris_bib_holdings_failed", "The holdings owner could not be resolved.");
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
        catch (Exception exception) when (IsExpectedProviderFailure(exception))
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

    private static string CleanSearch(string? value) => Regex.Replace(value ?? string.Empty, "\\s+", " ").Trim();

    internal const string PairedSearchNumberSignError = "Remove # from the title or author to search for a phrase.";

    internal static bool HasPairedSearchNumberSign(string title, string author) =>
        title.Contains('#') || author.Contains('#');

    private static string PairedSearch(string title, string author)
    {
        if (HasPairedSearchNumberSign(title, author))
        {
            throw new ArgumentException(PairedSearchNumberSignError);
        }

        var condition = $"AU={QuotePairedPhrase(author)}";
        if (AlternateAuthor(author) is { } alternate)
        {
            condition = $"({condition} OR AU={QuotePairedPhrase(alternate)})";
        }
        return $"TI={QuotePairedPhrase(title)} AND {condition}";
    }

    private static string QuotePairedPhrase(string value) => QuoteSearch(value)
        .Replace("*", "\\*", StringComparison.Ordinal)
        .Replace("?", "\\?", StringComparison.Ordinal);

    private static string? AlternateAuthor(string author)
    {
        var parts = author.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length > 2 || parts.Any(part => part.Length == 0))
        {
            return null;
        }
        // Reorder only name words and initials. Leave dates, qualifiers and other punctuation in the original phrase.
        var words = parts.SelectMany(part => part.Split(' ')).ToArray();
        if (!words.All(IsNameWord) || words.Length < 2)
        {
            return null;
        }
        if (parts.Length == 2)
        {
            return Swap(parts[0], parts[1], " ");
        }
        return words[1..^1].All(IsInitial) ? Swap(string.Join(' ', words[..^1]), words[^1], ", ") : null;

        static string? Swap(string first, string second, string separator) =>
            first.Equals(second, StringComparison.OrdinalIgnoreCase) ? null : second + separator + first;

        static bool IsInitial(string word) => Rune.TryGetRuneAt(word, 0, out var rune) &&
            Rune.IsLetter(rune) && word[rune.Utf16SequenceLength..] is "" or ".";

        static bool IsNameWord(string word) => IsInitial(word) || word.EnumerateRunes().Any(Rune.IsLetter) &&
            word.EnumerateRunes().All(r => Rune.IsLetter(r) || r.Value is '\'' or '’' or '-' ||
                Rune.GetUnicodeCategory(r) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark);
    }

    private static string IdentifierSearch(string text)
    {
        // Build literals from identifier characters only. No user quote, wildcard or escape reaches CCL.
        var compact = Regex.Replace(text, @"[\s\-_.:/]+", "").ToUpperInvariant();
        var identifier = string.Concat(compact.EnumerateRunes()
            .Select(rune => Rune.IsLetterOrDigit(rune) ? rune.ToString() : " ")).Trim();
        var lccn = Regex.Replace(text.Split('/')[0], @"\s+", "").ToLowerInvariant();
        var parts = Regex.Match(lccn, @"^([a-z]{0,3}(?:[0-9]{2}|[0-9]{4}))-([0-9]{1,6})$");
        if (parts.Success)
        {
            lccn = parts.Groups[1].Value + parts.Groups[2].Value.PadLeft(6, '0');
        }
        else if (!Regex.IsMatch(lccn, @"^[a-z]{0,3}[0-9]{8,10}$"))
        {
            lccn = identifier;
        }
        return $"ISBN=\"{identifier}\" OR UPC=\"{identifier}\" OR LCCN=\"{lccn}\"";
    }

    private static string QuoteSearch(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
