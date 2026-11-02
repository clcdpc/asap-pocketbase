using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Data;
using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Asap.Tests.Sql;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Asap.Web.Infrastructure.Security;
using Asap.Web.Infrastructure.Testing;
using Hangfire;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.SqlServer.Dac;

namespace Asap.Tests.Integration;

public sealed partial class PolarisProtocolTests
{
    [TestMethod]
    [DataRow(503, IdentifierLookupOutcome.TransientFailure)]
    [DataRow(429, IdentifierLookupOutcome.TransientFailure)]
    [DataRow(401, IdentifierLookupOutcome.OperationalFailure)]
    public async Task PolarisIdentifierLookupClassifiesActualPackageHttpResponses(
        int statusCode,
        IdentifierLookupOutcome expected)
    {
        var handler = new StaticResponseHandler((HttpStatusCode)statusCode, "{}");
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.LookupIdentifierAsync("9780000000099", 2, CancellationToken.None);

        Assert.AreEqual(expected, result.Outcome);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task PolarisStaffSearchUsesSupportedQueriesAndMapsCatalogRows()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            """
            {"PAPIErrorCode":0,"TotalRecordsFound":4,"BibSearchRows":[
              {"ControlNumber":9001,"Title":"  Catalog title  ","Author":"Catalog author",
               "PublicationDate":"2026","ISBN":"9780000000001","TypeOfMaterial":"1"},
              {"ControlNumber":9002,"Title":"Electronic title","PrimaryTypeOfMaterial":36},
              {"ControlNumber":9003,"Title":"Alternate title","Author":"Second author",
               "TypeOfMaterial":"Book","UPC":"012345678901"},
              {"ControlNumber":9001,"Title":"Duplicate title"}]}
            """);
        var provider = await CreatePolarisProviderAsync(handler);

        var byIdentifier = await provider.SearchBibsAsync("identifier", "978-0000000001", "", "", 2, CancellationToken.None);
        Assert.AreEqual(2, byIdentifier.Results.Count);
        Assert.AreEqual(4, byIdentifier.TotalMatches);
        Assert.AreEqual(9001, byIdentifier.Results[0].BibId);
        Assert.AreEqual("Catalog title", byIdentifier.Results[0].Title);
        Assert.AreEqual("2026", byIdentifier.Results[0].Publication);
        Assert.AreEqual(9003, byIdentifier.Results[1].BibId);
        Assert.AreEqual("012345678901", byIdentifier.Results[1].Identifier);
        CollectionAssert.AreEqual(
            new[] { "ISBN", "boolean", "LCCN" },
            handler.RequestUris.Where(uri => uri.AbsolutePath.Contains("/search/bibs/", StringComparison.Ordinal))
                .Select(uri => uri.AbsolutePath.Split('/').Last()).ToArray());
        StringAssert.Contains(Uri.UnescapeDataString(handler.RequestUris[1].Query), "q=UPC=\"9780000000001\"");
        Assert.IsTrue(handler.RequestUris.Where(uri => uri.AbsolutePath.Contains("/search/bibs/", StringComparison.Ordinal)).All(uri =>
            Uri.UnescapeDataString(uri.Query).Contains("sortby=PDTI", StringComparison.Ordinal) &&
            Uri.UnescapeDataString(uri.Query).Contains("notran=1", StringComparison.Ordinal) &&
            !Uri.UnescapeDataString(uri.Query).Contains("sort=", StringComparison.Ordinal)));

