using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Features.Staff.Compatibility;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Security;
using Asap.Web.Infrastructure.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow("identifier", "keyword/ISBN", "9781234567890")]
    [DataRow("title", "keyword/TI", "Port title")]
    [DataRow("author", "keyword/AU", "Port author")]
    [DataRow("title_author", "boolean", "TI=\"Port title\" AND AU=\"Port author\"")]
    public async Task StaffPortBibSearchPreservesLegacyQueriesAndBoundedResponse(string mode, string path, string query)
    {
        using var startup = factory!.CreateClient();
        var rows = Enumerable.Range(1, 15).Select(id => new
        {
            ControlNumber = id, DisplayTitle = "Port title", Author = "Port author",
            PublicationDate = "2020", MaterialTypeDescription = "Book", ISBN = "9781234567890",
            PrimaryTypeOfMaterial = "1"
        });
        var handler = new StaffSearchResponseHandler(_ => JsonSerializer.Serialize(new
        {
            PAPIErrorCode = 0, TotalRecordsFound = 40, BibSearchRows = rows
        }));
        var provider = await CreatePolarisProviderAsync(handler);
        await using var app = WithStaffPortProviders(provider);
        using var client = await StaffPortClientAsync(app);
        using var response = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup", new
        {
            mode, query = mode == "identifier" ? "978-1-234567-89-0" : "Port title",
            title = "Port title", author = "Port author", requestType = "title_request", requestId = "legacy-id"
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.AreEqual("found", root.GetProperty("status").GetString());
        Assert.AreEqual(40, root.GetProperty("totalMatches").GetInt32());
        Assert.IsTrue(root.GetProperty("multipleMatches").GetBoolean());
        Assert.AreEqual(10, root.GetProperty("results").GetArrayLength());
        var row = root.GetProperty("results")[0];
        foreach (var field in new[] { "bibId", "title", "author", "publication", "format", "identifier" })
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(row.GetProperty(field).GetString()), field);
        }
        Assert.HasCount(1, handler.Requests);
        StringAssert.EndsWith(handler.Requests[0].AbsolutePath, path);
        var parameters = QueryHelpers.ParseQuery(handler.Requests[0].Query);
        Assert.AreEqual(query, parameters["q"].ToString());
        Assert.AreEqual("10", parameters["bibsperpage"].ToString());
    }

    [TestMethod]
    public async Task StaffPortBibSearchExposesConfiguredLegacyFormatIconMetadata()
    {
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var settingsContext = await contextFactory.CreateDbContextAsync();
        var settings = await settingsContext.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
        var originalPattern = settings.MaterialTypeIconUrlPattern;
        try
        {
            settings.MaterialTypeIconUrlPattern = "https://icons.example.org/{MARCTypeOfMaterialID2}/{SearchCode}.svg";
            await settingsContext.SaveChangesAsync();
            var handler = new StaffSearchResponseHandler(_ => """
                {"PAPIErrorCode":0,"TotalRecordsFound":1,"BibSearchRows":[{
                  "ControlNumber":9001,"DisplayTitle":"Icon title","Author":"Icon author",
                  "PublicationDate":"2020","MaterialTypeDescription":"Book","ISBN":"9781234567890",
                  "PrimaryTypeOfMaterial":"1","SearchCode":"PRINT BOOK","Description":"123 pages"}]}
                """);
            var provider = await CreatePolarisProviderAsync(handler);
            await using var app = WithStaffPortProviders(provider);
            using var client = await StaffPortClientAsync(app);

            using var response = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup", new
            {
                mode = "title", query = "Icon title", title = "Icon title"
            });

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var row = document.RootElement.GetProperty("results")[0];
            Assert.AreEqual("https://icons.example.org/01/PRINT%20BOOK.svg",
                row.GetProperty("formatIconUrl").GetString());
            Assert.AreEqual("1", row.GetProperty("primaryTomId").GetString());
            Assert.AreEqual("Book", row.GetProperty("formatIconAlt").GetString());
            Assert.AreEqual("PRINT BOOK", row.GetProperty("materialTypeSearchCode").GetString());
            Assert.AreEqual("123 pages", row.GetProperty("physicalDescription").GetString());
        }
        finally
        {
            settings.MaterialTypeIconUrlPattern = originalPattern;
            await settingsContext.SaveChangesAsync();
        }
    }

    [TestMethod]
    public async Task StaffPortExactBibLookupIncludesLegacyMetadata()
    {
        using var startup = factory!.CreateClient();
        var handler = new StaffSearchResponseHandler(uri => uri.AbsolutePath.EndsWith("holdings", StringComparison.Ordinal)
            ? """
              {"PAPIErrorCode":0,"BibHoldingsGetRows":[
                {"LocationID":"101","Holdable":"1","ItemsTotal":"50"},
                {"LocationID":"101","Holdable":"false"},{"LocationID":"102","Holdable":"yes"}]}
              """
            : uri.AbsolutePath.Contains("authenticator/staff", StringComparison.OrdinalIgnoreCase)
                ? "{\"PAPIErrorCode\":0,\"AccessToken\":\"protected-token\",\"AccessSecret\":\"secret\",\"AuthExpDate\":\"2035-01-01T00:00:00Z\"}"
            : uri.AbsolutePath.Contains("holdrequests", StringComparison.OrdinalIgnoreCase)
                ? """
                  {"PAPIErrorCode":0,"PatronHoldRequestsGetRows":[
                    {"HoldRequestID":1,"BibID":123,"StatusDescription":"Active"},
                    {"HoldRequestID":2,"BibID":124,"StatusDescription":"Cancelled"}]}
                  """
            : uri.AbsolutePath.Contains("organizations", StringComparison.OrdinalIgnoreCase)
                ? """
                  {"PAPIErrorCode":0,"OrganizationsGetRows":[
                    {"OrganizationID":2,"OrganizationCodeID":2},
                    {"OrganizationID":101,"OrganizationCodeID":3,"ParentOrganizationID":2},
                    {"OrganizationID":3,"OrganizationCodeID":2},
                    {"OrganizationID":102,"OrganizationCodeID":3,"ParentOrganizationID":3}]}
                  """
                : """
            {"PAPIErrorCode":0,"BibGetRows":[
              {"ElementID":35,"Value":"Exact title"},{"ElementID":18,"Value":"Exact author"},
              {"ElementID":2,"Value":"Publisher, 2021"},{"ElementID":17,"Value":"Book"},
              {"ElementID":6,"Value":"9781234567890"}]}
            """);
        var provider = await CreatePolarisProviderAsync(handler);
        await using var app = WithStaffPortProviders(provider, new StaffPortPatronProvider());
        using var client = await StaffPortClientAsync(app);
        await ConfigureStaffPortLibraryAsync(client, false);
        using var response = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup", new
        {
            bibId = "123", libraryOrgId = "91632"
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("123", document.RootElement.GetProperty("bibId").GetString());
        Assert.AreEqual("Exact title", document.RootElement.GetProperty("title").GetString());
        Assert.AreEqual("Exact author", document.RootElement.GetProperty("author").GetString());
        Assert.AreEqual("Book", document.RootElement.GetProperty("format").GetString());
        Assert.AreEqual("9781234567890", document.RootElement.GetProperty("identifier").GetString());
        Assert.AreEqual("Publisher, 2021", document.RootElement.GetProperty("publisher").GetString());
        var summary = document.RootElement.GetProperty("holdingsSummary");
        Assert.AreEqual(3, summary.GetProperty("consortiumCount").GetInt32());
        Assert.IsTrue(summary.GetProperty("isHoldable").GetBoolean());
        using var withPatron = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup", new
        {
            bibId = "123", barcode = "port-local", libraryOrgId = "91632"
        });
        Assert.AreEqual(HttpStatusCode.OK, withPatron.StatusCode, await withPatron.Content.ReadAsStringAsync());
        using var checkedPatron = JsonDocument.Parse(await withPatron.Content.ReadAsStringAsync());
        Assert.AreEqual(29, checkedPatron.RootElement.GetProperty("patronHoldCheck").GetProperty("statusValue").GetInt32());
        using var ineligibleCode = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup", new
        {
            bibId = "123", barcode = "port-restricted", libraryOrgId = "91632"
        });
        Assert.AreEqual(HttpStatusCode.OK, ineligibleCode.StatusCode,
            await ineligibleCode.Content.ReadAsStringAsync());
        using var denied = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup", new
        {
            bibId = "123", barcode = "port-foreign", libraryOrgId = "91632"
        });
        Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode, await denied.Content.ReadAsStringAsync());
        var local = await provider.GetBibHoldingsAsync(123, 2, CancellationToken.None);
        Assert.AreEqual(new StaffBibHoldingsSummary(2, 1, 3, true, true), local);
        var empty = await CreatePolarisProviderAsync(new StaffSearchResponseHandler(_ => "{\"PAPIErrorCode\":-1}"));
        Assert.AreEqual(new StaffBibHoldingsSummary(0, 0, 0, false, false),
            await empty.GetBibHoldingsAsync(123, 2, CancellationToken.None));
        var failed = await CreatePolarisProviderAsync(new StaffSearchResponseHandler(_ => "{\"PAPIErrorCode\":-999}"));
        await Assert.ThrowsAsync<PolarisOperationalException>(() => failed.GetBibHoldingsAsync(123, 2, CancellationToken.None));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.GetBibHoldingsAsync(123, 2, canceled.Token));
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task StaffPortExactBibLookupKeepsOptionalPatronEnrichmentNonFatal(
        bool failPatronLookup,
        bool failHoldLookup)
    {
        var provider = new StaffPortPatronProvider
        {
            FailPatronLookup = failPatronLookup,
            FailHoldLookup = failHoldLookup
        };
        await using var app = WithStaffPortProviders(provider, provider);
        using var client = await StaffPortClientAsync(app);
        await ConfigureStaffPortLibraryAsync(client, false);

        using var response = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup", new
        {
            bibId = "123", barcode = "port-local", libraryOrgId = "91632"
        });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("Catalog title 123", document.RootElement.GetProperty("title").GetString());
        Assert.AreEqual(JsonValueKind.Null, document.RootElement.GetProperty("patronHoldCheck").ValueKind);
        Assert.AreEqual(91632, provider.HoldingsOrganizationIds.Single());
    }

    [TestMethod]
    public async Task StaffPortExactBibLookupUsesOrdinaryStaffScopeAndRejectsSpoofing()
    {
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        await UpsertTestOrganizationAsync(91632, "Staff port tests", "SPT");
        var staffRow = await CreateCorrectiveStaffAsync(superAdmin, "staff", 91632);
        var actor = await ReadCorrectiveStaffAsync(staffRow);
        var provider = new StaffPortPatronProvider();
        await using var app = WithStaffPortProviders(provider, provider);
        using var client = await StaffPortClientAsync(app, actor);
        try
        {
            using var ownScope = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup", new { bibId = "123" });
            Assert.AreEqual(HttpStatusCode.OK, ownScope.StatusCode, await ownScope.Content.ReadAsStringAsync());
            Assert.AreEqual(91632, provider.HoldingsOrganizationIds.Single());

            using var spoofed = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup", new
            {
                bibId = "123", libraryOrgId = "3"
            });
            Assert.AreEqual(HttpStatusCode.Forbidden, spoofed.StatusCode, await spoofed.Content.ReadAsStringAsync());
            Assert.HasCount(1, provider.HoldingsOrganizationIds);
        }
        finally
        {
            await DeactivateCorrectiveStaffAsync(staffRow.Id);
        }
    }

    [TestMethod]
    public async Task StaffPortExactBibLookupUsesSuperAdminServicingLibraryForHoldingsAndPatronScope()
    {
        var provider = new StaffPortPatronProvider();
        await using var app = WithStaffPortProviders(provider, provider);
        using var client = await StaffPortClientAsync(app);
        await ConfigureStaffPortLibraryAsync(client, false, 91632);
        await ConfigureStaffPortLibraryAsync(client, true, 3);

        using var holdings = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup", new
        {
            bibId = "123", libraryOrgId = "91632"
        });
        Assert.AreEqual(HttpStatusCode.OK, holdings.StatusCode, await holdings.Content.ReadAsStringAsync());
        Assert.AreEqual(91632, provider.HoldingsOrganizationIds.Single());

        using var wrongLibraryPatron = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup", new
        {
            bibId = "123", barcode = "port-foreign", libraryOrgId = "91632"
        });
        Assert.AreEqual(HttpStatusCode.Forbidden, wrongLibraryPatron.StatusCode,
            await wrongLibraryPatron.Content.ReadAsStringAsync());
        Assert.AreEqual(91632, provider.HoldingsOrganizationIds.Last());
    }

    [TestMethod]
    public async Task StaffPortExactBibLookupPrefersExistingRequestLibraryScope()
    {
        var provider = new StaffPortPatronProvider();
        await using var app = WithStaffPortProviders(provider, provider);
        using var client = await StaffPortClientAsync(app);
        var requestId = await SeedPendingHoldRequestAsync(
            "Existing request BIB scope",
            "20000000003991",
            "123");

        using var response = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup", new
        {
            bibId = "123", requestType = "title_request", requestId = requestId.ToString(), libraryOrgId = "91632"
        });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual(2, provider.HoldingsOrganizationIds.Single());
    }

    [TestMethod]
    public async Task StaffPortExactBibLookupUsesTypeQualifiedScopeWhenRequestIdsCollide()
    {
        using var configurationClient = await StaffPortClientAsync(factory!);
        await ConfigureStaffPortLibraryAsync(configurationClient, false);
        await UpsertTestOrganizationAsync(91632, "Staff port collision tests", "SPC");
        var requestId = await SeedCollidingBibLookupRequestsAsync();
        var provider = new StaffPortPatronProvider();
        await using var app = WithStaffPortProviders(provider, provider);
        using var client = await StaffPortClientAsync(app);

        using var titleResponse = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup", new
        {
            bibId = "123", requestType = "title_request", requestId = requestId.ToString(), libraryOrgId = "91632"
        });
        Assert.AreEqual(HttpStatusCode.OK, titleResponse.StatusCode, await titleResponse.Content.ReadAsStringAsync());
        Assert.AreEqual(2, provider.HoldingsOrganizationIds.Last());

        using var copyResponse = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup", new
        {
            bibId = "123", requestType = "additional_copy", requestId = requestId.ToString(), libraryOrgId = "2"
        });
        Assert.AreEqual(HttpStatusCode.OK, copyResponse.StatusCode, await copyResponse.Content.ReadAsStringAsync());
        Assert.AreEqual(91632, provider.HoldingsOrganizationIds.Last());

        using var mismatched = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup", new
        {
            bibId = "123", requestType = "title_request", requestId = (requestId + 1).ToString()
        });
        Assert.AreEqual(HttpStatusCode.NotFound, mismatched.StatusCode, await mismatched.Content.ReadAsStringAsync());
        Assert.HasCount(2, provider.HoldingsOrganizationIds);

        using var additionalOnly = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup", new
        {
            bibId = "123", requestType = "additional_copy", requestId = (requestId + 1).ToString()
        });
        Assert.AreEqual(HttpStatusCode.OK, additionalOnly.StatusCode, await additionalOnly.Content.ReadAsStringAsync());
        Assert.AreEqual(91632, provider.HoldingsOrganizationIds.Last());

        using var wrongEntityType = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup", new
        {
            bibId = "123", requestType = "additional_copy", requestId = (requestId + 2).ToString()
        });
        Assert.AreEqual(HttpStatusCode.NotFound, wrongEntityType.StatusCode, await wrongEntityType.Content.ReadAsStringAsync());
        Assert.HasCount(3, provider.HoldingsOrganizationIds);

        using var missingType = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup", new
        {
            bibId = "123", requestId = requestId.ToString()
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, missingType.StatusCode, await missingType.Content.ReadAsStringAsync());
        Assert.HasCount(3, provider.HoldingsOrganizationIds);

        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var staffRow = await CreateCorrectiveStaffAsync(superAdmin, "staff", 91632);
        try
        {
            var actor = await ReadCorrectiveStaffAsync(staffRow);
            using var staffClient = await StaffPortClientAsync(app, actor);
            using var ownCopy = await staffClient.PostAsJsonAsync("/api/asap/staff/bib-lookup", new
            {
                bibId = "123", requestType = "additional_copy", requestId = requestId.ToString()
            });
            Assert.AreEqual(HttpStatusCode.OK, ownCopy.StatusCode, await ownCopy.Content.ReadAsStringAsync());
            Assert.AreEqual(91632, provider.HoldingsOrganizationIds.Last());

            using var foreignTitle = await staffClient.PostAsJsonAsync("/api/asap/staff/bib-lookup", new
            {
                bibId = "123", requestType = "title_request", requestId = requestId.ToString()
            });
            Assert.AreEqual(HttpStatusCode.NotFound, foreignTitle.StatusCode, await foreignTitle.Content.ReadAsStringAsync());
            Assert.HasCount(4, provider.HoldingsOrganizationIds);
        }
        finally
        {
            await DeactivateCorrectiveStaffAsync(staffRow.Id);
        }
    }

    [TestMethod]
    public async Task StaffPortBibFallbacksFilterDigitalMaterialsAndReportFailures()
    {
        using var startup = factory!.CreateClient();
        var handler = new StaffSearchResponseHandler(uri => uri.AbsolutePath.EndsWith("boolean", StringComparison.Ordinal)
            ? "{\"PAPIErrorCode\":-1}"
            : """
              {"PAPIErrorCode":0,"TotalRecordsFound":3,"BibSearchRows":[
                {"ControlNumber":1,"Title":"Port title","Author":"Port author","PrimaryTypeOfMaterial":36},
                {"ControlNumber":2,"Title":"Port title","Author":"Other"},
                {"ControlNumber":3,"Title":"Port title","Author":"Port author"}]}
              """);
        var provider = await CreatePolarisProviderAsync(handler);
        var found = await provider.SearchBibsAsync("title_author", "Port title", "Port title", "Port author", CancellationToken.None);
        Assert.AreEqual("3", found.Results.Single().BibId);
        Assert.HasCount(2, handler.Requests);
        var noResults = await CreatePolarisProviderAsync(new StaffSearchResponseHandler(_ => "{\"PAPIErrorCode\":-1}"));
        Assert.HasCount(0, (await noResults.SearchBibsAsync("identifier", "123", "", "", CancellationToken.None)).Results);
        var failed = await CreatePolarisProviderAsync(new StaffSearchResponseHandler(_ => "{\"PAPIErrorCode\":-999}"));
        await Assert.ThrowsAsync<PolarisOperationalException>(() => failed.SearchBibsAsync("title", "Port", "", "", CancellationToken.None));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.SearchBibsAsync("title", "Port", "", "", canceled.Token));
    }

    [TestMethod]
    public async Task StaffPortPatronProviderUsesLegacyNameQueryAndResolvesActualScope()
    {
        using var startup = factory!.CreateClient();
        var handler = new StaffSearchResponseHandler(uri =>
        {
            var path = uri.AbsolutePath;
            if (path.Contains("authenticator/staff", StringComparison.OrdinalIgnoreCase))
            {
                return "{\"PAPIErrorCode\":0,\"AccessToken\":\"protected-token\",\"AccessSecret\":\"secret\",\"AuthExpDate\":\"2035-01-01T00:00:00Z\"}";
            }
            if (path.Contains("search/patrons", StringComparison.OrdinalIgnoreCase))
            {
                return "{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"PatronSearchRows\":[{\"PatronID\":123,\"Barcode\":\"port-barcode\"}]}";
            }
            if (path.Contains("organizations", StringComparison.OrdinalIgnoreCase))
            {
                return "{\"PAPIErrorCode\":1,\"OrganizationsGetRows\":[{\"OrganizationID\":300,\"OrganizationCodeID\":2,\"DisplayName\":\"Actual Library\"}]}";
            }
            return "{\"PAPIErrorCode\":0,\"PatronBasicData\":{\"PatronID\":123,\"Barcode\":\"port-barcode\",\"NameFirst\":\"Pat\",\"NameLast\":\"Reader\",\"PatronOrgID\":300,\"PatronCodeID\":14}}";
        });
        var provider = await CreatePolarisProviderAsync(handler);
        var patrons = await provider.SearchPatronsAsync("Pat Reader", CancellationToken.None);
        Assert.AreEqual(300, patrons.Single().HomeLibraryOrganizationId);
        Assert.AreEqual("14", patrons.Single().PatronCodeId);
        var search = handler.Requests.Single(uri => uri.AbsolutePath.Contains("search/patrons", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(search.AbsolutePath, "protected-token");
        var parameters = QueryHelpers.ParseQuery(search.Query);
        Assert.AreEqual("PATNF=\"Pat Reader\"", parameters["q"].ToString());
        Assert.AreEqual("PATNF", parameters["sortby"].ToString());
        Assert.AreEqual("10", parameters["patronsperpage"].ToString());
        var missing = await CreatePolarisProviderAsync(new StaffSearchResponseHandler(uri =>
            uri.AbsolutePath.Contains("authenticator/staff", StringComparison.OrdinalIgnoreCase)
                ? "{\"PAPIErrorCode\":0,\"AccessToken\":\"token\",\"AccessSecret\":\"secret\",\"AuthExpDate\":\"2035-01-01T00:00:00Z\"}"
                : "{\"PAPIErrorCode\":-1}"));
        Assert.HasCount(0, await missing.SearchPatronsAsync("Missing Reader", CancellationToken.None));
        var invalid = await CreatePolarisProviderAsync(new StaffSearchResponseHandler(uri =>
            uri.AbsolutePath.Contains("authenticator/staff", StringComparison.OrdinalIgnoreCase)
                ? "{\"PAPIErrorCode\":0,\"AccessToken\":\"token\",\"AccessSecret\":\"secret\",\"AuthExpDate\":\"2035-01-01T00:00:00Z\"}"
                : "{\"PatronSearchRows\":[]}"));
        await Assert.ThrowsAsync<PolarisOperationalException>(() => invalid.SearchPatronsAsync("Reader", CancellationToken.None));
    }

    [TestMethod]
    [DataRow("port-local", "selected", 1)]
    [DataRow("Single Reader", "selected", 1)]
    [DataRow("Reader", "multiple", 2)]
    [DataRow("Many Readers", "multiple", 10)]
    [DataRow("Missing Reader", "not_found", 0)]
    public async Task StaffPortPatronLookupPreservesSelectionAndFiltersCandidates(string query, string status, int count)
    {
        var provider = new StaffPortPatronProvider();
        await using var app = WithStaffPortProviders(provider, provider);
        using var client = await StaffPortClientAsync(app);
        await ConfigureStaffPortLibraryAsync(client, false);
        using var response = await client.PostAsJsonAsync("/api/asap/staff/patron-lookup", new { query, libraryOrgId = "91632" });
        Assert.AreEqual(count == 0 ? HttpStatusCode.NotFound : HttpStatusCode.OK, response.StatusCode,
            await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(status, document.RootElement.GetProperty("status").GetString());
        if (status == "multiple")
        {
            Assert.AreEqual(count, document.RootElement.GetProperty("totalMatches").GetInt32());
            foreach (var candidate in document.RootElement.GetProperty("results").EnumerateArray())
            {
                Assert.AreEqual(91632, candidate.GetProperty("libraryOrgId").GetInt32());
            }
        }
        else if (count == 1)
        {
            Assert.AreEqual("port-local", document.RootElement.GetProperty("barcode").GetString());
            Assert.AreEqual(2, document.RootElement.GetProperty("pickupBranches").GetArrayLength());
        }
        if (query == "Reader")
        {
            Assert.HasCount(1, provider.DirectLookups);
            Assert.AreEqual("Reader", provider.DirectLookups[0]);
        }
        Assert.DoesNotContain("port-foreign", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("port-restricted", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [DataRow("port-local", false, true)]
    [DataRow("port-foreign", false, false)]
    [DataRow("port-foreign", true, true)]
    [DataRow("port-restricted", true, false)]
    public async Task StaffPortDirectSubmissionRevalidatesEligibilityBeforePickupMutation(string barcode, bool crossLibrary, bool allowed)
    {
        var provider = new StaffPortPatronProvider();
        await using var app = WithStaffPortProviders(provider, provider);
        using var client = await StaffPortClientAsync(app);
        await ConfigureStaffPortLibraryAsync(client, crossLibrary);
        using var response = await client.PostAsJsonAsync("/api/asap/staff/suggestions", StaffPortSuggestion(barcode));
        Assert.AreEqual(allowed ? HttpStatusCode.Created : HttpStatusCode.Forbidden, response.StatusCode,
            await response.Content.ReadAsStringAsync());
        Assert.AreEqual(allowed ? 1 : 0, provider.PickupUpdates);
        if (!allowed)
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual(barcode == "port-restricted" ? "patron_code_forbidden" : "patron_library_forbidden",
                document.RootElement.GetProperty("code").GetString());
        }
    }

    [TestMethod]
    public async Task StaffPortBypassesOnlyPublicCountLimit()
    {
        var provider = new StaffPortPatronProvider();
        await using var app = WithStaffPortProviders(provider, provider);
        using var client = await StaffPortClientAsync(app);
        await ConfigureStaffPortLibraryAsync(client, false);
        var barcode = "limit-" + Guid.NewGuid().ToString("N");
        using var login = await client.PostAsJsonAsync("/api/asap/patron/login", new { barcode, pin = "1234", libraryOrgId = 91632 });
        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());
        using var loginDocument = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        using var publicClient = app.CreateClient();
        publicClient.DefaultRequestHeaders.Authorization = new("Bearer", loginDocument.RootElement.GetProperty("token").GetString());
        var title = "Public port limit " + Guid.NewGuid().ToString("N");
        using var first = await publicClient.PostAsJsonAsync("/api/asap/patron/suggestions", new
        {
            format = "book", title, author = "Port author", publication = "Coming soon", preferredPickupBranchId = 101
        });
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, await first.Content.ReadAsStringAsync());
        using var limited = await publicClient.PostAsJsonAsync("/api/asap/patron/suggestions", new
        {
            format = "book", title = title + " second", author = "Port author", publication = "Coming soon", preferredPickupBranchId = 101
        });
        Assert.AreEqual(HttpStatusCode.NotAcceptable, limited.StatusCode, await limited.Content.ReadAsStringAsync());
        var staffInput = StaffPortSuggestion(barcode);
        using var staff = await client.PostAsJsonAsync("/api/asap/staff/suggestions", staffInput);
        Assert.AreEqual(HttpStatusCode.Created, staff.StatusCode, await staff.Content.ReadAsStringAsync());
        using var duplicate = await client.PostAsJsonAsync("/api/asap/staff/suggestions", staffInput);
        Assert.AreEqual(HttpStatusCode.Conflict, duplicate.StatusCode, await duplicate.Content.ReadAsStringAsync());
        using var invalid = await client.PostAsJsonAsync("/api/asap/staff/suggestions", staffInput with { Format = "not-a-format" });
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var pickup = await client.PostAsJsonAsync("/api/asap/staff/suggestions", staffInput with { PreferredPickupBranchId = "999" });
        Assert.AreEqual(HttpStatusCode.BadRequest, pickup.StatusCode);
    }

    [TestMethod]
    public async Task StaffPortPostmarkSettingsPreserveSecretsAndReportEffectiveReadiness()
    {
        using var client = await StaffPortClientAsync(factory!);
        const string orgId = "91631";
        await UpsertTestOrganizationAsync(91631, "Postmark port", "PMP");
        var protector = factory!.Services.GetRequiredService<IntegrationCredentialProtector>();
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        foreach (var (token, clear, expected) in new[]
                 { ("first-private-token", false, "first-private-token"), ("", false, "first-private-token"),
                   ("replacement-private-token", false, "replacement-private-token"), ("", true, (string?)null) })
        {
            using var before = await ReadSettingsDocumentAsync(client, orgId);
            using var saved = await SaveSettingsDocumentAsync(client, before.RootElement, orgId, new Dictionary<string, object?>
            {
                ["emails"] = new { fromAddress = "port@example.org", fromName = "Port sender", postmarkToken = token, clearPostmarkToken = clear },
                ["smtp"] = new { host = "obsolete.invalid", password = "obsolete-secret" }
            });
            using var loaded = await ReadSettingsDocumentAsync(client, orgId);
            var email = loaded.RootElement.GetProperty("stored").GetProperty("libraryOverride").GetProperty("email");
            Assert.AreEqual(expected is not null, email.GetProperty("hasPostmarkToken").GetBoolean());
            Assert.AreEqual("port@example.org", email.GetProperty("fromAddress").GetString());
            Assert.AreEqual("Port sender", email.GetProperty("fromName").GetString());
            Assert.DoesNotContain("private-token", loaded.RootElement.GetRawText());
            Assert.DoesNotContain("obsolete-secret", loaded.RootElement.GetRawText());
            await using var context = await contexts.CreateDbContextAsync();
            var stored = await context.EmailSettings.SingleAsync(row => row.OrganizationId == 91631);
            Assert.AreEqual(expected, stored.ProtectedServerToken is null ? null : protector.Unprotect(stored.ProtectedServerToken));
        }
        using var current = await ReadSettingsDocumentAsync(client, orgId);
        using var blankSender = await SaveSettingsDocumentAsync(client, current.RootElement, orgId, new Dictionary<string, object?>
        {
            ["emails"] = new { fromAddress = "", postmarkToken = "configured-token" }
        });
        await using var readinessContext = await contexts.CreateDbContextAsync();
        var systemEmail = await readinessContext.EmailSettings.SingleAsync(row => row.OrganizationId == 1);
        var originalSender = systemEmail.FromAddress;
        var originalToken = systemEmail.ProtectedServerToken;
        try
        {
            // A blank library sender inherits. Remove the system sender to test truly missing configuration.
            systemEmail.FromAddress = null;
            systemEmail.ProtectedServerToken = null;
            await readinessContext.SaveChangesAsync();
            using var status = await client.GetAsync($"/api/asap/staff/email-status?orgId={orgId}");
            using var statusDocument = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
            Assert.IsFalse(statusDocument.RootElement.GetProperty("enabled").GetBoolean());
            Assert.IsTrue(statusDocument.RootElement.GetProperty("hasPostmarkToken").GetBoolean());
            using var systemStatus = await client.GetAsync("/api/asap/staff/email-status?orgId=system");
            using var systemDocument = JsonDocument.Parse(await systemStatus.Content.ReadAsStringAsync());
            Assert.IsFalse(systemDocument.RootElement.GetProperty("enabled").GetBoolean());
            Assert.IsFalse(systemDocument.RootElement.GetProperty("hasPostmarkToken").GetBoolean());
        }
        finally
        {
            systemEmail.FromAddress = originalSender;
            systemEmail.ProtectedServerToken = originalToken;
            await readinessContext.SaveChangesAsync();
        }
    }

    [TestMethod]
    public async Task StaffPortPolarisSettingsRoundTripCanonicalValuesAndWriteOnlySecrets()
    {
        using var client = await StaffPortClientAsync(factory!);
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var protector = factory.Services.GetRequiredService<IntegrationCredentialProtector>();
        string? originalHost;
        string? originalAccessId;
        string? originalApiKey;
        string? originalStaffDomain;
        string? originalAdminUser;
        string? originalAdminPassword;
        int? originalWorkstationId;
        int? originalSystemUserId;
        int? originalRequestingOrganizationId;
        int? originalPickupOrganizationId;
        string? originalLoginPrompt;

        await using (var seedContext = await contexts.CreateDbContextAsync())
        {
            var polaris = await seedContext.PolarisSettings.SingleAsync(row => row.OrganizationId == 1);
            var patron = await seedContext.PatronSettings.SingleAsync(row => row.OrganizationId == 1);
            originalHost = polaris.Host;
            originalAccessId = polaris.AccessId;
            originalApiKey = polaris.ProtectedApiKey;
            originalStaffDomain = polaris.StaffDomain;
            originalAdminUser = polaris.AdminUser;
            originalAdminPassword = polaris.ProtectedAdminPassword;
            originalWorkstationId = polaris.WorkstationId;
            originalSystemUserId = polaris.SystemPolarisUserId;
            originalRequestingOrganizationId = polaris.OrganizationIdForRequests;
            originalPickupOrganizationId = polaris.PickupOrganizationId;
            originalLoginPrompt = patron.LoginPrompt;

            polaris.Host = "https://polaris-settings.invalid";
            polaris.AccessId = "settings-access";
            polaris.ProtectedApiKey = protector.Protect("settings-api-secret");
            polaris.StaffDomain = "SETTINGS";
            polaris.AdminUser = "settings-admin";
            polaris.ProtectedAdminPassword = protector.Protect("settings-admin-secret");
            polaris.WorkstationId = 73;
            polaris.SystemPolarisUserId = 4201;
            polaris.OrganizationIdForRequests = 731;
            polaris.PickupOrganizationId = 910;
            await seedContext.SaveChangesAsync();
        }

        try
        {
            using var loaded = await ReadSettingsDocumentAsync(client, "system");
            var storedPolaris = loaded.RootElement.GetProperty("stored").GetProperty("polaris");
            Assert.AreEqual(73, storedPolaris.GetProperty("workstationId").GetInt32());
            Assert.AreEqual(4201, storedPolaris.GetProperty("systemPolarisUserId").GetInt32());
            Assert.AreEqual(731, storedPolaris.GetProperty("organizationIdForRequests").GetInt32());
            Assert.AreEqual(910, storedPolaris.GetProperty("pickupOrganizationId").GetInt32());
            Assert.IsTrue(storedPolaris.GetProperty("hasApiKey").GetBoolean());
            Assert.IsTrue(storedPolaris.GetProperty("hasAdminPassword").GetBoolean());
            Assert.DoesNotContain("settings-api-secret", loaded.RootElement.GetRawText());
            Assert.DoesNotContain("settings-admin-secret", loaded.RootElement.GetRawText());

            using var unrelatedSave = await SaveSettingsDocumentAsync(client, loaded.RootElement, "system",
                new Dictionary<string, object?>
                {
                    ["ui_text"] = new { loginPrompt = "Unrelated Polaris round-trip setting" }
                });
            await AssertPolarisValuesAsync(73, 4201, 731, 910, "settings-api-secret", "settings-admin-secret");

            using var afterUnrelated = await ReadSettingsDocumentAsync(client, "system");
            using var blankSecretSave = await SaveSettingsDocumentAsync(client, afterUnrelated.RootElement, "system",
                new Dictionary<string, object?>
                {
                    ["polaris"] = new
                    {
                        host = "https://polaris-settings.invalid",
                        accessId = "settings-access",
                        staffDomain = "SETTINGS",
                        adminUser = "settings-admin",
                        workstationId = 73,
                        systemPolarisUserId = 4201,
                        organizationIdForRequests = 731,
                        pickupOrganizationId = 910,
                        apiKey = "",
                        adminPassword = "",
                        clearApiKey = false,
                        clearAdminPassword = false
                    }
                });
            await AssertPolarisValuesAsync(73, 4201, 731, 910, "settings-api-secret", "settings-admin-secret");

            using var afterBlank = await ReadSettingsDocumentAsync(client, "system");
            using var oneIdSave = await SaveSettingsDocumentAsync(client, afterBlank.RootElement, "system",
                new Dictionary<string, object?>
                {
                    ["polaris"] = new { organizationIdForRequests = 732 }
                });
            await AssertPolarisValuesAsync(73, 4201, 732, 910, "settings-api-secret", "settings-admin-secret");

            using var afterId = await ReadSettingsDocumentAsync(client, "system");
            using var replaceApi = await SaveSettingsDocumentAsync(client, afterId.RootElement, "system",
                new Dictionary<string, object?>
                {
                    ["polaris"] = new { apiKey = "replacement-api-secret", adminPassword = "" }
                });
            await AssertPolarisValuesAsync(73, 4201, 732, 910, "replacement-api-secret", "settings-admin-secret");

            using var afterApi = await ReadSettingsDocumentAsync(client, "system");
            using var clearAdmin = await SaveSettingsDocumentAsync(client, afterApi.RootElement, "system",
                new Dictionary<string, object?>
                {
                    ["polaris"] = new { clearAdminPassword = true }
                });
            await AssertPolarisValuesAsync(73, 4201, 732, 910, "replacement-api-secret", null);

            async Task AssertPolarisValuesAsync(
                int workstationId,
                int systemUserId,
                int requestingOrganizationId,
                int pickupOrganizationId,
                string? apiKey,
                string? adminPassword)
            {
                await using var assertionContext = await contexts.CreateDbContextAsync();
                var row = await assertionContext.PolarisSettings.AsNoTracking()
                    .SingleAsync(item => item.OrganizationId == 1);
                Assert.AreEqual(workstationId, row.WorkstationId);
                Assert.AreEqual(systemUserId, row.SystemPolarisUserId);
                Assert.AreEqual(requestingOrganizationId, row.OrganizationIdForRequests);
                Assert.AreEqual(pickupOrganizationId, row.PickupOrganizationId);
                Assert.AreEqual(apiKey, row.ProtectedApiKey is null ? null : protector.Unprotect(row.ProtectedApiKey));
                Assert.AreEqual(adminPassword,
                    row.ProtectedAdminPassword is null ? null : protector.Unprotect(row.ProtectedAdminPassword));
            }
        }
        finally
        {
            await using var restoreContext = await contexts.CreateDbContextAsync();
            var polaris = await restoreContext.PolarisSettings.SingleAsync(row => row.OrganizationId == 1);
            var patron = await restoreContext.PatronSettings.SingleAsync(row => row.OrganizationId == 1);
            polaris.Host = originalHost;
            polaris.AccessId = originalAccessId;
            polaris.ProtectedApiKey = originalApiKey;
            polaris.StaffDomain = originalStaffDomain;
            polaris.AdminUser = originalAdminUser;
            polaris.ProtectedAdminPassword = originalAdminPassword;
            polaris.WorkstationId = originalWorkstationId;
            polaris.SystemPolarisUserId = originalSystemUserId;
            polaris.OrganizationIdForRequests = originalRequestingOrganizationId;
            polaris.PickupOrganizationId = originalPickupOrganizationId;
            patron.LoginPrompt = originalLoginPrompt;
            await restoreContext.SaveChangesAsync();
        }
    }

    [TestMethod]
    public async Task StaffPortSystemSettingsRoundTripCollectionsIconAndParticipation()
    {
        const int enabledLibraryId = 91641;
        const int disabledLibraryId = 91642;
        await UpsertTestOrganizationAsync(enabledLibraryId, "Settings enabled library", "SEL");
        await UpsertTestOrganizationAsync(disabledLibraryId, "Settings disabled library", "SDL");

        using var client = await StaffPortClientAsync(factory!);
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        string? originalIconPattern;
        bool originalCreatorSetExists;
        bool originalPatronCodeSetExists;
        (string Value, int SortOrder)[] originalCreators;
        string[] originalPatronCodes;
        (string Origin, string NormalizedOrigin)[] originalOrigins;
        long[] seededOriginIds;
        (long Id, bool IsEnabled, string Label, string UrlTemplate, int SortOrder)[] originalProviders;
        Dictionary<int, bool> originalParticipation;

        await using (var seedContext = await contexts.CreateDbContextAsync())
        {
            var systemSettings = await seedContext.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
            originalIconPattern = systemSettings.MaterialTypeIconUrlPattern;
            originalCreatorSetExists = await seedContext.CommonCreatorSets.AnyAsync(item => item.OrganizationId == 1);
            originalPatronCodeSetExists = await seedContext.PatronCodeEligibilitySets.AnyAsync(item => item.OrganizationId == 1);
            originalCreators = await seedContext.CommonCreatorTerms.AsNoTracking()
                .Where(item => item.OrganizationId == 1)
                .OrderBy(item => item.SortOrder).ThenBy(item => item.Id)
                .Select(item => new ValueTuple<string, int>(item.Value, item.SortOrder))
                .ToArrayAsync();
            originalPatronCodes = await seedContext.PatronCodeEligibilityMembers.AsNoTracking()
                .Where(item => item.OrganizationId == 1)
                .OrderBy(item => item.PatronCodeId)
                .Select(item => item.PatronCodeId)
                .ToArrayAsync();
            originalOrigins = await seedContext.PatronEmbedAllowedOrigins.AsNoTracking()
                .Where(item => item.OrganizationId == 1)
                .OrderBy(item => item.NormalizedOrigin)
                .Select(item => new ValueTuple<string, string>(item.Origin, item.NormalizedOrigin))
                .ToArrayAsync();
            var providers = await seedContext.ExternalSearchProviders
                .Where(item => item.OrganizationId == 1 &&
                    (item.ProviderKey == "external_search_1" ||
                     item.ProviderKey == "external_search_2" ||
                     item.ProviderKey == "external_search_3"))
                .OrderBy(item => item.SortOrder)
                .ToArrayAsync();
            Assert.HasCount(3, providers);
            originalProviders = providers
                .Select(item => (item.Id, item.IsEnabled, item.Label, item.UrlTemplate, item.SortOrder))
                .ToArray();
            originalParticipation = await seedContext.Organizations.AsNoTracking()
                .ToDictionaryAsync(item => item.Id, item => item.IsActive);

            systemSettings.MaterialTypeIconUrlPattern = "https://icons.settings.example/{format}.svg";
            seedContext.CommonCreatorTerms.RemoveRange(
                await seedContext.CommonCreatorTerms.Where(item => item.OrganizationId == 1).ToListAsync());
            if (!originalCreatorSetExists)
            {
                seedContext.CommonCreatorSets.Add(new CommonCreatorSet { OrganizationId = 1 });
            }
            seedContext.CommonCreatorTerms.AddRange(
                new CommonCreatorTerm { OrganizationId = 1, Value = "Octavia E. Butler", SortOrder = 10 },
                new CommonCreatorTerm { OrganizationId = 1, Value = "N. K. Jemisin", SortOrder = 20 });
            seedContext.PatronCodeEligibilityMembers.RemoveRange(
                await seedContext.PatronCodeEligibilityMembers.Where(item => item.OrganizationId == 1).ToListAsync());
            if (!originalPatronCodeSetExists)
            {
                seedContext.PatronCodeEligibilitySets.Add(new PatronCodeEligibilitySet { OrganizationId = 1 });
            }
            seedContext.PatronCodeEligibilityMembers.AddRange(
                new PatronCodeEligibilityMember { OrganizationId = 1, PatronCodeId = "14" },
                new PatronCodeEligibilityMember { OrganizationId = 1, PatronCodeId = "28" });
            seedContext.PatronEmbedAllowedOrigins.RemoveRange(
                await seedContext.PatronEmbedAllowedOrigins.Where(item => item.OrganizationId == 1).ToListAsync());
            seedContext.PatronEmbedAllowedOrigins.AddRange(
                new PatronEmbedAllowedOrigin
                {
                    OrganizationId = 1,
                    Origin = "https://library-one.settings.example",
                    NormalizedOrigin = "https://library-one.settings.example",
                    CreatedUtc = DateTime.UtcNow
                },
                new PatronEmbedAllowedOrigin
                {
                    OrganizationId = 1,
                    Origin = "https://library-two.settings.example",
                    NormalizedOrigin = "https://library-two.settings.example",
                    CreatedUtc = DateTime.UtcNow
                });

            providers[0].IsEnabled = true;
            providers[0].Label = "Local Discovery";
            providers[0].UrlTemplate = "https://discovery.settings.example/?title={{title}}";
            providers[1].IsEnabled = false;
            providers[1].Label = "Disabled Research Index";
            providers[1].UrlTemplate = "https://research.settings.example/?isbn={{isbn}}";
            providers[2].IsEnabled = true;
            providers[2].Label = "Regional Catalog";
            providers[2].UrlTemplate = "https://regional.settings.example/?q={{title}}";

            var enabledLibrary = await seedContext.Organizations.SingleAsync(item => item.Id == enabledLibraryId);
            var disabledLibrary = await seedContext.Organizations.SingleAsync(item => item.Id == disabledLibraryId);
            enabledLibrary.IsActive = true;
            disabledLibrary.IsActive = false;
            await seedContext.SaveChangesAsync();
            seededOriginIds = await seedContext.PatronEmbedAllowedOrigins.AsNoTracking()
                .Where(item => item.OrganizationId == 1)
                .OrderBy(item => item.NormalizedOrigin)
                .Select(item => item.Id)
                .ToArrayAsync();
        }

        try
        {
            using var loaded = await ReadSettingsDocumentAsync(client, "system");
            var stored = loaded.RootElement.GetProperty("stored");
            CollectionAssert.AreEqual(
                new[] { "Octavia E. Butler", "N. K. Jemisin" },
                stored.GetProperty("commonCreators").EnumerateArray().Select(item => item.GetString()).ToArray());
            CollectionAssert.AreEqual(
                new[] { "14", "28" },
                stored.GetProperty("allowedPatronCodeIds").EnumerateArray().Select(item => item.GetString()).ToArray());
            Assert.AreEqual("https://icons.settings.example/{format}.svg",
                stored.GetProperty("systemSettings").GetProperty("formatIconUrlPattern").GetString());
            CollectionAssert.AreEqual(
                new[] { "https://library-one.settings.example", "https://library-two.settings.example" },
                stored.GetProperty("systemSettings").GetProperty("patronEmbedAllowedOrigins")
                    .EnumerateArray().Select(item => item.GetString()).ToArray());
            var loadedProviders = stored.GetProperty("providers").EnumerateArray().ToDictionary(
                item => item.GetProperty("key").GetString()!, item => item);
            Assert.AreEqual("Local Discovery", loadedProviders["external_search_1"].GetProperty("label").GetString());
            Assert.IsFalse(loadedProviders["external_search_2"].GetProperty("isEnabled").GetBoolean());
            Assert.AreEqual("https://regional.settings.example/?q={{title}}",
                loadedProviders["external_search_3"].GetProperty("urlTemplate").GetString());

            var enabledAtLoad = originalParticipation
                .Where(item => item.Key > 1 && item.Value)
                .Select(item => item.Key)
                .Append(enabledLibraryId)
                .Where(item => item != disabledLibraryId)
                .Distinct()
                .Order()
                .ToArray();
            using var unchanged = await SaveSettingsDocumentAsync(client, loaded.RootElement, "system",
                new Dictionary<string, object?>
                {
                    ["workflow"] = new
                    {
                        commonAuthorsList = "Octavia E. Butler\nN. K. Jemisin",
                        allowedPatronCodeIds = new[] { "14", "28" },
                        externalSearch1Enabled = true,
                        externalSearch1Label = "Local Discovery",
                        externalSearch1UrlTemplate = "https://discovery.settings.example/?title={{title}}",
                        externalSearch2Enabled = false,
                        externalSearch2Label = "Disabled Research Index",
                        externalSearch2UrlTemplate = "https://research.settings.example/?isbn={{isbn}}",
                        externalSearch3Enabled = true,
                        externalSearch3Label = "Regional Catalog",
                        externalSearch3UrlTemplate = "https://regional.settings.example/?q={{title}}"
                    },
                    ["formatIconUrlPattern"] = "https://icons.settings.example/{format}.svg",
                    ["patronEmbedAllowedOrigins"] = new[]
                    {
                        "https://library-one.settings.example",
                        "https://library-two.settings.example"
                    },
                    ["enabledLibraryOrgIds"] = enabledAtLoad.Select(item => item.ToString()).ToArray()
                });
            await using (var noEditVerify = await contexts.CreateDbContextAsync())
            {
                CollectionAssert.AreEqual(seededOriginIds, await noEditVerify.PatronEmbedAllowedOrigins.AsNoTracking()
                    .Where(item => item.OrganizationId == 1)
                    .OrderBy(item => item.NormalizedOrigin)
                    .Select(item => item.Id)
                    .ToArrayAsync(), "an unchanged system-origin list must not delete and recreate rows");
            }
            await AssertSystemStateAsync(
                new[] { "Octavia E. Butler", "N. K. Jemisin" },
                new[] { "14", "28" },
                "Disabled Research Index",
                "https://icons.settings.example/{format}.svg",
                enabledAtLoad);

            using var afterUnchanged = await ReadSettingsDocumentAsync(client, "system");
            var currentPolaris = afterUnchanged.RootElement.GetProperty("stored").GetProperty("polaris");
            var currentRequestOrganizationId = currentPolaris.GetProperty("organizationIdForRequests").ValueKind == JsonValueKind.Null
                ? (int?)null
                : currentPolaris.GetProperty("organizationIdForRequests").GetInt32();
            using var polarisOnly = await SaveSettingsDocumentAsync(client, afterUnchanged.RootElement, "system",
                new Dictionary<string, object?>
                {
                    ["polaris"] = new { organizationIdForRequests = currentRequestOrganizationId }
                });
            await AssertParticipationAsync(enabledAtLoad);

            var editedParticipation = enabledAtLoad
                .Where(item => item != enabledLibraryId)
                .Append(disabledLibraryId)
                .Order()
                .ToArray();
            using var afterPolaris = await ReadSettingsDocumentAsync(client, "system");
            using var edited = await SaveSettingsDocumentAsync(client, afterPolaris.RootElement, "system",
                new Dictionary<string, object?>
                {
                    ["workflow"] = new
                    {
                        commonAuthorsList = "James Baldwin\nUrsula K. Le Guin",
                        allowedPatronCodeIds = new[] { "14" },
                        externalSearch2Enabled = false,
                        externalSearch2Label = "Edited Research Index",
                        externalSearch2UrlTemplate = "https://research.settings.example/?isbn={{isbn}}"
                    },
                    ["formatIconUrlPattern"] = "https://new-icons.settings.example/{format}.png",
                    ["enabledLibraryOrgIds"] = editedParticipation.Select(item => item.ToString()).ToArray()
                });
            await AssertSystemStateAsync(
                new[] { "James Baldwin", "Ursula K. Le Guin" },
                new[] { "14" },
                "Edited Research Index",
                "https://new-icons.settings.example/{format}.png",
                editedParticipation);

            using var afterEdited = await ReadSettingsDocumentAsync(client, "system");
            using var noParticipatingLibraries = await SaveSettingsDocumentAsync(
                client,
                afterEdited.RootElement,
                "system",
                new Dictionary<string, object?>
                {
                    ["enabledLibraryOrgIds"] = Array.Empty<string>()
                });
            await AssertParticipationAsync([]);
            await using (var systemOrganizationCheck = await contexts.CreateDbContextAsync())
            {
                Assert.AreEqual(originalParticipation[1],
                    (await systemOrganizationCheck.Organizations.AsNoTracking().SingleAsync(item => item.Id == 1)).IsActive,
                    "an explicit empty library set must not change system organization 1");
            }

            using var reloaded = await ReadSettingsDocumentAsync(client, "system");
            Assert.AreEqual("https://new-icons.settings.example/{format}.png",
                reloaded.RootElement.GetProperty("stored").GetProperty("systemSettings")
                    .GetProperty("formatIconUrlPattern").GetString());

            async Task AssertSystemStateAsync(
                string[] creators,
                string[] patronCodes,
                string provider2Label,
                string iconPattern,
                int[] enabledLibraries)
            {
                await using var assertionContext = await contexts.CreateDbContextAsync();
                CollectionAssert.AreEqual(creators, await assertionContext.CommonCreatorTerms.AsNoTracking()
                    .Where(item => item.OrganizationId == 1)
                    .OrderBy(item => item.SortOrder).ThenBy(item => item.Id)
                    .Select(item => item.Value)
                    .ToArrayAsync());
                CollectionAssert.AreEqual(patronCodes, await assertionContext.PatronCodeEligibilityMembers.AsNoTracking()
                    .Where(item => item.OrganizationId == 1)
                    .OrderBy(item => item.PatronCodeId)
                    .Select(item => item.PatronCodeId)
                    .ToArrayAsync());
                Assert.AreEqual(provider2Label, (await assertionContext.ExternalSearchProviders.AsNoTracking()
                    .SingleAsync(item => item.ProviderKey == "external_search_2")).Label);
                Assert.AreEqual(iconPattern, (await assertionContext.SystemSettings.AsNoTracking()
                    .SingleAsync(item => item.OrganizationId == 1)).MaterialTypeIconUrlPattern);
                await AssertParticipationAsync(enabledLibraries);
            }

            async Task AssertParticipationAsync(int[] enabledLibraries)
            {
                await using var assertionContext = await contexts.CreateDbContextAsync();
                var actual = await assertionContext.Organizations.AsNoTracking()
                    .Where(item => item.Id > 1 && item.IsActive)
                    .OrderBy(item => item.Id)
                    .Select(item => item.Id)
                    .ToArrayAsync();
                var expected = enabledLibraries.Order().ToArray();
                CollectionAssert.AreEqual(expected, actual,
                    $"Expected active libraries {string.Join(',', expected)}; actual {string.Join(',', actual)}.");
            }
        }
        finally
        {
            await using var restoreContext = await contexts.CreateDbContextAsync();
            var systemSettings = await restoreContext.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
            systemSettings.MaterialTypeIconUrlPattern = originalIconPattern;

            restoreContext.CommonCreatorTerms.RemoveRange(
                await restoreContext.CommonCreatorTerms.Where(item => item.OrganizationId == 1).ToListAsync());
            var creatorSet = await restoreContext.CommonCreatorSets.SingleOrDefaultAsync(item => item.OrganizationId == 1);
            if (!originalCreatorSetExists && creatorSet is not null)
            {
                restoreContext.CommonCreatorSets.Remove(creatorSet);
            }
            else if (originalCreatorSetExists && creatorSet is null)
            {
                restoreContext.CommonCreatorSets.Add(new CommonCreatorSet { OrganizationId = 1 });
            }
            restoreContext.CommonCreatorTerms.AddRange(originalCreators.Select(item => new CommonCreatorTerm
            {
                OrganizationId = 1,
                Value = item.Value,
                SortOrder = item.SortOrder
            }));

            restoreContext.PatronCodeEligibilityMembers.RemoveRange(
                await restoreContext.PatronCodeEligibilityMembers.Where(item => item.OrganizationId == 1).ToListAsync());
            var patronCodeSet = await restoreContext.PatronCodeEligibilitySets.SingleOrDefaultAsync(item => item.OrganizationId == 1);
            if (!originalPatronCodeSetExists && patronCodeSet is not null)
            {
                restoreContext.PatronCodeEligibilitySets.Remove(patronCodeSet);
            }
            else if (originalPatronCodeSetExists && patronCodeSet is null)
            {
                restoreContext.PatronCodeEligibilitySets.Add(new PatronCodeEligibilitySet { OrganizationId = 1 });
            }
            restoreContext.PatronCodeEligibilityMembers.AddRange(originalPatronCodes.Select(item =>
                new PatronCodeEligibilityMember { OrganizationId = 1, PatronCodeId = item }));
            restoreContext.PatronEmbedAllowedOrigins.RemoveRange(
                await restoreContext.PatronEmbedAllowedOrigins.Where(item => item.OrganizationId == 1).ToListAsync());
            restoreContext.PatronEmbedAllowedOrigins.AddRange(originalOrigins.Select(item => new PatronEmbedAllowedOrigin
            {
                OrganizationId = 1,
                Origin = item.Origin,
                NormalizedOrigin = item.NormalizedOrigin,
                CreatedUtc = DateTime.UtcNow
            }));

            foreach (var backup in originalProviders)
            {
                var provider = await restoreContext.ExternalSearchProviders.SingleAsync(item => item.Id == backup.Id);
                provider.IsEnabled = backup.IsEnabled;
                provider.Label = backup.Label;
                provider.UrlTemplate = backup.UrlTemplate;
                provider.SortOrder = backup.SortOrder;
            }
            foreach (var organization in await restoreContext.Organizations.ToListAsync())
            {
                if (originalParticipation.TryGetValue(organization.Id, out var active))
                {
                    organization.IsActive = active;
                }
            }
            await restoreContext.SaveChangesAsync();
        }
    }

    private WebApplicationFactory<Program> WithStaffPortProviders(IStaffPolarisProvider staff, IPatronProvider? patron = null) =>
        factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton(staff);
            if (patron is not null)
            {
                services.RemoveAll<IPatronProvider>();
                services.AddSingleton(patron);
            }
        }));

    private async Task<HttpClient> StaffPortClientAsync(
        WebApplicationFactory<Program> app,
        CurrentStaff? selectedActor = null)
    {
        var client = app.CreateClient();
        var actor = selectedActor ?? await ReadConfiguredSuperAdminAsync();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        return client;
    }

    private static async Task ConfigureStaffPortLibraryAsync(
        HttpClient client,
        bool crossLibrary,
        int organizationId = 91632)
    {
        await UpsertTestOrganizationAsync(organizationId, "Staff port tests", "SPT");
        var organizationIdText = organizationId.ToString();
        using var current = await ReadSettingsDocumentAsync(client, organizationIdText);
        using var saved = await SaveSettingsDocumentAsync(client, current.RootElement, organizationIdText, new Dictionary<string, object?>
        {
            ["workflow"] = new { suggestionLimit = 1, allowAnyRegisteredCardLogin = crossLibrary,
                patronCodeEligibilityEnabled = true, allowedPatronCodeIds = new[] { "1" } }
        });
    }

    private static async Task<long> SeedCollidingBibLookupRequestsAsync()
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DECLARE @requestId bigint = (
                SELECT ISNULL(MAX([Id]), 0) + 1000
                FROM (
                    SELECT [Id] FROM [asap].[TitleRequest]
                    UNION ALL
                    SELECT [Id] FROM [asap].[AdditionalCopyRequest]
                ) ids);
            DECLARE @formatId bigint = (
                SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');

            SET IDENTITY_INSERT [asap].[TitleRequest] ON;
            INSERT INTO [asap].[TitleRequest]
                ([Id], [LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId],
                 [Status], [BibId], [CreatedUtc], [UpdatedUtc])
            VALUES
                (@requestId, 2, N'20000000004901', N'Colliding title request', 0, @formatId,
                 N'pending_hold', N'123', SYSUTCDATETIME(), SYSUTCDATETIME()),
                (@requestId + 2, 2, N'20000000004902', N'Title request without additional-copy twin', 0, @formatId,
                 N'pending_hold', N'123', SYSUTCDATETIME(), SYSUTCDATETIME());
            SET IDENTITY_INSERT [asap].[TitleRequest] OFF;

            SET IDENTITY_INSERT [asap].[AdditionalCopyRequest] ON;
            INSERT INTO [asap].[AdditionalCopyRequest]
                ([Id], [LibraryOrganizationId], [BibId], [Title], [Status], [CreatedUtc], [UpdatedUtc])
            VALUES
                (@requestId, 91632, N'123', N'Colliding additional-copy request', N'open',
                 SYSUTCDATETIME(), SYSUTCDATETIME()),
                (@requestId + 1, 91632, N'123', N'Additional-copy request without title twin', N'open',
                 SYSUTCDATETIME(), SYSUTCDATETIME());
            SET IDENTITY_INSERT [asap].[AdditionalCopyRequest] OFF;

            SELECT @requestId;
            """;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static StaffSuggestionInput StaffPortSuggestion(string barcode) => new(
        barcode, "Staff port " + Guid.NewGuid().ToString("N"), "Port author", null, "book", "Coming soon", null, "102", true, "91632");

    private sealed class StaffSearchResponseHandler(Func<Uri, string> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request.RequestUri!)), RequestMessage = request
            });
        }
    }

    private sealed class StaffPortPatronProvider : IPatronProvider, IStaffPolarisProvider
    {
        private readonly DeterministicTestingPatronProvider inner = new();
        public List<string> DirectLookups { get; } = [];
        public List<int> HoldingsOrganizationIds { get; } = [];
        public bool FailPatronLookup { get; init; }
        public bool FailHoldLookup { get; init; }
        public int PickupUpdates { get; private set; }
        public async Task<PatronSnapshot> RefreshAsync(string barcode, CancellationToken cancellationToken)
        {
            DirectLookups.Add(barcode);
            if (FailPatronLookup)
            {
                throw new PolarisOperationalException("testing_patron_lookup_failed", "Testing patron lookup failure");
            }
            if (barcode == "Reader")
            {
                throw new PolarisOperationalException("polaris_patron_not_found", "Not found");
            }
            return await SnapshotAsync(barcode, cancellationToken);
        }
        private async Task<PatronSnapshot> SnapshotAsync(string barcode, CancellationToken cancellationToken) =>
            (await inner.RefreshAsync(barcode, cancellationToken)) with
            {
                HomeLibraryOrganizationId = barcode == "port-foreign" ? 3 : 91632,
                PatronCodeId = barcode == "port-restricted" ? "3" : "1"
            };
        public async Task<IReadOnlyList<PatronSnapshot>> SearchPatronsAsync(string query, CancellationToken cancellationToken)
        {
            var barcodes = query switch
            {
                "Single Reader" => new[] { "port-local", "port-foreign", "port-restricted" },
                "Reader" => ["port-local", "port-second", "port-foreign", "port-restricted"],
                "Many Readers" => Enumerable.Range(1, 15).Select(index => $"port-{index}").ToArray(),
                _ => []
            };
            return await Task.WhenAll(barcodes.Select(barcode => SnapshotAsync(barcode, cancellationToken)));
        }
        public Task<PatronSnapshot> AuthenticateAsync(string barcode, string pin, CancellationToken token) => SnapshotAsync(barcode, token);
        public Task<IReadOnlyList<PickupBranch>> GetPickupBranchesAsync(PatronSnapshot patron, CancellationToken token) => inner.GetPickupBranchesAsync(patron, token);
        public Task UpdatePreferredPickupBranchAsync(string barcode, int pickupBranchId, CancellationToken token)
        {
            PickupUpdates++;
            return inner.UpdatePreferredPickupBranchAsync(barcode, pickupBranchId, token);
        }
        public Task<IdentifierLookupResult> LookupIdentifierAsync(string identifier, CancellationToken token) => inner.LookupIdentifierAsync(identifier, token);
        public Task<BibValidationResult> ValidateBibAsync(int bibId, CancellationToken token) => inner.ValidateBibAsync(bibId, token);
        public Task<StaffBibHoldingsSummary> GetBibHoldingsAsync(int bibId, int organizationId, CancellationToken token)
        {
            HoldingsOrganizationIds.Add(organizationId);
            return inner.GetBibHoldingsAsync(bibId, organizationId, token);
        }
        public Task<IReadOnlyList<PolarisHoldSnapshot>> GetPatronHoldsAsync(string barcode, CancellationToken token) =>
            FailHoldLookup
                ? Task.FromException<IReadOnlyList<PolarisHoldSnapshot>>(
                    new PolarisOperationalException("testing_hold_lookup_failed", "Testing hold lookup failure"))
                : inner.GetPatronHoldsAsync(barcode, token);
        public Task<HoldProviderResult> CreateHoldAsync(HoldCreateCommand command, CancellationToken token) => inner.CreateHoldAsync(command, token);
        public Task<HoldProviderResult> ReplyToHoldAsync(HoldReplyCommand command, CancellationToken token) => inner.ReplyToHoldAsync(command, token);
    }
}
