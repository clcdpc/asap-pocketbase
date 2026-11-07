using System.Globalization;
using System.Text.RegularExpressions;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Staff;

public sealed record SubmissionSearchInput(string? Query, string? Scope = null, string? Group = null,
    int Page = 1, int PageSize = 12);
public sealed record SubmissionSearchItem(string Type, string Id, string Status, string Title, string? Author,
    string? Patron, string? Barcode, string? Identifier, int? BibId, int LibraryOrgId, string LibraryOrgName);
public sealed record SubmissionSearchGroup(string Key, int Count);
public sealed record SubmissionSearchResult(string Query, string Scope, IReadOnlyList<OrganizationChoice> Libraries,
    IReadOnlyList<SubmissionSearchGroup> Groups, int Total, int FilteredTotal, int Page, int PageSize,
    IReadOnlyList<SubmissionSearchItem> Items);

// Read-only POST keeps private terms out of URLs/request logs. Facets always describe the
// entire query + authorized scope; FilteredTotal and Items additionally apply Group.
public sealed partial class SubmissionSearchService(IDbContextFactory<AsapDbContext> contextFactory)
{
    public const int MaxQueryLength = 160;
    public const int MaxTokens = 12;
    private const string SearchCollation = "Latin1_General_100_CI_AS_SC";
    public static readonly string[] GroupKeys = ["title:suggestion", "title:outstanding_purchase", "title:pending_hold",
        "title:hold_placed", "title:closed", "copy:open", "copy:closed"];

    public static bool TryNormalize(SubmissionSearchInput input, out string query)
    {
        query = "";
        // Windows-collation LIKE ignores NUL; rejecting it preserves literal and idle semantics.
        if (input.Query?.Length > MaxQueryLength || input.Query?.Contains('\0') == true || input.Page is < 1 or > 100000 ||
            input.PageSize is < 1 or > 50 || input.Group is not (null or "") && !GroupKeys.Contains(input.Group))
        {
            return false;
        }
        query = Whitespace().Replace(input.Query ?? "", " ").Trim();
        return query.Length == 0 || query.Split(' ').Length <= MaxTokens;
    }