        handler.RequestUris.Clear();
        await provider.SearchBibsAsync("title", "A title", "", "", 2, CancellationToken.None);
        await provider.SearchBibsAsync("author", "An author", "", "", 2, CancellationToken.None);
        await provider.SearchBibsAsync("title_author", "", "A title", "An author", 2, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "TI", "AU", "boolean", "TI" },
            handler.RequestUris.Select(uri => uri.AbsolutePath.Split('/').Last()).ToArray());
        StringAssert.Contains(Uri.UnescapeDataString(handler.RequestUris[2].Query),
            "q=TI=\"A title\" AND AU=\"An author\"");
    }

    [TestMethod]
    public async Task PolarisStaffSearchAcceptsSparseRowsWithAValidBIBIdentity()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            """{"PAPIErrorCode":0,"TotalRecordsFound":1,"BibSearchRows":[{"ControlNumber":9001}]}""");
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.SearchBibsAsync("title", "A title", "", "", 2, CancellationToken.None);

        Assert.AreEqual(1, result.Results.Count);
        Assert.AreEqual(9001, result.Results[0].BibId);
        Assert.IsNull(result.Results[0].Title);
        Assert.AreEqual(1, result.TotalMatches);
    }

    [TestMethod]
    [DataRow("{\"BibID\":9001}")]
    [DataRow("{\"BibliographicRecordID\":9001}")]
    public async Task PolarisStaffSearchRejectsUndocumentedBIBIdentityAliases(string row)
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            $$"""{"PAPIErrorCode":0,"TotalRecordsFound":1,"BibSearchRows":[{{row}}]}""");
        var provider = await CreatePolarisProviderAsync(handler);

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(() =>
            provider.SearchBibsAsync("title", "A title", "", "", 2, CancellationToken.None));
    }

    [TestMethod]
    public async Task PolarisStaffSearchAcceptsOnlyAWellFormedNonnegativeEmptyResponseAsNotFound()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            """{"PAPIErrorCode":0,"TotalRecordsFound":0,"BibSearchRows":[],"ErrorMessage":""}""");
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.SearchBibsAsync("title", "A title", "", "", 2, CancellationToken.None);

        Assert.AreEqual(0, result.Results.Count);
        Assert.AreEqual(0, result.TotalMatches);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task PolarisStaffSearchTreatsCodeZeroEmptyInformationalMessageAsNotFound()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            """{"PAPIErrorCode":0,"ErrorMessage":"Search terms normalized","TotalRecordsFound":0,"BibSearchRows":[]}""");
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.SearchBibsAsync("title", "A title", "", "", 2, CancellationToken.None);

        Assert.AreEqual(0, result.Results.Count);
        Assert.AreEqual(0, result.TotalMatches);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task PolarisStaffTitleSearchAcceptsTheCoherentMinusOneEmptyResponse()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            """{"PAPIErrorCode":-1,"TotalRecordsFound":0,"BibSearchRows":[]}""");
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.SearchBibsAsync("title", "A title", "", "", 2, CancellationToken.None);

        Assert.AreEqual(0, result.Results.Count);
        Assert.AreEqual(0, result.TotalMatches);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task PolarisStaffSearchAcceptsPapiRowCountAndInformationalMessage()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            """{"PAPIErrorCode":1,"ErrorMessage":"Search terms normalized","TotalRecordsFound":1,"BibSearchRows":[{"ControlNumber":9001}]}""");
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.SearchBibsAsync("title", "A title", "", "", 2, CancellationToken.None);

        Assert.AreEqual(9001, result.Results.Single().BibId);
    }

    [TestMethod]
    public async Task PolarisStaffSearchFiltersKnownMaterialTypesAfterValidatingTheirRows()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            """{"PAPIErrorCode":0,"TotalRecordsFound":1,"BibSearchRows":[{"ControlNumber":9001,"PrimaryTypeOfMaterial":36}]}""");
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.SearchBibsAsync("title", "A title", "", "", 2, CancellationToken.None);

        Assert.AreEqual(0, result.Results.Count);
        Assert.AreEqual(1, result.TotalMatches);
    }

    [TestMethod]
    [DataRow(503, "{}")]
    [DataRow(200, "not-json")]
    [DataRow(200, "{}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"BibSearchRows\":[]}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{}]}")]
    [DataRow(200, "{\"PAPIErrorCode\":-1,\"ErrorMessage\":\"General failure\",\"TotalRecordsFound\":0,\"BibSearchRows\":[]}")]
    [DataRow(200, "{\"PAPIErrorCode\":-1,\"ErrorMessage\":\"SQL timeout\",\"TotalRecordsFound\":0,\"BibSearchRows\":[]}")]
    [DataRow(200, "{\"PAPIErrorCode\":-9,\"ErrorMessage\":\"SQL timeout\",\"TotalRecordsFound\":0,\"BibSearchRows\":[]}")]
    [DataRow(200, "{\"PAPIErrorCode\":-1,\"TotalRecordsFound\":1,\"BibSearchRows\":[]}")]
    [DataRow(200, "{\"PAPIErrorCode\":-1,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":9001}]}")]
    [DataRow(200, "{\"PAPIErrorCode\":-1,\"TotalRecordsFound\":0}")]
    [DataRow(200, "{\"PAPIErrorCode\":-1,\"BibSearchRows\":[]}")]
    [DataRow(200, "{\"PAPIErrorCode\":-1,\"TotalRecordsFound\":0,\"BibSearchRows\":[],\"papierrorcode\":0}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":0}]}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":-1}]}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":\"not-a-number\"}]}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":9001,\"BibID\":9002}]}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":9001,\"controlnumber\":9001}]}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"papierrorcode\":-9,\"TotalRecordsFound\":0,\"BibSearchRows\":[]}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"TotalRecordsFound\":0,\"BibSearchRows\":[],\"bibsearchrows\":[]}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[]}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"TotalRecordsFound\":2,\"totalrecordsfound\":2,\"BibSearchRows\":[{\"ControlNumber\":9001}]}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"TotalRecordsFound\":2,\"BibSearchRows\":[{\"ControlNumber\":9001},{}]}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"TotalRecordsFound\":0,\"BibSearchRows\":[{\"ControlNumber\":9001}]}")]
    [DataRow(200, "{\"PAPIErrorCode\":2,\"TotalRecordsFound\":2,\"BibSearchRows\":[{\"ControlNumber\":9001}]}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":0,\"PrimaryTypeOfMaterial\":36}]}")]
    public async Task PolarisStaffSearchTreatsTransportAndMalformedResponsesAsUnavailable(
        int statusCode,
        string content)
    {
        var handler = new StaticResponseHandler((HttpStatusCode)statusCode, content);
        var provider = await CreatePolarisProviderAsync(handler);

        var failure = await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await provider.SearchBibsAsync("title", "A title", "", "", 2, CancellationToken.None));

        Assert.AreEqual("polaris_bib_search_failed", failure.Code);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task PolarisIdentifierSearchKeepsTrustworthyPartialResultsAndFailsWhenEveryAttemptFails()
    {
        var valid = """{"PAPIErrorCode":0,"TotalRecordsFound":1,"BibSearchRows":[{"ControlNumber":9001,"Title":"Catalog title"}]}""";
        var first = new SequenceResponseHandler(
            (HttpStatusCode.OK, valid),
            (HttpStatusCode.ServiceUnavailable, "{}"),
            (HttpStatusCode.InternalServerError, "{}"));
        var firstProvider = await CreatePolarisProviderAsync(first);
        var firstResult = await firstProvider.SearchBibsAsync("identifier", "9780000000001", "", "", 2, CancellationToken.None);
        Assert.AreEqual(9001, firstResult.Results.Single().BibId);
        Assert.AreEqual(3, first.RequestCount);

        var partial = new SequenceResponseHandler(
            (HttpStatusCode.ServiceUnavailable, "{}"),
            (HttpStatusCode.OK, valid),
            (HttpStatusCode.ServiceUnavailable, "{}"));
        var partialProvider = await CreatePolarisProviderAsync(partial);

        var result = await partialProvider.SearchBibsAsync("identifier", "9780000000001", "", "", 2, CancellationToken.None);

        Assert.AreEqual(1, result.Results.Count);
        Assert.AreEqual(9001, result.Results[0].BibId);
        Assert.AreEqual(3, partial.RequestCount);

        var last = new SequenceResponseHandler(
            (HttpStatusCode.ServiceUnavailable, "{}"),
            (HttpStatusCode.InternalServerError, "{}"),
            (HttpStatusCode.OK, valid));
        var lastProvider = await CreatePolarisProviderAsync(last);
        var lastResult = await lastProvider.SearchBibsAsync("identifier", "9780000000001", "", "", 2, CancellationToken.None);
        Assert.AreEqual(9001, lastResult.Results.Single().BibId);
        Assert.AreEqual(3, last.RequestCount);

        var failed = new SequenceResponseHandler(
            (HttpStatusCode.ServiceUnavailable, "{}"),
            (HttpStatusCode.InternalServerError, "{}"),
            (HttpStatusCode.BadGateway, "{}"));
        var failedProvider = await CreatePolarisProviderAsync(failed);
        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await failedProvider.SearchBibsAsync("identifier", "9780000000001", "", "", 2, CancellationToken.None));
        Assert.AreEqual(3, failed.RequestCount);

        var empty = """{"PAPIErrorCode":0,"TotalRecordsFound":0,"BibSearchRows":[]}""";
        var allEmpty = new SequenceResponseHandler(
            (HttpStatusCode.OK, """{"PAPIErrorCode":-1,"TotalRecordsFound":0,"BibSearchRows":[]}"""),
            (HttpStatusCode.OK, empty),
            (HttpStatusCode.OK, """{"PAPIErrorCode":-1,"TotalRecordsFound":0,"BibSearchRows":[]}"""));
        var emptyProvider = await CreatePolarisProviderAsync(allEmpty);
        var noMatches = await emptyProvider.SearchBibsAsync("identifier", "9780000000001", "", "", 2, CancellationToken.None);
        Assert.AreEqual(0, noMatches.Results.Count);
        Assert.AreEqual(3, allEmpty.RequestCount);

        var emptyAndFailed = new SequenceResponseHandler(
            (HttpStatusCode.OK, """{"PAPIErrorCode":-1,"TotalRecordsFound":0,"BibSearchRows":[]}"""),
            (HttpStatusCode.ServiceUnavailable, "{}"),
            (HttpStatusCode.OK, empty));
        var emptyAndFailedProvider = await CreatePolarisProviderAsync(emptyAndFailed);
        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await emptyAndFailedProvider.SearchBibsAsync(
                "identifier", "9780000000001", "", "", 2, CancellationToken.None));
        Assert.AreEqual(3, emptyAndFailed.RequestCount);
    }

    [TestMethod]
    public async Task PolarisIdentifierLookupAcceptsOnlyCoherentMinusOneAndZeroEmptyAttempts()
    {
        var handler = new SequenceResponseHandler(
            (HttpStatusCode.OK, """{"PAPIErrorCode":-1,"TotalRecordsFound":0,"BibSearchRows":[]}"""),
            (HttpStatusCode.OK, """{"PAPIErrorCode":0,"TotalRecordsFound":0,"BibSearchRows":[]}"""),
            (HttpStatusCode.OK, """{"PAPIErrorCode":-1,"TotalRecordsFound":0,"BibSearchRows":[]}"""));
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.LookupIdentifierAsync("9780000000001", 2, CancellationToken.None);

        Assert.AreEqual(IdentifierLookupOutcome.NotFound, result.Outcome);
        Assert.AreEqual(3, handler.RequestCount);
    }

    [TestMethod]
    public async Task PolarisIdentifierLookupFindsOnlyRowsWithValidatedControlNumbers()
    {
        var handler = new SequenceResponseHandler(
            (HttpStatusCode.OK,
             """{"PAPIErrorCode":1,"TotalRecordsFound":2,"BibSearchRows":[{"ControlNumber":9001,"Title":"Catalog title"}]}"""),
            (HttpStatusCode.OK, """{"PAPIErrorCode":0,"TotalRecordsFound":0,"BibSearchRows":[]}"""),
            (HttpStatusCode.OK, """{"PAPIErrorCode":0,"TotalRecordsFound":0,"BibSearchRows":[]}"""),
            (HttpStatusCode.OK, """{"PAPIErrorCode":-1,"ErrorMessage":"Invalid BibID","BibGetRows":null}"""));
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.LookupIdentifierAsync("9780000000001", 2, CancellationToken.None);

        Assert.AreEqual(IdentifierLookupOutcome.Found, result.Outcome);
        Assert.AreEqual(9001, result.BibId);
        Assert.IsTrue(result.MultipleMatches, "TotalRecordsFound may exceed the current page row count.");
        Assert.AreEqual(4, handler.RequestCount);
    }

    [TestMethod]
    public async Task PolarisIdentifierLookupPreservesCancellationDuringOptionalBibDetail()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new SequenceResponseHandler(
            (HttpStatusCode.OK,
             """{"PAPIErrorCode":0,"TotalRecordsFound":1,"BibSearchRows":[{"ControlNumber":9001,"Title":"Catalog title"}]}"""),
            (HttpStatusCode.OK, """{"PAPIErrorCode":0,"TotalRecordsFound":0,"BibSearchRows":[]}"""),
            (HttpStatusCode.OK, """{"PAPIErrorCode":0,"TotalRecordsFound":0,"BibSearchRows":[]}"""));
        handler.OnRequest = (count, token) =>
        {
            if (count == 4)
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
        };
        var provider = await CreatePolarisProviderAsync(handler);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await provider.LookupIdentifierAsync("9780000000001", 2, cancellation.Token));

        Assert.AreEqual(4, handler.RequestCount);
        Assert.IsTrue(cancellation.IsCancellationRequested);
    }

    [TestMethod]
    [DataRow("{\"PAPIErrorCode\":0,\"ErrorMessage\":\"Search terms normalized\",\"TotalRecordsFound\":0,\"BibSearchRows\":[]}")]
    [DataRow("{\"PAPIErrorCode\":-1,\"TotalRecordsFound\":0,\"BibSearchRows\":[],\"ErrorMessage\":\"\"}")]
    [DataRow("""{"PAPIErrorCode":-1,"TotalRecordsFound":0,"BibSearchRows":[],"ErrorMessage":"  "}""")]
    public async Task PolarisIdentifierLookupTreatsInformationalOrBlankEmptyMessagesAsDefinitiveEmpty(string content)
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK, content);
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.LookupIdentifierAsync("9780000000001", 2, CancellationToken.None);

        Assert.AreEqual(IdentifierLookupOutcome.DefinitiveNotFound, result.Outcome);
        Assert.AreEqual(3, handler.RequestCount);
    }

    [TestMethod]
    public async Task PolarisTitleAuthorSearchCanUseAValidFallbackAfterPrimaryFailure()
    {
        var handler = new SequenceResponseHandler(
            (HttpStatusCode.ServiceUnavailable, "{}"),
            (HttpStatusCode.OK,
             """{"PAPIErrorCode":0,"TotalRecordsFound":1,"BibSearchRows":[{"ControlNumber":9002,"Title":"Fallback title","Author":"An author"}]}"""));
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.SearchBibsAsync("title_author", "", "A title", "An author", 2, CancellationToken.None);

        Assert.AreEqual(1, result.Results.Count);
        Assert.AreEqual(9002, result.Results[0].BibId);
        Assert.AreEqual(2, handler.RequestCount);
    }

    [TestMethod]
    public async Task PolarisStaffSearchPreservesCancellationBeforeTheProviderRequest()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            """{"PAPIErrorCode":0,"TotalRecordsFound":0,"BibSearchRows":[]}""");
        var provider = await CreatePolarisProviderAsync(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var canceled = false;
        try
        {
            await provider.SearchBibsAsync("title", "A title", "", "", 2, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        Assert.IsTrue(canceled);
        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task PolarisPatronHoldsAcceptsValidEmptyAndPopulatedResponses()
    {
        var emptyProvider = await CreatePolarisProviderAsync(new ProtectedSequenceResponseHandler(
            (HttpStatusCode.OK, """{"PAPIErrorCode":0,"PatronHoldRequestsGetRows":[]}""")));
        var empty = await emptyProvider.GetPatronHoldsAsync("20000000000001", 2, CancellationToken.None);
        Assert.AreEqual(0, empty.Count);

        var populatedProvider = await CreatePolarisProviderAsync(new ProtectedSequenceResponseHandler(
            (HttpStatusCode.OK, """{"PAPIErrorCode":0,"PatronHoldRequestsGetRows":[{"HoldRequestID":51,"BibID":9001,"StatusID":6,"StatusDescription":"Held","PickupBranchID":101}]}""")));
        var populated = await populatedProvider.GetPatronHoldsAsync("20000000000001", 2, CancellationToken.None);
        Assert.AreEqual(1, populated.Count);
        Assert.AreEqual(51, populated[0].HoldRequestId);
        Assert.AreEqual(9001, populated[0].BibId);
        Assert.AreEqual(6, populated[0].StatusId);
        Assert.AreEqual("Held", populated[0].StatusDescription);
        Assert.IsFalse(HoldPlacementService.IsTerminal(populated[0].StatusId));
        Assert.AreEqual(101, populated[0].PickupBranchId);
        Assert.AreEqual("20000000000001", populated[0].PatronBarcode);
    }

    [TestMethod]
    public async Task PolarisPatronHoldsPreservesKnownTerminalStatusEvidence()
    {
        var provider = await CreatePolarisProviderAsync(new ProtectedSequenceResponseHandler(
            (HttpStatusCode.OK, """{"PAPIErrorCode":0,"PatronHoldRequestsGetRows":[{"HoldRequestID":51,"BibID":9001,"StatusID":8,"StatusDescription":"Unclaimed"}]}""")));

        var holds = await provider.GetPatronHoldsAsync("20000000000001", 2, CancellationToken.None);

        Assert.AreEqual(8, holds.Single().StatusId);
        Assert.AreEqual("Unclaimed", holds.Single().StatusDescription);
        Assert.IsNull(holds.Single().PickupBranchId,
            "A terminal hold remains valid when this optional pickup field is omitted.");
        Assert.IsTrue(HoldPlacementService.IsTerminal(holds.Single().StatusId));
    }

    [TestMethod]
    public async Task PolarisPatronHoldsAcceptsCompatibleNumericStringIdentities()
    {
        var provider = await CreatePolarisProviderAsync(new ProtectedSequenceResponseHandler(
            (HttpStatusCode.OK, """{"PAPIErrorCode":0,"PatronHoldRequestsGetRows":[{"HoldRequestID":"51","BibID":"9001","StatusID":"6","StatusDescription":" Held "}]}""")));

        var holds = await provider.GetPatronHoldsAsync("20000000000001", 2, CancellationToken.None);

        Assert.AreEqual(51, holds.Single().HoldRequestId);
        Assert.AreEqual(9001, holds.Single().BibId);
        Assert.AreEqual(6, holds.Single().StatusId);
        Assert.AreEqual("Held", holds.Single().StatusDescription);
        Assert.IsNull(holds.Single().PickupBranchId,
            "Compatible numeric-string identity fields do not make an omitted optional pickup fabricated evidence.");
    }

    [TestMethod]
    [DataRow("not-json")]
    [DataRow("[]")]
    [DataRow("{}")]
    [DataRow("{\"PatronHoldRequestsGetRows\":[]}")]
    [DataRow("{\"PAPIErrorCode\":\"0\",\"PatronHoldRequestsGetRows\":[]}")]
    [DataRow("{\"PAPIErrorCode\":-1,\"ErrorMessage\":\"failure\",\"PatronHoldRequestsGetRows\":[]}")]
    [DataRow("{\"PAPIErrorCode\":-9,\"papierrorcode\":0,\"PatronHoldRequestsGetRows\":[]}")]
    [DataRow("{\"PAPIErrorCode\":0}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":null}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":{}}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":0,\"BibID\":9001}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":0}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":-1,\"BibID\":9001}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":-1}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":\"bad\",\"BibID\":9001}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":true}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51.5,\"BibID\":9001}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"holdrequestid\":51,\"BibID\":9001}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"holdrequestid\":52,\"BibID\":9001}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":9001,\"bibid\":9002}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":9001},{ }]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":9001,\"StatusDescription\":\"Held\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":9001,\"StatusID\":0,\"StatusDescription\":\"Held\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":9001,\"StatusID\":2,\"StatusDescription\":\"Held\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":9001,\"StatusID\":\"bad\",\"StatusDescription\":\"Held\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":9001,\"StatusID\":6.5,\"StatusDescription\":\"Held\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":9001,\"StatusID\":6}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":9001,\"StatusID\":6,\"StatusDescription\":null}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":9001,\"StatusID\":6,\"StatusDescription\":\"  \"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":9001,\"StatusID\":6,\"statusid\":6,\"StatusDescription\":\"Held\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":9001,\"StatusID\":6,\"statusid\":8,\"StatusDescription\":\"Held\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":9001,\"StatusID\":6,\"StatusDescription\":\"Held\",\"statusdescription\":\"Held\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":9001,\"StatusID\":6,\"StatusDescription\":\"Held\",\"statusdescription\":\"Active\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":9001,\"StatusID\":6,\"StatusDescription\":\"Held\"},{\"HoldRequestID\":52,\"BibID\":9002,\"StatusID\":3}]}")]
    public async Task PolarisPatronHoldsTreatsMalformedOrContradictoryResponsesAsUnavailable(string content)
    {
        var provider = await CreatePolarisProviderAsync(new ProtectedSequenceResponseHandler(
            (HttpStatusCode.OK, content)));

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await provider.GetPatronHoldsAsync("20000000000001", 2, CancellationToken.None));
    }

    [TestMethod]
    public async Task PolarisExactBibCarriesCatalogDetailsWithoutChangingRequestOptionSemantics()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            """
            {"PAPIErrorCode":0,"BibGetRows":[{"ElementID":35,"Label":"Title","Value":"Catalog title"},
              {"ElementID":18,"Label":"Author","Value":"Catalog author"},
              {"ElementID":2,"Label":"Publisher","Value":"Catalog publisher"},
              {"ElementID":6,"Label":"ISBN","Value":"9780000000001"},
              {"ElementID":17,"Label":"Format","Value":"Book"},
              {"ElementID":1,"Label":"Publication Date","Value":"2026"}],
             "Title":"Catalog title","Author":["Catalog author"],"Publisher":["Catalog publisher"],
             "Format":"Book","ISBN":"9780000000001"}
            """);
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.ValidateBibAsync(9001, 2, CancellationToken.None);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual("Catalog title", result.Title);
        Assert.AreEqual("Catalog author", result.Author);
        Assert.AreEqual("2026", result.Publication);
        Assert.AreEqual("Book", result.Format);
        Assert.AreEqual("9780000000001", result.Identifier);
        Assert.AreEqual("Catalog publisher", result.Publisher);
        StringAssert.Contains(handler.RequestPaths[0], "/bib/9001");
    }

    [TestMethod]
    public async Task PolarisExactBibAcceptsSparseRowWithoutOptionalMetadata()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            """
            {"PAPIErrorCode":0,"BibGetRows":[{"ElementID":13,"Value":"QA 76"}]}
            """);
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.ValidateBibAsync(9001, 2, CancellationToken.None);

        Assert.IsTrue(result.IsValid);
        Assert.IsNull(result.Title);
        Assert.IsNull(result.Author);
        Assert.IsNull(result.Publication);
        Assert.IsNull(result.Format);
        Assert.IsNull(result.Identifier);
        Assert.IsNull(result.Publisher);
    }

    [TestMethod]
    public async Task PolarisExactBibClassifiesTheDocumentedInvalidBibResponseAsNotFound()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            """
            {"PAPIErrorCode":-1,"ErrorMessage":"Invalid BibID","BibGetRows":null}
            """);
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.ValidateBibAsync(9001, 2, CancellationToken.None);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task PolarisExactBibDoesNotClassifyAmbiguousInvalidResponseAsNotFound()
    {
        var provider = await CreatePolarisProviderAsync(new StaticResponseHandler(HttpStatusCode.OK,
            """
            {"PAPIErrorCode":-1,"ErrorMessage":"Invalid BibID","errormessage":"SQL timeout","BibGetRows":null}
            """));

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await provider.ValidateBibAsync(9001, 2, CancellationToken.None));
    }

    [TestMethod]
    public async Task PolarisExactBibTreatsHttpFailureAsOperational()
    {
        var provider = await CreatePolarisProviderAsync(
            new StaticResponseHandler(HttpStatusCode.ServiceUnavailable, "{}"));

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await provider.ValidateBibAsync(9001, 2, CancellationToken.None));
    }

    [TestMethod]
    public async Task PolarisExactBibPreservesCancellationBeforeLocalInputChecks()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK, "{}");
        var provider = await CreatePolarisProviderAsync(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await provider.ValidateBibAsync(0, 2, cancellation.Token));
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    [DataRow(-9, "SQL timeout")]
    [DataRow(-1, "FAILURE - General")]
    public async Task PolarisExactBibTreatsOperationalPapiFailuresAsUnavailable(
        int papiErrorCode,
        string errorMessage)
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            $$"""
            {"PAPIErrorCode":{{papiErrorCode}},"ErrorMessage":"{{errorMessage}}","BibGetRows":null}
            """);
        var provider = await CreatePolarisProviderAsync(handler);

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await provider.ValidateBibAsync(9001, 2, CancellationToken.None));
    }

    [TestMethod]
    [DataRow("not-json")]
    [DataRow("{}")]
    [DataRow("{\"PAPIErrorCode\":0}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibGetRows\":[]}")]
    [DataRow("null")]
    public async Task PolarisExactBibTreatsMalformedOrMissingProtocolDataAsUnavailable(string content)
    {
        var provider = await CreatePolarisProviderAsync(new StaticResponseHandler(HttpStatusCode.OK, content));

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await provider.ValidateBibAsync(9001, 2, CancellationToken.None));
    }

    [TestMethod]
    [DataRow("{\"PAPIErrorCode\":0,\"BibGetRows\":[{}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibGetRows\":[{\"Value\":\"Catalog title\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibGetRows\":[{\"ElementID\":35}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibGetRows\":[{\"ElementID\":0,\"Value\":\"Catalog title\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibGetRows\":[{\"ElementID\":\"35\",\"Value\":\"Catalog title\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibGetRows\":[{\"ElementID\":35.5,\"Value\":\"Catalog title\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibGetRows\":[{\"ElementID\":35,\"Value\":42}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibGetRows\":[{\"ElementID\":35,\"Value\":null}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibGetRows\":[{\"ElementID\":35,\"elementid\":0,\"Value\":\"Catalog title\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibGetRows\":[{\"ElementID\":35,\"Value\":\"Catalog title\"},{}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibGetRows\":[{\"ElementID\":35,\"Value\":\"Catalog title\"}],\"bibgetrows\":[{}]}")]
    public async Task PolarisExactBibTreatsMalformedSuccessRowsAsUnavailable(string content)
    {
        var provider = await CreatePolarisProviderAsync(new StaticResponseHandler(HttpStatusCode.OK, content));

        var failure = await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await provider.ValidateBibAsync(9001, 2, CancellationToken.None));

        Assert.AreEqual(
            "polaris_bib_validation_protocol_failed",
            failure.Code,
            $"Original provider exception type: {failure.InnerException?.GetType().FullName ?? "<none>"}.");
    }

    [TestMethod]
    public async Task PolarisExactBibRejectsInconsistentRawPapiCodesAsUnavailable()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            """
            {"PAPIErrorCode":0,"papierrorcode":-9,"BibGetRows":[{"Label":"Title","Value":"Catalog title"}]}
            """);
        var provider = await CreatePolarisProviderAsync(handler);

        var failure = await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await provider.ValidateBibAsync(9001, 2, CancellationToken.None));
        Assert.AreEqual("polaris_bib_validation_protocol_failed", failure.Code);
    }

    [TestMethod]
    public async Task PolarisStaffHoldingsCountsOwningLibrariesAndReportsFailures()
    {
        var handler = new SequenceResponseHandler(
            (HttpStatusCode.OK,
             """
             {"PAPIErrorCode":0,"BibHoldingsGetRows":[
               {"LocationID":"201","Holdable":"true"},
               {"LocationID":"202","Holdable":"false"},
               {"LocationID":"301","Holdable":"yes"}]}
             """),
            (HttpStatusCode.OK,
             """
             {"PAPIErrorCode":0,"OrganizationsGetRows":[
               {"OrganizationID":2,"OrganizationCodeID":2,"DisplayName":"My Library"},
               {"OrganizationID":201,"OrganizationCodeID":3,"ParentOrganizationID":2},
               {"OrganizationID":202,"OrganizationCodeID":3,"ParentOrganizationID":2},
               {"OrganizationID":3,"OrganizationCodeID":2,"DisplayName":"Other Library"},
               {"OrganizationID":301,"OrganizationCodeID":3,"ParentOrganizationID":3}]}
             """));
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.GetBibHoldingsAsync(9001, 2, CancellationToken.None);

        Assert.AreEqual(2, result.MyLibraryCount);
        Assert.AreEqual(1, result.OtherLibraryCount);
        Assert.AreEqual(3, result.ConsortiumCount);
        Assert.IsTrue(result.IsHoldable);
        Assert.IsTrue(result.HasHoldableAtMyLibrary);
        Assert.AreEqual(2, handler.RequestUris.Count);
        StringAssert.Contains(handler.RequestUris[0].AbsolutePath, "/bib/9001/holdings");
        StringAssert.Contains(handler.RequestUris[1].AbsolutePath, "/organizations/");

        var failed = await CreatePolarisProviderAsync(new StaticResponseHandler(HttpStatusCode.ServiceUnavailable, "{}"));
        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await failed.GetBibHoldingsAsync(9001, 2, CancellationToken.None));
    }

    [TestMethod]
    public async Task PolarisStaffHoldingsTreatsOnlyAWellFormedSuccessWithNoRowsAsZero()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            """{"PAPIErrorCode":0,"ErrorMessage":"","BibHoldingsGetRows":[]}""");
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.GetBibHoldingsAsync(9001, 2, CancellationToken.None);

        Assert.AreEqual(0, result.MyLibraryCount);
        Assert.AreEqual(0, result.OtherLibraryCount);
        Assert.AreEqual(0, result.ConsortiumCount);
        Assert.IsFalse(result.IsHoldable);
        Assert.IsFalse(result.HasHoldableAtMyLibrary);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    [DataRow("{\"PAPIErrorCode\":-1,\"ErrorMessage\":\"SQL timeout\",\"BibHoldingsGetRows\":[]}")]
    [DataRow("{\"PAPIErrorCode\":-1,\"ErrorMessage\":\"Invalid BibID\",\"BibHoldingsGetRows\":null}")]
    public async Task PolarisStaffHoldingsTreatsEveryNegativePapiCodeAsUnavailable(string content)
    {
        var provider = await CreatePolarisProviderAsync(new StaticResponseHandler(HttpStatusCode.OK, content));

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await provider.GetBibHoldingsAsync(9001, 2, CancellationToken.None));
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("not-json")]
    [DataRow("{\"PAPIErrorCode\":0}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibHoldingsGetRows\":null}")]
    [DataRow("{\"PAPIErrorCode\":0,\"papierrorcode\":-9,\"BibHoldingsGetRows\":[]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibHoldingsGetRows\":[],\"bibholdingsgetrows\":[]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibHoldingsGetRows\":[{}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibHoldingsGetRows\":[{\"LocationID\":\"bad\",\"Holdable\":\"true\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibHoldingsGetRows\":[{\"LocationID\":\"201\",\"Holdable\":\"unknown\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibHoldingsGetRows\":[{\"LocationID\":\"201\",\"locationid\":\"202\",\"Holdable\":\"true\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibHoldingsGetRows\":[{\"LocationID\":\"201\",\"Holdable\":\"true\",\"holdable\":\"false\"}]}")]
    public async Task PolarisStaffHoldingsRejectsMalformedSuccessAsUnavailable(string content)
    {
        var provider = await CreatePolarisProviderAsync(new StaticResponseHandler(HttpStatusCode.OK, content));

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await provider.GetBibHoldingsAsync(9001, 2, CancellationToken.None));
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("not-json")]
    [DataRow("{\"PAPIErrorCode\":-1,\"ErrorMessage\":\"SQL timeout\",\"TotalRecordsFound\":0,\"BibSearchRows\":[]}")]
    [DataRow("{\"PAPIErrorCode\":-1,\"ErrorMessage\":\"General failure\",\"TotalRecordsFound\":0,\"BibSearchRows\":[]}")]
    [DataRow("{\"PAPIErrorCode\":-1,\"TotalRecordsFound\":1,\"BibSearchRows\":[]}")]
    [DataRow("{\"PAPIErrorCode\":-1,\"BibSearchRows\":[]}")]
    [DataRow("{\"PAPIErrorCode\":-1,\"TotalRecordsFound\":0}")]
    [DataRow("{\"PAPIErrorCode\":-1,\"TotalRecordsFound\":0,\"BibSearchRows\":[],\"papierrorcode\":0}")]
    [DataRow("{\"PAPIErrorCode\":-1,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":9001}]}")]
    [DataRow("{\"PAPIErrorCode\":-9,\"TotalRecordsFound\":0,\"BibSearchRows\":[]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibSearchRows\":[{\"ControlNumber\":9001}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"TotalRecordsFound\":0,\"BibSearchRows\":[{\"ControlNumber\":9001}]}")]
    [DataRow("{\"PAPIErrorCode\":2,\"TotalRecordsFound\":2,\"BibSearchRows\":[{\"ControlNumber\":9001}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"totalrecordsfound\":1,\"BibSearchRows\":[{\"ControlNumber\":9001}]}")]
    [DataRow("{\"PAPIErrorCode\":2,\"TotalRecordsFound\":2,\"BibSearchRows\":[{\"ControlNumber\":9001},{\"ControlNumber\":9002},{\"ControlNumber\":9003}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"TotalRecordsFound\":2,\"BibSearchRows\":[{\"ControlNumber\":9001},{}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"TotalRecordsFound\":2,\"BibSearchRows\":[{\"ControlNumber\":9001},{\"ControlNumber\":\"bad\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":0}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":-1}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":1.5}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":\"bad\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"BibID\":9001}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":9001,\"controlnumber\":9001}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":9001,\"controlnumber\":9002}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":9001,\"BibID\":9002}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":9001,\"BibliographicRecordID\":9002}]}")]
    public async Task PolarisIdentifierLookupRejectsSuccessfulMalformedProtocol(string content)
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK, content);
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.LookupIdentifierAsync("9780000000098", 2, CancellationToken.None);

        Assert.AreEqual(IdentifierLookupOutcome.OperationalFailure, result.Outcome);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public async Task PolarisIdentifierLookupAcceptsApiDefinedDefinitiveEmptyResponses(int papiErrorCode)
    {
        var content = $$"""
            {"PAPIErrorCode":{{papiErrorCode}},"TotalRecordsFound":0,"BibSearchRows":[]}
            """;
        var handler = new StaticResponseHandler(HttpStatusCode.OK, content);
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.LookupIdentifierAsync("9780000000097", 2, CancellationToken.None);

        Assert.AreEqual(IdentifierLookupOutcome.NotFound, result.Outcome);
        Assert.AreEqual(3, handler.RequestCount);
    }

    [TestMethod]
    [DataRow(503, "{}")]
    [DataRow(429, "{}")]
    [DataRow(200, "{}")]
    [DataRow(200, "not-json")]
    [DataRow(200, "{\"PAPIErrorCode\":0}")]
    [DataRow(200, "{\"PAPIErrorCode\":-1,\"PatronID\":\"7001\"}")]
    [DataRow(200, "{\"PAPIErrorCode\":-1,\"PatronID\":7001,\"patronid\":7002}")]
    public async Task PolarisAuthenticationTreatsTransportAndProtocolFailuresAsOperational(
        int statusCode,
        string content)
    {
        var handler = new StaticResponseHandler((HttpStatusCode)statusCode, content);
        var provider = await CreatePolarisProviderAsync(handler);

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await provider.AuthenticateAsync("20000000000030", "1234", CancellationToken.None));
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    [DataRow(401, "{}")]
    [DataRow(200, "{\"PAPIErrorCode\":-1,\"ErrorMessage\":\"Invalid credentials\"}")]
    public async Task PolarisAuthenticationPreservesDefinitiveCredentialDenials(
        int statusCode,
        string content)
    {
        var handler = new StaticResponseHandler((HttpStatusCode)statusCode, content);
        var provider = await CreatePolarisProviderAsync(handler);

        await Assert.ThrowsExactlyAsync<PatronAuthenticationException>(async () =>
            await provider.AuthenticateAsync("20000000000031", "wrong", CancellationToken.None));
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    [DataRow(503, "{}")]
    [DataRow(429, "{}")]
    [DataRow(200, "not-json")]
    [DataRow(200, "{}")]
    public async Task PolarisAuthenticationPreservesBasicDataOperationalFailures(
        int statusCode,
        string basicDataContent)
    {
        var handler = new SequenceResponseHandler(
            (HttpStatusCode.OK,
             "{\"PAPIErrorCode\":0,\"AccessToken\":\"patron-token\",\"AccessSecret\":\"patron-secret\",\"PatronID\":123}"),
            ((HttpStatusCode)statusCode, basicDataContent));
        var provider = await CreatePolarisProviderAsync(handler);

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await provider.AuthenticateAsync("20000000000033", "1234", CancellationToken.None));
        Assert.AreEqual(2, handler.RequestCount);
    }

    [TestMethod]
    public async Task PolarisPickupUpdateRequiresExplicitSuccessfulProtocolResponse()
    {
        var missingCode = new ProtectedUpdateResponseHandler("{}");
        var missingCodeProvider = await CreatePolarisProviderAsync(missingCode);
        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await missingCodeProvider.UpdatePreferredPickupBranchAsync(
                "20000000000032",
                101,
                2, CancellationToken.None));

        var explicitSuccess = new ProtectedUpdateResponseHandler("{\"PAPIErrorCode\":0}");
        var successProvider = await CreatePolarisProviderAsync(explicitSuccess);
        await successProvider.UpdatePreferredPickupBranchAsync(
            "20000000000032",
            101,
            2, CancellationToken.None);

        Assert.AreEqual(1, missingCode.UpdateRequestCount);
        Assert.AreEqual(1, explicitSuccess.UpdateRequestCount);
        StringAssert.Contains(
            explicitSuccess.UpdateRequestPath,
            "/public/v1/1033/100/2/patron/20000000000032");
        using var body = JsonDocument.Parse(explicitSuccess.UpdateRequestBody);
        Assert.AreEqual(2, body.RootElement.GetProperty("LogonBranchId").GetInt32());
        Assert.AreEqual(42, body.RootElement.GetProperty("LogonUserId").GetInt32());
        Assert.AreEqual(99, body.RootElement.GetProperty("LogonWorkstationId").GetInt32());
        Assert.AreEqual(101, body.RootElement.GetProperty("RequestPickupBranchID").GetInt32());
    }

    [TestMethod]
    public async Task PolarisPickupBranchesMapTypedIdsAndOrganizationLabels()
    {
        var handler = new SequenceResponseHandler(
            (HttpStatusCode.OK, """{"PAPIErrorCode":0,"PickupBranchesRows":[{"ID":3},{"ID":4},{"ID":3}]}"""),
            (HttpStatusCode.OK, """
            {"PAPIErrorCode":2,"OrganizationsGetRows":[
              {"OrganizationID":3,"OrganizationCodeID":3,"DisplayName":"Zulu Branch"},
              {"OrganizationID":4,"OrganizationCodeID":3,"DisplayName":"Alpha Branch"}
            ]}
            """));
        var provider = await CreatePolarisProviderAsync(handler);
        var branches = await provider.GetPickupBranchesAsync(
            new PatronSnapshot(30, "20000000000033", "patron@example.org", "Test", "Patron",
                1, null, 77, 88, "Home Library", 4), 2, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { 4, 3 }, branches.Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "Alpha Branch", "Zulu Branch" }, branches.Select(item => item.Label).ToArray());
        Assert.AreEqual(2, handler.RequestCount);
        StringAssert.Contains(handler.RequestUris[0].AbsolutePath, "/public/v1/1033/100/77/pickupbranches");
        StringAssert.Contains(handler.RequestUris[1].AbsolutePath, "/organizations/");
    }

    [TestMethod]
    [DataRow("""{"PAPIErrorCode":0,"PickupBranchesRows":[{"OrgID":3}]}""")]
    [DataRow("""{"PAPIErrorCode":0,"PickupBranchesRows":[{"ID":0}]}""")]
    [DataRow("""{"PAPIErrorCode":0,"PickupBranchesRows":[{"ID":1}]}""")]
    [DataRow("""{"PAPIErrorCode":0,"PickupBranchesRows":[{"ID":-1}]}""")]
    [DataRow("""{"PAPIErrorCode":0,"PickupBranchesRows":[{"ID":2147483648}]}""")]
    [DataRow("""{"PAPIErrorCode":0,"PickupBranchesRows":[{"ID":3,"id":4}]}""")]
    [DataRow("""{"PAPIErrorCode":0,"PickupBranchesRows":{"PickupBranchRow":[{"ID":3}]}}""")]
    [DataRow("""{"PickupBranchesRows":[{"ID":3}]}""")]
    public async Task PolarisPickupBranchesRejectUnmodeledOrAmbiguousIdentity(string json)
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK, json);
        var provider = await CreatePolarisProviderAsync(handler);
        await Assert.ThrowsAsync<PolarisOperationalException>(() => provider.GetPickupBranchesAsync(
            new PatronSnapshot(30, "20000000000033", "patron@example.org", "Test", "Patron",
                1, null, 77, 88, "Home Library", 4), 2, CancellationToken.None));
        Assert.AreEqual(1, handler.RequestCount, "A malformed branch identity must not cause a context fallback.");
    }

    [TestMethod]
    public async Task PolarisPreferredPickupUsesRequestFieldOrRegisteredBranchWithoutRawAliasFallback()
    {
        var cases = new[]
        {
            (RequestField: "\"RequestPickupBranchID\":200,", Expected: (int?)200, State: PatronPickupPreferenceState.Current),
            (RequestField: string.Empty, Expected: (int?)300, State: PatronPickupPreferenceState.Absent),
            (RequestField: "\"RequestPickupBranchID\":0,", Expected: (int?)null, State: PatronPickupPreferenceState.ExplicitInvalid),
            (RequestField: "\"RequestPickupBranchID\":1,", Expected: (int?)null, State: PatronPickupPreferenceState.ExplicitInvalid)
        };

        foreach (var testCase in cases)
        {
            var handler = new SequenceResponseHandler(
                (HttpStatusCode.OK,
                 "{\"PAPIErrorCode\":0,\"AccessToken\":\"patron-token\",\"AccessSecret\":\"patron-secret\",\"PatronID\":123}"),
                (HttpStatusCode.OK,
                 "{\"PAPIErrorCode\":0,\"PatronBasicData\":{\"PatronID\":123,\"Barcode\":\"20000000000034\",\"PatronOrgID\":300," +
                 testCase.RequestField +
                 "\"CurrentPreferredPickupBranchID\":100,\"PreferredPickupBranchID\":400}}"),
                (HttpStatusCode.OK,
                 "{\"PAPIErrorCode\":1,\"OrganizationsGetRows\":[{\"OrganizationID\":300,\"OrganizationCodeID\":2,\"DisplayName\":\"Registered Branch\"}]}"));
            var provider = await CreatePolarisProviderAsync(handler);

            var patron = await provider.AuthenticateAsync(
                "20000000000034",
                "1234",
                CancellationToken.None);

            Assert.AreEqual(testCase.Expected, patron.PreferredPickupBranchId, testCase.RequestField);
            Assert.AreEqual(testCase.State, patron.EffectivePickupPreferenceState, testCase.RequestField);
            Assert.AreEqual(3, handler.RequestCount);
        }
    }

}
