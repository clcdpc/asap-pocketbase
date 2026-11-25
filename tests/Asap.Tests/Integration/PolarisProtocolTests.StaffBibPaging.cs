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
    [DataRow("title_author", "", "The Shining", "Stephen King", "/search/bibs/boolean",
        "TI=\"The Shining\" AND (AU=\"Stephen King\" OR AU=\"King, Stephen\")", "PDTI")]
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
        Assert.AreEqual(handler.RequestUris[0].PathAndQuery,
            handler.RequestUris[1].PathAndQuery.Replace("page=2&", "page=1&", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("The Shining", "King, Stephen", "TI=\"The Shining\" AND (AU=\"King, Stephen\" OR AU=\"Stephen King\")")]
    [DataRow("Harry Potter and the Chamber of Secrets", "J. K. Rowling",
        "TI=\"Harry Potter and the Chamber of Secrets\" AND (AU=\"J. K. Rowling\" OR AU=\"Rowling, J. K.\")")]
    [DataRow("Nighttime", "Mary H. Clark", "TI=\"Nighttime\" AND (AU=\"Mary H. Clark\" OR AU=\"Clark, Mary H.\")")]
    [DataRow("Cien años de soledad", "García Márquez, Gabriel",
        "TI=\"Cien años de soledad\" AND (AU=\"García Márquez, Gabriel\" OR AU=\"Gabriel García Márquez\")")]
    [DataRow("𠮷野", "𠮷", "TI=\"𠮷野\" AND AU=\"𠮷\"")]
    [DataRow("A specific title", "Madonna", "TI=\"A specific title\" AND AU=\"Madonna\"")]
    [DataRow("One", "Anne Marie Smith", "TI=\"One\" AND AU=\"Anne Marie Smith\"")]
    [DataRow("Two", "Library of Congress", "TI=\"Two\" AND AU=\"Library of Congress\"")]
    [DataRow("Three", "King, Martin Luther, Jr.", "TI=\"Three\" AND AU=\"King, Martin Luther, Jr.\"")]
    [DataRow("Four", "Bombeck, 1927–1996", "TI=\"Four\" AND AU=\"Bombeck, 1927–1996\"")]
    [DataRow("Five", " Lee \t lEE ", "TI=\"Five\" AND AU=\"Lee lEE\"")]
    [DataRow("French-\"cooking\" OR\\NOT", "O'Toole, J. K.",
        "TI=\"French-\\\"cooking\\\" OR\\\\NOT\" AND (AU=\"O'Toole, J. K.\" OR AU=\"J. K. O'Toole\")")]
    [DataRow("C++: $100, 99% & more", "A\\\" OR TI=\"B",
        "TI=\"C++: $100, 99% & more\" AND AU=\"A\\\\\\\" OR TI=\\\"B\"")]
    [DataRow("a b c d e f g h i j k", "Lee", "TI=\"a b c d e f g h i j k\" AND AU=\"Lee\"")]
    [DataRow("What If?", "Randall Munroe",
        "TI=\"What If\\?\" AND (AU=\"Randall Munroe\" OR AU=\"Munroe, Randall\")")]
    [DataRow("M*A*S*H", "An? author", "TI=\"M\\*A\\*S\\*H\" AND AU=\"An\\? author\"")]
    [DataRow("A\\? \"title\"", "An\\* author", "TI=\"A\\\\\\? \\\"title\\\"\" AND AU=\"An\\\\\\* author\"")]
    public async Task StaffPairedSearchSendsCompletePhrasesWithoutFallback(string title, string author, string term)
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK, PageResponse(0, []));
        var provider = await CreatePolarisProviderAsync(handler);
        var result = await provider.SearchBibsAsync("title_author", "", title, author, 2, 1, CancellationToken.None);
        Assert.AreEqual(0, result.TotalMatches);
        AssertSearchRequest(handler.RequestUris.Single(), "/search/bibs/boolean", term, "PDTI", 1);
    }

    [TestMethod]
    [DataRow("A title", "Author #1")]
    [DataRow("#MeToo", "An author")]
    public async Task StaffPairedSearchRejectsNumberSignBeforeSending(string title, string author)
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK, PageResponse(0, []));
        var provider = await CreatePolarisProviderAsync(handler);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            provider.SearchBibsAsync("title_author", "", title, author, 2, 1, CancellationToken.None));
        Assert.AreEqual(0, handler.RequestCount);
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

    private const string TomDefinitions = """
        {"PAPIErrorCode":3,"MARCTypeOfMaterialsRows":[{"MARCTypeOfMaterialID":36,"Description":"eBook","SearchCode":"ebk"},
        {"MARCTypeOfMaterialID":41,"Description":"eAudiobook","SearchCode":"aeb"},{"MARCTypeOfMaterialID":1,"Description":"Book","SearchCode":"bks"}]}
        """;

    [TestMethod]
    [DataRow("aeb,ebk", "0-8044-2957-X", "ISBN", "080442957X", 1)]
    [DataRow("bks", "001234567890", "UPC", "001234567890", 36)]
    [DataRow("", "n 78-89035", "LCCN", "n78089035", 41)]
    public async Task PurchaseDiscoverySendsOneFilteredIdentifierSearch(string codes, string query,
        string field, string catalogValue, int primary)
    {
        var exclusions = codes.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var hit = SearchRow(42, "Title", primary);
        hit[field] = catalogValue;
        var replies = new List<(HttpStatusCode, string)>();
        if (exclusions.Length > 0)
        {
            replies.Add((HttpStatusCode.OK, TomDefinitions));
        }
        replies.Add((HttpStatusCode.OK, PageResponse(1, [hit])));
        var handler = new SequenceResponseHandler(replies.ToArray());
        var provider = await CreatePolarisProviderAsync(handler);
        var result = await provider.LookupPurchaseIdentifierAsync(query, 2, exclusions, CancellationToken.None);
        Assert.AreEqual(IdentifierLookupOutcome.Found, result.Outcome);
        Assert.AreEqual(42, result.BibId);
        var identifier = field == "LCCN" ? "N7889035" : catalogValue;
        var term = $"ISBN=\"{identifier}\" OR UPC=\"{identifier}\" OR LCCN=\"{catalogValue}\"";
        if (exclusions.Length > 0)
        {
            term = $"({term}) NOT ({string.Join(" OR ", exclusions.Select(code => "TOM=" + code))})";
        }
        AssertSearchRequest(handler.RequestUris.Last(), "/search/bibs/boolean", term, "PDTI", 1);
        Assert.AreEqual(replies.Count, handler.RequestCount);
    }

    [TestMethod]
    [DataRow("zero", IdentifierLookupOutcome.DefinitiveNotFound)]
    [DataRow("empty-hit", IdentifierLookupOutcome.OperationalFailure)]
    [DataRow("primary", IdentifierLookupOutcome.OperationalFailure)]
    [DataRow("associated", IdentifierLookupOutcome.OperationalFailure)]
    [DataRow("missing-material", IdentifierLookupOutcome.OperationalFailure)]
    [DataRow("unconfirmed", IdentifierLookupOutcome.OperationalFailure)]
    [DataRow("short", IdentifierLookupOutcome.OperationalFailure)]
    [DataRow("multiple", IdentifierLookupOutcome.Found)]
    [DataRow("large", IdentifierLookupOutcome.Found)]
    public async Task PurchaseDiscoveryChecksFirstPageCardinalityAndEvidence(string scenario, IdentifierLookupOutcome expected)
    {
        var total = scenario switch { "zero" => 0, "multiple" => 2, "large" or "short" => 111, _ => 1 };
        var count = scenario switch { "empty-hit" => 0, "short" => 1, _ => Math.Min(10, total) };
        var rows = Enumerable.Range(1, count).Select(id => SearchRow(id, "Title",
            scenario switch { "missing-material" => null, "primary" => 36, _ => 1 })).ToArray();
        foreach (var row in rows)
        {
            row["ISBN"] = scenario == "unconfirmed" ? "9780000000000" : "9780306406157";
            row["TypeOfMaterialValues"] = scenario == "associated" ? new[] { 1, 36 } : new[] { 1 };
        }
        var handler = new SequenceResponseHandler((HttpStatusCode.OK, TomDefinitions), (HttpStatusCode.OK, PageResponse(total, rows)),
            (HttpStatusCode.OK, """{"PAPIErrorCode":0,"ControlNumber":1,"PrimaryTypeOfMaterial":1,"BibGetRows":[{"ElementID":1,"Label":"ISBN","Value":"9780000000000"}]}"""));
        var provider = await CreatePolarisProviderAsync(handler);
        var result = await provider.LookupPurchaseIdentifierAsync("9780306406157", 2, ["ebk"], CancellationToken.None);
        Assert.AreEqual(expected, result.Outcome);
        Assert.AreEqual(scenario is "multiple" or "large", result.MultipleMatches);
        Assert.IsNull(result.BibId);
        Assert.AreEqual(scenario == "unconfirmed" ? 3 : 2, handler.RequestCount);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("unknown")]
    [DataRow("*9780306406157*")]
    public async Task UnsupportedPurchaseIdentifierDoesNotContactPolaris(string identifier)
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK, PageResponse(0, []));
        var provider = await CreatePolarisProviderAsync(handler);
        var result = await provider.LookupPurchaseIdentifierAsync(identifier, 2, [], CancellationToken.None);
        Assert.AreEqual("unsupported_identifier", result.ErrorCode);
        Assert.IsNull(result.BibId);
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task PurchaseDiscoveryCorroboratesSparseSearchHitFromExactBibDetail()
    {
        var handler = new SequenceResponseHandler((HttpStatusCode.OK,
            """{"PAPIErrorCode":1,"ErrorMessage":"Search terms normalized","TotalRecordsFound":1,"BibSearchRows":[{"ControlNumber":42,"PrimaryTypeOfMaterial":1}]}"""),
            (HttpStatusCode.OK, """{"PAPIErrorCode":0,"PrimaryTypeOfMaterial":1,"BibGetRows":[{"ElementID":1,"Label":"LCCN","Value":"n78089035"}]}"""));
        var provider = await CreatePolarisProviderAsync(handler);
        var result = await provider.LookupPurchaseIdentifierAsync("n 78-89035", 2, [], CancellationToken.None);
        Assert.AreEqual(IdentifierLookupOutcome.Found, result.Outcome);
        Assert.AreEqual(42, result.BibId);
        StringAssert.Contains(handler.RequestUris.Last().AbsolutePath, "/bib/42");
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("ebk OR CN=42")]
    [DataRow("36")]
    public async Task PurchaseDiscoveryRequiresRecognizedTomBeforeSearching(string code)
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK, TomDefinitions);
        var provider = await CreatePolarisProviderAsync(handler);
        var result = await provider.LookupPurchaseIdentifierAsync("9780306406157", 2, [code], CancellationToken.None);
        Assert.AreEqual(IdentifierLookupOutcome.OperationalFailure, result.Outcome);
        StringAssert.EndsWith(handler.RequestUris.Single().AbsolutePath, "/marctypeofmaterials");
    }

    [TestMethod]
    [DataRow(503, "{}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"MARCTypeOfMaterialsRows\":[]}")]
    [DataRow(200, "{\"PAPIErrorCode\":2,\"MARCTypeOfMaterialsRows\":[{\"MARCTypeOfMaterialID\":36,\"Description\":\"eBook\",\"SearchCode\":\"ebk\"}]}")]
    public async Task UnavailableTomDefinitionsBlockFilteredSearch(int status, string body)
    {
        var handler = new StaticResponseHandler((HttpStatusCode)status, body);
        var provider = await CreatePolarisProviderAsync(handler);
        var result = await provider.LookupPurchaseIdentifierAsync("9780306406157", 2, ["ebk"], CancellationToken.None);
        Assert.AreEqual(IdentifierLookupOutcome.OperationalFailure, result.Outcome);
        StringAssert.EndsWith(handler.RequestUris.Single().AbsolutePath, "/marctypeofmaterials");
    }

    [TestMethod]
    [DataRow(401, "{}")]
    [DataRow(408, "{}")]
    [DataRow(503, "{}")]
    [DataRow(200, "{}")]
    [DataRow(200, "{\"TotalRecordsFound\":-1,\"BibSearchRows\":[],\"PAPIErrorCode\":0}")]
    public async Task PurchaseDiscoveryDistinguishesProviderFailureFromZeroMatches(int status, string body)
    {
        var handler = new StaticResponseHandler((HttpStatusCode)status, body);
        var provider = await CreatePolarisProviderAsync(handler);
        var result = await provider.LookupPurchaseIdentifierAsync("9780306406157", 2, [], CancellationToken.None);
        Assert.AreEqual(IdentifierLookupOutcome.OperationalFailure, result.Outcome);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    [DataRow("eligible", IdentifierLookupOutcome.Found)]
    [DataRow("excluded", IdentifierLookupOutcome.DefinitiveNotFound)]
    [DataRow("wrong-bib", IdentifierLookupOutcome.OperationalFailure)]
    [DataRow("contradiction", IdentifierLookupOutcome.OperationalFailure)]
    public async Task StaffPurchaseBibUsesExactControlNumberWithTheSameTomFilter(string scenario, IdentifierLookupOutcome expected)
    {
        var detail = """{"PAPIErrorCode":0,"ControlNumber":42,"PrimaryTypeOfMaterial":1,"BibGetRows":[{"ElementID":1,"Value":"Title"}]}""";
        var row = SearchRow(scenario == "wrong-bib" ? 43 : 42, "Title", scenario == "contradiction" ? 36 : 1);
        var search = scenario == "excluded" ? PageResponse(0, []) : PageResponse(1, [row]);
        var handler = new SequenceResponseHandler((HttpStatusCode.OK, detail), (HttpStatusCode.OK, TomDefinitions), (HttpStatusCode.OK, search));
        var provider = await CreatePolarisProviderAsync(handler);
        var result = await provider.ValidatePurchaseBibAsync(42, 2, ["aeb", "ebk"], CancellationToken.None);
        Assert.AreEqual(expected, result.Outcome);
        AssertSearchRequest(handler.RequestUris.Last(), "/search/bibs/boolean", "(CN=42) NOT (TOM=aeb OR TOM=ebk)", "PDTI", 1);
        Assert.AreEqual(3, handler.RequestCount);
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
