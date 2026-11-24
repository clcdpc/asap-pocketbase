using System.Net;
using System.Text.Json;
using Asap.Web.Features.Patron;

namespace Asap.Tests.Integration;

public sealed partial class PolarisProtocolTests
{
    [TestMethod]
    [DataRow("0-8044-2957-X", "080442957X", "080442957X")]
    [DataRow("978-0-306-40615-7", "9780306406157", "9780306406157")]
    [DataRow("9780306406157", "9780306406157", "9780306406157")]
    [DataRow("001234567890", "001234567890", "001234567890")]
    [DataRow("n 78-89035", "N7889035", "n78089035")]
    [DataRow("85-2//r92", "852R92", "85000002")]
    [DataRow("00\" OR UPC=\\*", "00 ORUPC", "00 ORUPC")]
    public async Task StaffIdentifierSendsOneLiteralOrSearch(string input, string identifier, string lccn)
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK, PageResponse(0, []));
        var provider = await CreatePolarisProviderAsync(handler);
        var result = await provider.SearchBibsAsync("identifier", input, "", "", 2, 2, CancellationToken.None);
        Assert.AreEqual(0, result.TotalMatches);
        AssertSearchRequest(handler.RequestUris.Single(), "/search/bibs/boolean",
            $"ISBN=\"{identifier}\" OR UPC=\"{identifier}\" OR LCCN=\"{lccn}\"", "PDTI", 2);
    }

    [TestMethod]
    [DataRow("title", "  Books   & music  ", "", "", "/search/bibs/keyword/TI", "Books & music", "RELEVANCE")]
    [DataRow("author", "O’Toole, Tim", "", "", "/search/bibs/keyword/AU", "O’Toole, Tim", "AU")]
    [DataRow("title_author", "", "French-\"cooking\" OR\\NOT", "O'Toole, J. K.", "/search/bibs/boolean",
        "(TI=\"French\" AND TI=\"cooking\" AND TI=\"OR\" AND TI=\"NOT\") AND (AU=\"*O'Toole*\" AND AU=\"J*\" AND AU=\"K*\")", "PDTI")]
    [DataRow("title_author", "", "#MeToo: $100, 99% & C++", "Row", "/search/bibs/boolean",
        "(TI=\"MeToo\" AND TI=\"$100\" AND TI=\"99%\" AND TI=\"C++\") AND (AU=\"*Row*\")", "PDTI")]
    [DataRow("title_author", "", "𠮷野", "𠮷", "/search/bibs/boolean",
        "(TI=\"𠮷野\") AND (AU=\"𠮷*\")", "PDTI")]
    [DataRow("title_author", "", "a b c d e f g h i j k", "Lee", "/search/bibs/boolean",
        "(TI=\"a\" AND TI=\"b\" AND TI=\"c\" AND TI=\"d\" AND TI=\"e\" AND TI=\"f\" AND TI=\"g\" AND TI=\"h\" AND TI=\"i\" AND TI=\"j\") AND (TI=\"k\") AND (AU=\"*Lee*\")", "PDTI")]
    public async Task StaffSearchKeepsNativeOrderAndSortAcrossPages(string mode, string query, string title,
        string author, string path, string term, string sort)
    {
        var first = Enumerable.Range(1, 10).Select(i => SearchRow(i, "First page")).ToArray();
        var last = new[] { SearchRow(18, "Z"), SearchRow(15, "A") };
        var handler = new SequenceResponseHandler((HttpStatusCode.OK, PageResponse(12, first)),
            (HttpStatusCode.OK, PageResponse(12, last)));
        var provider = await CreatePolarisProviderAsync(handler);
        var one = await provider.SearchBibsAsync(mode, query, title, author, 2, 1, CancellationToken.None);
        var two = await provider.SearchBibsAsync(mode, query, title, author, 2, 2, CancellationToken.None);
        Assert.AreEqual(10, one.Results.Count);
        Assert.AreEqual(12, two.TotalMatches);
        CollectionAssert.AreEqual(new[] { 18, 15 }, two.Results.Select(row => row.BibId).ToArray());
        Assert.AreEqual(2, handler.RequestCount);
        AssertSearchRequest(handler.RequestUris[0], path, term, sort, 1);
        AssertSearchRequest(handler.RequestUris[1], path, term, sort, 2);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task StaffSearchFiltersAndDeduplicatesOnlyTheRequestedPage(bool allExcluded)
    {
        var rows = new[] { SearchRow(3, "Excluded", materialType: 36), SearchRow(2, "Excluded", materialType: 41),
            SearchRow(1, "First", materialType: allExcluded ? 36 : null),
            SearchRow(1, "Duplicate", materialType: allExcluded ? 41 : null) };
        var handler = new StaticResponseHandler(HttpStatusCode.OK, PageResponse(31, rows));
        var provider = await CreatePolarisProviderAsync(handler);
        var result = await provider.SearchBibsAsync("title", "Title", "", "", 2, 2, CancellationToken.None);
        CollectionAssert.AreEqual(allExcluded ? Array.Empty<string>() : new[] { "First" },
            result.Results.Select(row => row.Title).ToArray());
        Assert.AreEqual(31, result.TotalMatches);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    [DataRow(3, 15, 0, true)]
    [DataRow(int.MaxValue, int.MaxValue, 0, true)]
    [DataRow(2, 12, 0, false)]
    [DataRow(2, 11, 2, false)]
    [DataRow(1, 500, 11, false)]
    public async Task StaffSearchChecksRowsAgainstNativePageBoundaries(int page, int total, int count, bool valid)
    {
        var rows = Enumerable.Range(1, count).Select(i => SearchRow(i, "Title")).ToArray();
        var handler = new StaticResponseHandler(HttpStatusCode.OK, PageResponse(total, rows));
        var provider = await CreatePolarisProviderAsync(handler);
        var request = provider.SearchBibsAsync("title", "Title", "", "", 2, page, CancellationToken.None);
        if (valid)
        {
            var result = await request;
            Assert.AreEqual(total, result.TotalMatches);
            Assert.AreEqual(0, result.Results.Count);
        }
        else
        {
            await Assert.ThrowsExactlyAsync<PolarisOperationalException>(() => request);
        }
        Assert.AreEqual(1, handler.RequestCount);
    }

    private static Dictionary<string, object?> SearchRow(int id, string title, int? materialType = null) =>
        new() { ["ControlNumber"] = id, ["Title"] = title, ["PrimaryTypeOfMaterial"] = materialType };

    private static string PageResponse(int total, Dictionary<string, object?>[] rows) =>
        JsonSerializer.Serialize(new { PAPIErrorCode = rows.Length, TotalRecordsFound = total, BibSearchRows = rows });

    private static void AssertSearchRequest(Uri uri, string path, string term, string sort, int page)
    {
        Assert.AreEqual(path, SearchPath(uri));
        var parameters = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        Assert.AreEqual(term, parameters["q"].Single());
        Assert.AreEqual(sort, parameters["sortby"].Single());
        Assert.AreEqual(page.ToString(System.Globalization.CultureInfo.InvariantCulture), parameters["page"].Single());
        Assert.AreEqual("10", parameters["bibsperpage"].Single());
        Assert.AreEqual("1", parameters["notran"].Single());
    }
}