    public async Task<SubmissionSearchResult?> SearchAsync(CurrentStaff actor, LibraryScope requestedScope,
        SubmissionSearchInput input, string query, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var librariesQuery = context.Organizations.AsNoTracking().Where(item => item.Id > 1 &&
            item.OrganizationCodeId == OrganizationAuthority.LibraryOrganizationCodeId && item.IsActive);
        // Library actors receive only their own choice, as well as only their own records/counts.
        var libraries = await librariesQuery.Where(item => actor.Role == StaffRole.SuperAdmin || item.Id == actor.OrganizationId)
            .OrderBy(item => item.DisplayName).ThenBy(item => item.Id)
            .Select(item => new OrganizationChoice(item.Id, item.DisplayName)).ToListAsync(cancellationToken);
        var scope = actor.Role == StaffRole.SuperAdmin ? requestedScope : LibraryScope.ForLibrary(actor.OrganizationId);
        if (scope.Kind == LibraryScopeKind.System || scope.Kind == LibraryScopeKind.Library &&
            !libraries.Any(item => item.Id == scope.OrganizationId))
        {
            return null;
        }
        var groups = GroupKeys.Select(key => new SubmissionSearchGroup(key, 0)).ToList();
        if (query.Length == 0)
        {
            return new(query, scope.ToTransportValue(), libraries, groups, 0, 0, 1, input.PageSize, []);
        }

        var titles = from item in context.TitleRequests.AsNoTracking()
                     join library in librariesQuery on item.LibraryOrganizationId equals library.Id
                     where (scope.OrganizationId == null || item.LibraryOrganizationId == scope.OrganizationId) &&
                         (item.Status == "suggestion" || item.Status == "outstanding_purchase" || item.Status == "pending_hold" ||
                          item.Status == "hold_placed" || item.Status == "closed")
                     select new SearchRow
                     {
                         Type = "title", NativeId = item.Id, Id = item.Id.ToString(), Status = item.Status,
                         Title = item.Title, Author = item.Author ?? "", Barcode = item.Barcode,
                         Patron = ((item.NameFirst ?? "").Trim() + " " + (item.NameLast ?? "").Trim()).Trim(),
                         Identifier = item.Identifier ?? "", Bib = item.BibId == null ? "" : item.BibId.Value.ToString(),
                         BibId = item.BibId, LibraryOrgId = library.Id, LibraryOrgName = library.DisplayName
                     };
        var copies = from item in context.AdditionalCopyRequests.AsNoTracking()
                     join library in librariesQuery on item.LibraryOrganizationId equals library.Id
                     where (scope.OrganizationId == null || item.LibraryOrganizationId == scope.OrganizationId) &&
                         (item.Status == "open" || item.Status == "closed")
                     select new SearchRow
                     {
                         Type = "copy", NativeId = item.Id, Id = item.Id.ToString(), Status = item.Status,
                         Title = item.Title, Author = item.Author ?? "", Barcode = "", Patron = "",
                         Identifier = item.Identifier ?? "", Bib = item.BibId.ToString(), BibId = item.BibId,
                         LibraryOrgId = library.Id, LibraryOrgName = library.DisplayName
                     };
        var rows = titles.Concat(copies);
        // LIKE is deliberately escaped, including the escape character itself. Every token
        // must match this same row. No full DTOs, activities, patrons or providers are loaded.
        foreach (var token in query.Split(' '))
        {
            var pattern = "%" + EscapeLike(token) + "%";
            var hasNativeId = long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var nativeId) && nativeId > 0;
            rows = rows.Where(item =>
                EF.Functions.Like(EF.Functions.Collate(item.Title, SearchCollation), pattern, "~") ||
                EF.Functions.Like(EF.Functions.Collate(item.Author, SearchCollation), pattern, "~") ||
                EF.Functions.Like(EF.Functions.Collate(item.Patron, SearchCollation), pattern, "~") ||
                EF.Functions.Like(EF.Functions.Collate(item.Barcode, SearchCollation), pattern, "~") ||
                EF.Functions.Like(EF.Functions.Collate(item.Identifier, SearchCollation), pattern, "~") ||
                hasNativeId && item.NativeId == nativeId || EF.Functions.Like(item.Bib, pattern, "~"));
        }
        var counts = await rows.GroupBy(item => new { item.Type, item.Status })
            .Select(group => new { group.Key.Type, group.Key.Status, Count = group.Count() }).ToListAsync(cancellationToken);
        groups = GroupKeys.Select(key => new SubmissionSearchGroup(key,
            counts.SingleOrDefault(item => item.Type + ":" + item.Status == key)?.Count ?? 0)).ToList();
        var total = groups.Sum(item => item.Count);
        var filteredTotal = string.IsNullOrEmpty(input.Group) ? total : groups.Single(item => item.Key == input.Group).Count;
        if (!string.IsNullOrEmpty(input.Group))
        {
            var parts = input.Group.Split(':');
            rows = rows.Where(item => item.Type == parts[0] && item.Status == parts[1]);
        }
        var page = Math.Min(input.Page, Math.Max(1, (filteredTotal + input.PageSize - 1) / input.PageSize));
        var prefix = EscapeLike(query) + "%";
        var exactNativeId = long.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out var queryId) && queryId > 0;
        var items = await rows.OrderBy(item =>
                EF.Functions.Collate(item.Barcode, SearchCollation) == query || exactNativeId && item.NativeId == queryId ||
                EF.Functions.Collate(item.Identifier, SearchCollation) == query || item.Bib == query ? 0 :
                EF.Functions.Collate(item.Title, SearchCollation) == query || EF.Functions.Collate(item.Author, SearchCollation) == query ||
                EF.Functions.Collate(item.Patron, SearchCollation) == query ? 1 :
                EF.Functions.Like(EF.Functions.Collate(item.Title, SearchCollation), prefix, "~") ||
                EF.Functions.Like(EF.Functions.Collate(item.Author, SearchCollation), prefix, "~") ||
                EF.Functions.Like(EF.Functions.Collate(item.Patron, SearchCollation), prefix, "~") ? 2 : 3)
            .ThenBy(item => item.Type == "copy" ? 0 : 1).ThenBy(item => item.NativeId)
            .Skip((page - 1) * input.PageSize).Take(input.PageSize)
            .Select(item => new SubmissionSearchItem(item.Type, item.Id, item.Status, item.Title,
                item.Author == "" ? null : item.Author, item.Patron == "" ? null : item.Patron,
                item.Barcode == "" ? null : item.Barcode, item.Identifier == "" ? null : item.Identifier,
                item.BibId, item.LibraryOrgId, item.LibraryOrgName)).ToListAsync(cancellationToken);
        return new(query, scope.ToTransportValue(), libraries, groups, total, filteredTotal, page, input.PageSize, items);
    }

    private static string EscapeLike(string value) => value.Replace("~", "~~").Replace("%", "~%")
        .Replace("_", "~_").Replace("[", "~[");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private sealed class SearchRow
    {
        public required string Type { get; init; }
        public long NativeId { get; init; }
        public required string Id { get; init; }
        public required string Status { get; init; }
        public required string Title { get; init; }
        public required string Author { get; init; }
        public required string Patron { get; init; }
        public required string Barcode { get; init; }
        public required string Identifier { get; init; }
        public required string Bib { get; init; }
        public int? BibId { get; init; }
        public int LibraryOrgId { get; init; }
        public required string LibraryOrgName { get; init; }
    }
}

public static class SubmissionSearchEndpoints
{
    public static IEndpointRouteBuilder MapSubmissionSearchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/asap/staff/search", async (HttpContext context, SubmissionSearchInput input,
            SubmissionSearchService service, CancellationToken cancellationToken) =>
        {
            if (!SubmissionSearchService.TryNormalize(input, out var query) ||
                !LibraryScope.TryParse(input.Scope, LibraryScope.All, out var scope) || scope.Kind == LibraryScopeKind.System)
            {
                return Results.BadRequest(new { code = "invalid_search", message = "Use up to 160 characters, 12 words, and a valid library and result filter." });
            }
            var result = await service.SearchAsync((CurrentStaff)context.Items[StaffCurrentUserMiddleware.ItemKey]!,
                scope, input, query, cancellationToken);
            return result is null ? Results.BadRequest(new { code = "invalid_scope", message = "That search library is no longer available. Choose a current library." }) : Results.Json(result);
        }).RequireAuthorization().AddEndpointFilter<StaffAntiforgeryFilter>();
        return endpoints;
    }
}
