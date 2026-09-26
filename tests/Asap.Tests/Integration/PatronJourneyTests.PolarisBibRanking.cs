using System.Net;
using Asap.Web.Features.Patron;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
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
        Assert.AreEqual(3, handler.RequestCount, "Every search attempt must contribute before the result limit is applied.");
    }

    [TestMethod]
    public async Task PolarisTitleAuthorSearchRanksExactCrossAttemptCandidateFirst()
    {
        var handler = new SequenceResponseHandler(
            (HttpStatusCode.OK,
             """{"PAPIErrorCode":1,"TotalRecordsFound":1,"BibSearchRows":[{"ControlNumber":9201,"Title":"A title: second edition","Author":"An author","PublicationDate":"2026"}]}"""),
            (HttpStatusCode.OK,
             """{"PAPIErrorCode":1,"TotalRecordsFound":1,"BibSearchRows":[{"ControlNumber":9202,"Title":"A title","Author":"An author","PublicationDate":"1901"}]}"""));
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.SearchBibsAsync("title_author", "", "A title", "An author", CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "9202", "9201" }, result.Results.Select(row => row.BibId).ToArray());
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
}
