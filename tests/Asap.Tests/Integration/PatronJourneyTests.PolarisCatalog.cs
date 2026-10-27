using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PolarisCatalogDuplicatePreviewMatchesAutoholdOptOutAndPurchasePolicy(bool autoHold)
    {
        await using var fixture = await SeedPolarisQueueAsync(autoHold: autoHold);
        await using var other = await SeedPolarisQueueAsync();
        await using (var context = await fixture.Contexts.CreateDbContextAsync())
        {
            var duplicate = await context.TitleRequests.SingleAsync(item => item.Id == other.Source.Id);
            duplicate.Barcode = fixture.Source.Barcode;
            duplicate.PatronIdSnapshot = 88001;
            duplicate.BibId = 9001;
            await context.SaveChangesAsync();
        }
        using var client = await PolarisQueueClientAsync(fixture.Actor);
        using var response = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup", new
        {
            requestId = fixture.Source.Id.ToString(), mode = "bib", bibId = 9001, includePatronHolds = false
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var actions = body.RootElement.GetProperty("queueActions");
        Assert.AreEqual(autoHold, actions.GetProperty("currentAutohold").GetBoolean());
        Assert.AreEqual(!autoHold, actions.GetProperty("useBib").GetProperty("allowed").GetBoolean());
        Assert.IsFalse(actions.GetProperty("additionalCopy").GetProperty("allowed").GetBoolean());
        Assert.AreEqual("duplicate_open_request", actions.GetProperty("additionalCopy").GetProperty("blockingReason").GetString());
    }

    [TestMethod]
    [DataRow(2, 3, true, "owned_by_library", true)]
    [DataRow(0, 4, true, "owned_by_consortium", true)]
    [DataRow(0, 0, false, "no_holdings", false)]
    [DataRow(1, 0, false, "not_holdable", false)]
    public async Task PolarisCatalogHoldingsUsesRequestLibraryAndProvidesActionEligibility(
        int mine, int other, bool holdable, string status, bool allowed)
    {
        await using var fixture = await SeedPolarisQueueAsync();
        fixture.Provider.SetBib(9001, 2, new(true, "Selected catalog title", Format: "Large print"),
            new(mine, other, mine + other, holdable, mine > 0 && holdable));
        using var client = await PolarisQueueClientAsync(fixture.Actor);
        using var response = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup", new
        {
            requestId = fixture.Source.Id.ToString(), mode = "bib", bibId = 9001, includePatronHolds = false
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.AreEqual(2, root.GetProperty("libraryOrgId").GetInt32());
        Assert.AreEqual(status, root.GetProperty("holdingsStatus").GetString());
        Assert.AreEqual(mine, root.GetProperty("holdingsSummary").GetProperty("myLibraryCount").GetInt32());
        Assert.AreEqual(mine + other, root.GetProperty("holdingsSummary").GetProperty("consortiumCount").GetInt32());
        Assert.IsFalse(root.GetProperty("holdingsUnavailable").GetBoolean());
        var actions = root.GetProperty("queueActions");
        Assert.AreEqual(StaffVersion.Encode(fixture.Source.RowVersion), actions.GetProperty("version").GetString());
        Assert.AreEqual(fixture.Source.Id.ToString(), actions.GetProperty("requestId").GetString());
        Assert.AreEqual(allowed, actions.GetProperty("useBib").GetProperty("allowed").GetBoolean());
        Assert.AreEqual(allowed, actions.GetProperty("additionalCopy").GetProperty("allowed").GetBoolean());
        Assert.IsTrue(actions.GetProperty("additionalCopy").GetProperty("enablesAutomaticHold").GetBoolean());
        Assert.IsFalse(actions.GetProperty("useBib").GetProperty("enablesAutomaticHold").GetBoolean());
        Assert.AreEqual("closed", actions.GetProperty("useBib").GetProperty("targetStatus").GetString());
        Assert.AreEqual(0, fixture.Provider.Calls.Count(call => call.Operation == TestingPolarisOperation.PatronHolds));
        Assert.IsTrue(fixture.Provider.Calls.Where(call => call.Operation is TestingPolarisOperation.BibValidation or TestingPolarisOperation.BibHoldings)
            .All(call => call.OrganizationId == 2));
        await AssertQueueUnchangedAsync(fixture);
    }

    [TestMethod]
    [DataRow("outage", "holdings_unavailable")]
    [DataRow("timeout", "holdings_timeout")]
    [DataRow("wrapped_timeout", "holdings_timeout")]
    public async Task PolarisCatalogHoldingsFailureNeverBecomesZeroOwnership(string failure, string expected)
    {
        await using var fixture = await SeedPolarisQueueAsync();
        fixture.Provider.SetFailure(new(TestingPolarisOperation.BibHoldings, 2, NativeId: 9001), failure switch
        {
            "timeout" => new OperationCanceledException("Provider timeout"),
            "wrapped_timeout" => new PolarisOperationalException("polaris_bib_holdings_failed", "Provider timeout", new OperationCanceledException()),
            _ => new PolarisOperationalException("test_outage", "Holdings offline")
        });
        using var client = await PolarisQueueClientAsync(fixture.Actor);
        using var response = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup", new
        {
            requestId = fixture.Source.Id.ToString(), mode = "bib", bibId = 9001, includePatronHolds = false
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(JsonValueKind.Null, body.RootElement.GetProperty("holdingsSummary").ValueKind);
        Assert.IsTrue(body.RootElement.GetProperty("holdingsUnavailable").GetBoolean());
        Assert.AreEqual("unavailable", body.RootElement.GetProperty("holdingsStatus").GetString());
        Assert.AreEqual(expected, body.RootElement.GetProperty("holdingsErrorCode").GetString());
        Assert.IsFalse(body.RootElement.GetProperty("queueActions").GetProperty("additionalCopy").GetProperty("allowed").GetBoolean());
        Assert.IsFalse(body.RootElement.GetProperty("queueActions").GetProperty("useBib").GetProperty("allowed").GetBoolean());
    }

    [TestMethod]
    public async Task PolarisCatalogRejectsForeignRequestScopeAndKeepsResearchWithoutRequestReadOnly()
    {
        await using var fixture = await SeedPolarisQueueAsync();
        using var client = await PolarisQueueClientAsync(fixture.Actor);
        using var mismatch = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup", new
        {
            requestId = fixture.Source.Id.ToString(), libraryOrgId = 3, mode = "bib", bibId = 9001
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, mismatch.StatusCode);
        using var foreign = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup", new
        {
            libraryOrgId = 3, mode = "bib", bibId = 9001
        });
        Assert.AreEqual(HttpStatusCode.Forbidden, foreign.StatusCode);
        using var research = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup", new
        {
            libraryOrgId = 2, mode = "bib", bibId = 9001, includePatronHolds = false
        });
        Assert.AreEqual(HttpStatusCode.OK, research.StatusCode);
        using var body = JsonDocument.Parse(await research.Content.ReadAsStringAsync());
        var action = body.RootElement.GetProperty("queueActions").GetProperty("useBib");
        Assert.IsFalse(action.GetProperty("allowed").GetBoolean());
        Assert.AreEqual("request_context_required", action.GetProperty("blockingReason").GetString());
        await AssertQueueUnchangedAsync(fixture);
    }

    [TestMethod]
    [DataRow("title")]
    [DataRow("author")]
    [DataRow("identifier")]
    [DataRow("title_author")]
    public async Task PolarisCatalogSearchEndpointKeepsTenResultCapAndProviderTotal(string mode)
    {
        await using var fixture = await SeedPolarisQueueAsync();
        fixture.Provider.SetBibSearch(mode, "Catalog query", "Title", "Author", 2,
            new StaffBibSearchResult(Enumerable.Range(1, 15).Select(id => new StaffBibSearchRow(
                9100 + id, $"Result {id}", "Author", "2026", "Book", "Identifier")).ToArray(), 2951));
        using var client = await PolarisQueueClientAsync(fixture.Actor);
        using var response = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup", new
        {
            requestId = fixture.Source.Id.ToString(), mode, query = "Catalog query", title = "Title", author = "Author"
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(10, body.RootElement.GetProperty("results").GetArrayLength());
        Assert.AreEqual(2951, body.RootElement.GetProperty("totalMatches").GetInt32());
        Assert.AreEqual(mode, body.RootElement.GetProperty("mode").GetString());
        Assert.AreEqual(2, body.RootElement.GetProperty("libraryOrgId").GetInt32());
    }

    [TestMethod]
    [DataRow("https://catalog.example.org/icons/{id2}/{SearchCode}.png", "https://catalog.example.org/icons/53/lp.png")]
    [DataRow("/icons/{MARCTypeOfMaterialID}.png", "/icons/53.png")]
    [DataRow("javascript:alert(1)", null)]
    [DataRow("data:image/svg+xml,<svg/>", null)]
    [DataRow("//foreign.example.org/icon.png", null)]
    [DataRow("https://user:password@example.org/icon.png", null)]
    [DataRow("https://catalog.example.org/{unknown}.png", null)]
    [DataRow("https://catalog.example.org/\\evil.png", null)]
    public async Task PolarisCatalogMaterialDefinitionsResolveDescriptionsAndValidateIcons(string pattern, string? expected)
    {
        var provider = await CreatePolarisProviderAsync(new CatalogMaterialHandler());
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contexts.CreateDbContextAsync();
        var settings = await context.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
        var original = settings.MaterialTypeIconUrlPattern;
        settings.MaterialTypeIconUrlPattern = pattern;
        await context.SaveChangesAsync();
        try
        {
            var result = await provider.SearchBibsAsync("title", "Title", "", "", 2, CancellationToken.None);
            var row = result.Results.Single(item => item.BibId == 9001);
            Assert.AreEqual("Large print", row.Format);
            Assert.AreEqual(53, row.MaterialTypeId);
            Assert.AreEqual("lp", row.MaterialTypeSearchCode);
            Assert.AreEqual(expected, row.FormatIconUrl);
            Assert.AreEqual("Book", result.Results.Single(item => item.BibId == 9002).Format);
            Assert.AreEqual("Unknown", result.Results.Single(item => item.BibId == 9003).Format);
            Assert.AreEqual(2951, result.TotalMatches);
        }
        finally
        {
            settings.MaterialTypeIconUrlPattern = original;
            await context.SaveChangesAsync();
        }
    }

    [TestMethod]
    [DataRow("outage")]
    [DataRow("timeout")]
    [DataRow("malformed")]
    public async Task PolarisCatalogMaterialReferenceFailuresKeepTrustworthyResultsAndBoundRetries(string failure)
    {
        var handler = new CatalogMaterialHandler(failure);
        var provider = await CreatePolarisProviderAsync(handler);
        var result = await provider.SearchBibsAsync("title", "Title", "", "", 2, CancellationToken.None);
        var again = await provider.SearchBibsAsync("title", "Title", "", "", 2, CancellationToken.None);
        Assert.AreEqual(3, result.Results.Count);
        Assert.AreEqual(2951, again.TotalMatches);
        Assert.AreEqual("Unknown", result.Results.Single(item => item.BibId == 9001).Format);
        Assert.IsNull(result.Results[0].FormatIconUrl);
        Assert.AreEqual(1, handler.ReferenceCalls);
    }

    [TestMethod]
    public async Task PolarisCatalogMaterialCacheIsSharedBySearchesButHonorsCancellation()
    {
        var handler = new CatalogMaterialHandler();
        var provider = await CreatePolarisProviderAsync(handler);
        await provider.SearchBibsAsync("title", "Title", "", "", 2, CancellationToken.None);
        await provider.SearchBibsAsync("author", "Author", "", "", 2, CancellationToken.None);
        Assert.AreEqual(1, handler.ReferenceCalls);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            provider.SearchBibsAsync("title", "Title", "", "", 2, canceled.Token));
        Assert.AreEqual(1, handler.ReferenceCalls);
    }

    private sealed class CatalogMaterialHandler(string? failure = null) : HttpMessageHandler
    {
        public int ReferenceCalls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isReference = request.RequestUri!.AbsolutePath.EndsWith("/marctypeofmaterials", StringComparison.Ordinal);
            if (isReference)
            {
                ReferenceCalls++;
                if (failure == "timeout")
                {
                    throw new OperationCanceledException("Reference timeout");
                }
            }
            else
            {
                StringAssert.Contains(request.RequestUri.AbsolutePath, "/search/bibs/");
            }
            var json = isReference ? failure == "malformed" ? "{}" :
                """{"PAPIErrorCode":0,"MARCTypeOfMaterialsRows":[{"MARCTypeOfMaterialID":53,"Description":"Large print","SearchCode":"lp"},{"MARCTypeOfMaterialID":1,"Description":"Book","SearchCode":"book"}]}""" :
                """{"PAPIErrorCode":0,"TotalRecordsFound":2951,"BibSearchRows":[{"ControlNumber":9001,"Title":"Title","TypeOfMaterial":"33181604","PrimaryTypeOfMaterial":53},{"ControlNumber":9002,"Title":"Other title","TypeOfMaterial":"01"},{"ControlNumber":9003,"Title":"Unknown format","TypeOfMaterial":"999"}]}""";
            return Task.FromResult(new HttpResponseMessage(isReference && failure == "outage" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
