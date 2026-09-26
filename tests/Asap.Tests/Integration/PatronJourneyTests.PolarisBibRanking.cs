using System.Net;
using System.Text.Json;
using Asap.Web.Features.Patron;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task PolarisIdentifierSearchStopsAfterFirstAttemptFillsLimit()
    {
        var rows = Enumerable.Range(1, 10)
            .Select(index => SearchRow(9000 + index, $"Identifier candidate {index}", publication: "2020", identifierName: "ISBN", identifier: "different"))
            .ToArray();
        var handler = new SequenceResponseHandler(
            (HttpStatusCode.OK, SearchResponse(rows)),
            (HttpStatusCode.OK, SearchResponse(SearchRow(9201, "UPC fallback", identifierName: "UPC", identifier: "9780000000001"))),
            (HttpStatusCode.OK, SearchResponse()));
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.SearchBibsAsync("identifier", "978-0000000001", "", "", CancellationToken.None);

        Assert.AreEqual(10, result.Results.Count);
        Assert.AreEqual(1, handler.RequestCount);
        Assert.AreEqual("/search/bibs/keyword/ISBN", SearchPath(handler.RequestUris[0]));
    }

    [TestMethod]
    public async Task PolarisIdentifierSearchRanksStrongLaterAttemptBeforeWeakerEarlierAttempt()
    {
        var handler = new SequenceResponseHandler(
            (HttpStatusCode.OK,
             """{"PAPIErrorCode":1,"TotalRecordsFound":1,"BibSearchRows":[{"ControlNumber":9101,"Title":"Other edition","PublicationDate":"2026","ISBN":"9780000000002"}]}"""),
            (HttpStatusCode.OK,
             """{"PAPIErrorCode":1,"TotalRecordsFound":1,"BibSearchRows":[{"ControlNumber":9102,"Title":"Exact identifier","PublicationDate":"1901","UPC":"978-0000000001"}]}"""),
            (HttpStatusCode.OK, """{"PAPIErrorCode":0,"TotalRecordsFound":0,"BibSearchRows":[]}"""));
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.SearchBibsAsync("identifier", "978-0000000001", "", "", CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "9102", "9101" }, result.Results.Select(row => row.BibId).ToArray());
        Assert.AreEqual(3, handler.RequestCount, "Fallback attempts run while fewer than ten candidates have been accepted.");
        CollectionAssert.AreEqual(
            new[] { "/search/bibs/keyword/ISBN", "/search/bibs/keyword/UPC", "/search/bibs/keyword/LCCN" },
            handler.RequestUris.Select(SearchPath).ToArray());
    }

    [TestMethod]
    public async Task PolarisIdentifierSearchFilteringKeepsFallbackAliveUntilTenCandidatesAreAccepted()
    {
        var firstRows = Enumerable.Range(1, 8)
            .Select(index => SearchRow(9300 + index, $"First attempt {index}", publication: "2020", identifierName: "ISBN", identifier: "different"))
            .ToList();
        firstRows.Add(SearchRow(9390, "Electronic item", materialType: "36", identifierName: "ISBN", identifier: "9780000000001"));
        firstRows.Add(SearchRow(9301, "Duplicate candidate with exact identifier", publication: "2026", identifierName: "ISBN", identifier: "9780000000001"));
        var secondRows = new[]
        {
            SearchRow(9401, "UPC candidate one", identifierName: "UPC", identifier: "different"),
            SearchRow(9402, "UPC candidate two", identifierName: "UPC", identifier: "different")
        };
        var handler = new SequenceResponseHandler(
            (HttpStatusCode.OK, SearchResponse(firstRows.ToArray())),
            (HttpStatusCode.OK, SearchResponse(secondRows)),
            (HttpStatusCode.OK, SearchResponse()));
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.SearchBibsAsync("identifier", "978-0000000001", "", "", CancellationToken.None);

        Assert.AreEqual(10, result.Results.Count);
        Assert.AreEqual(2, handler.RequestCount);
        Assert.AreEqual("/search/bibs/keyword/UPC", SearchPath(handler.RequestUris[1]));
        CollectionAssert.Contains(result.Results.Select(row => row.BibId).ToArray(), "9402");
    }

    [TestMethod]
    public async Task PolarisTitleAuthorSearchStopsAfterBooleanAttemptFillsLimit()
    {
        var rows = Enumerable.Range(1, 10)
            .Select(index => SearchRow(9500 + index, $"A title candidate {index}", author: "An author", publication: "2020"))
            .ToArray();
        var handler = new SequenceResponseHandler(
            (HttpStatusCode.OK, SearchResponse(rows)),
            (HttpStatusCode.OK, SearchResponse(SearchRow(9601, "A title fallback", author: "An author"))));
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.SearchBibsAsync("title_author", "", "A title", "An author", CancellationToken.None);

        Assert.AreEqual(10, result.Results.Count);
        Assert.AreEqual(1, handler.RequestCount);
        Assert.AreEqual("/search/bibs/boolean", SearchPath(handler.RequestUris[0]));
    }

    [TestMethod]
    public async Task PolarisTitleAuthorSearchRanksFallbackCandidatesWhenBooleanAttemptIsBelowLimit()
    {
        var handler = new SequenceResponseHandler(
            (HttpStatusCode.OK, SearchResponse(SearchRow(9201, "A title: second edition", author: "An author", publication: "2026"))),
            (HttpStatusCode.OK, SearchResponse(SearchRow(9202, "A title", author: "An author", publication: "1901"))));
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.SearchBibsAsync("title_author", "", "A title", "An author", CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "9202", "9201" }, result.Results.Select(row => row.BibId).ToArray());
        Assert.AreEqual(2, handler.RequestCount);
        Assert.AreEqual("/search/bibs/keyword/TI", SearchPath(handler.RequestUris[1]));
    }

    [TestMethod]
    public async Task PolarisBibSearchKeepsEqualScoreResultsInAcceptedOrder()
    {
        var handler = new SequenceResponseHandler(
            (HttpStatusCode.OK,
             """{"PAPIErrorCode":1,"TotalRecordsFound":1,"BibSearchRows":[{"ControlNumber":9301,"Title":"Record one","PublicationDate":"2020","ISBN":"9780000000001"}]}"""),
            (HttpStatusCode.OK,
             """{"PAPIErrorCode":1,"TotalRecordsFound":1,"BibSearchRows":[{"ControlNumber":9302,"Title":"Record two","PublicationDate":"2020","UPC":"9780000000001"}]}"""),
            (HttpStatusCode.OK, """{"PAPIErrorCode":0,"TotalRecordsFound":0,"BibSearchRows":[]}"""));
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.SearchBibsAsync("identifier", "9780000000001", "", "", CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "9301", "9302" }, result.Results.Select(row => row.BibId).ToArray());
    }

    [TestMethod]
    public async Task PolarisBibSearchFiltersMaterialTypesAndDeduplicatesBeforeRanking()
    {
        var handler = new SequenceResponseHandler(
            (HttpStatusCode.OK,
             """{"PAPIErrorCode":3,"TotalRecordsFound":4,"BibSearchRows":[{"ControlNumber":9500,"PrimaryTypeOfMaterial":"36","ISBN":"9780000000001"},{"ControlNumber":9501,"Title":"First accepted title","PublicationDate":"2020","ISBN":"different"},{"ControlNumber":9501,"Title":"Duplicate with exact identifier","PublicationDate":"2026","ISBN":"9780000000001"}]}"""),
            (HttpStatusCode.OK,
             """{"PAPIErrorCode":2,"TotalRecordsFound":2,"BibSearchRows":[{"ControlNumber":9501,"Title":"Later duplicate","PublicationDate":"2026","ISBN":"9780000000001"},{"ControlNumber":9502,"Title":"Second accepted title","PublicationDate":"2020","ISBN":"different"}]}"""),
            (HttpStatusCode.OK, """{"PAPIErrorCode":0,"TotalRecordsFound":0,"BibSearchRows":[]}"""));
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.SearchBibsAsync("identifier", "9780000000001", "", "", CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "9501", "9502" }, result.Results.Select(row => row.BibId).ToArray());
        Assert.AreEqual("First accepted title", result.Results[0].Title,
            "A later duplicate must not replace the earliest accepted candidate before ranking.");
        Assert.AreEqual(4, result.TotalMatches);
    }

    private static string SearchResponse(params Dictionary<string, object?>[] rows) =>
        JsonSerializer.Serialize(new
        {
            PAPIErrorCode = rows.Length,
            TotalRecordsFound = rows.Length,
            BibSearchRows = rows
        });

    private static Dictionary<string, object?> SearchRow(
        int controlNumber,
        string title,
        string? author = null,
        string? publication = null,
        string? identifierName = null,
        string? identifier = null,
        string? materialType = null)
    {
        var row = new Dictionary<string, object?>
        {
            ["ControlNumber"] = controlNumber,
            ["Title"] = title
        };
        if (author is not null) row["Author"] = author;
        if (publication is not null) row["PublicationDate"] = publication;
        if (identifierName is not null && identifier is not null) row[identifierName] = identifier;
        if (materialType is not null) row["PrimaryTypeOfMaterial"] = materialType;
        return row;
    }

    private static string SearchPath(Uri uri)
    {
        var path = uri.AbsolutePath;
        var searchIndex = path.IndexOf("/search/bibs/", StringComparison.Ordinal);
        return searchIndex >= 0 ? path[searchIndex..] : path;
    }
}
