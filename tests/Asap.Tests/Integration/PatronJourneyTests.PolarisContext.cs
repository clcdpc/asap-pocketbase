using System.Net;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow(2)]
    [DataRow(3)]
    public async Task PolarisPackageRequestsUseTheExplicitServicingLibrary(int organizationId)
    {
        var handler = new ContextPapiHandler(organizationId);
        var provider = await CreatePolarisProviderAsync(handler);
        const string barcode = "20000000003470";
        var patron = await provider.AuthenticateAsync(barcode, "1234", CancellationToken.None);
        Assert.AreEqual(organizationId, patron.HomeLibraryOrganizationId);
        await provider.RefreshAsync(barcode, organizationId, CancellationToken.None);
        Assert.AreEqual(7001, await provider.GetPatronIdAsync(barcode, organizationId, CancellationToken.None));
        var branches = await provider.GetPickupBranchesAsync(patron, organizationId, CancellationToken.None);
        Assert.AreEqual(handler.PickupBranchId, branches.Single().Id);
        await provider.UpdatePreferredPickupBranchAsync(barcode, handler.PickupBranchId,
            organizationId, CancellationToken.None);
        Assert.AreEqual(0, (await provider.SearchPatronsAsync("Name", organizationId, CancellationToken.None)).Count);
        Assert.AreEqual(9001, (await provider.LookupIdentifierAsync("9780000000001", organizationId,
            CancellationToken.None)).BibId);
        Assert.AreEqual(9001, (await provider.SearchBibsAsync("title", "Title", "", "",
            organizationId, CancellationToken.None)).Results.Single().BibId);
        Assert.IsTrue((await provider.ValidateBibAsync(9001, organizationId, CancellationToken.None)).IsValid);
        await provider.GetBibHoldingsAsync(9001, organizationId, CancellationToken.None);
        Assert.AreEqual(8456, (await provider.GetPatronHoldsAsync(barcode, organizationId,
            CancellationToken.None)).Single().HoldRequestId);
        Assert.AreEqual(9001, (await provider.GetPatronCheckoutsAsync(barcode, organizationId,
            CancellationToken.None)).Single().BibId);
        var create = await provider.CreateHoldAsync(new HoldCreateCommand(
            7001, 9001, handler.PickupBranchId, handler.PickupBranchId, 99, 42), CancellationToken.None);
        Assert.AreEqual(HoldProviderOutcome.ReplyRequired, create.Outcome);
        var reply = await provider.ReplyToHoldAsync(new HoldReplyCommand(
            create.RequestGuid!.Value, create.TxnGroupQualifier!, create.TxnQualifier!, handler.PickupBranchId),
            CancellationToken.None);
        Assert.AreEqual(HoldProviderOutcome.FinalSuccess, reply.Outcome);
        CollectionAssert.IsSubsetOf(new[] { "authenticate", "refresh", "pickup-read", "pickup-write",
            "patron-search", "bib-search", "bib-detail", "holdings", "holds", "checkouts", "create", "reply" },
            handler.Operations.ToArray());
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(-1)]
    public async Task PolarisMemberReadsRejectMissingOrSystemContextBeforeHttp(int organizationId)
    {
        var handler = new ContextPapiHandler(2);
        var provider = await CreatePolarisProviderAsync(handler);
        var error = await Assert.ThrowsAsync<PolarisOperationalException>(() =>
            provider.RefreshAsync("20000000003470", organizationId, CancellationToken.None));
        Assert.AreEqual("polaris_operation_context_missing", error.Code);
        Assert.AreEqual(0, handler.RequestCount);
    }

    private sealed class ContextPapiHandler(int organizationId) : HttpMessageHandler
    {
        public int PickupBranchId => organizationId * 100 + 1;
        public int RequestCount { get; private set; }
        public HashSet<string> Operations { get; } = [];
        private bool bootstrapping = true;
        private static readonly Guid Conversation = Guid.Parse("3b3ce08d-4111-411b-9999-784d0866d94c");

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            var path = request.RequestUri!.AbsolutePath;
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var actualOrganizationId = int.Parse(segments[6], System.Globalization.CultureInfo.InvariantCulture);
            string json;
            if (path.Contains("/authenticator/staff", StringComparison.Ordinal))
            {
                json = """{"PAPIErrorCode":0,"AccessToken":"context-token","AccessSecret":"context-secret","AuthExpDate":"2030-01-01T00:00:00Z"}""";
            }
            else if (path.Contains("/authenticator/patron", StringComparison.Ordinal))
            {
                Assert.AreEqual(1, actualOrganizationId, "Only bootstrap may use system scope.");
                Operations.Add("authenticate");
                json = """{"PAPIErrorCode":0,"PatronID":7001,"AccessToken":"patron-token","AccessSecret":"patron-secret"}""";
            }
            else if (path.EndsWith("/basicdata", StringComparison.Ordinal))
            {
                Assert.AreEqual(bootstrapping ? 1 : organizationId, actualOrganizationId);
                Operations.Add("refresh");
                json = JsonSerializer.Serialize(new { PAPIErrorCode = 0, PatronBasicData = new {
                    PatronID = 7001, Barcode = "20000000003470", PatronOrgID = PickupBranchId,
                    PatronCodeID = 1, RequestPickupBranchID = PickupBranchId } });
            }
            else if (path.Contains("/organizations/", StringComparison.Ordinal))
            {
                Assert.AreEqual(bootstrapping ? 1 : organizationId, actualOrganizationId,
                    "Hierarchy reads retain their bootstrap or servicing context.");
                bootstrapping = false;
                json = JsonSerializer.Serialize(new { PAPIErrorCode = 2, OrganizationsGetRows = new[] {
                    new { OrganizationID = organizationId, OrganizationCodeID = 2, Name = "Library", ParentOrganizationID = 1 },
                    new { OrganizationID = PickupBranchId, OrganizationCodeID = 3, Name = "Branch", ParentOrganizationID = organizationId } } });
            }
            else if (path.EndsWith("/pickupbranches", StringComparison.Ordinal))
            {
                Assert.AreEqual(PickupBranchId, actualOrganizationId, "Eligibility uses the live registered branch.");
                Operations.Add("pickup-read");
                json = JsonSerializer.Serialize(new { PAPIErrorCode = 1, PickupBranchesRows = new[] { new { ID = PickupBranchId } } });
            }
            else
            {
                var holdMemberContext = path.EndsWith("/holdrequest", StringComparison.Ordinal) ||
                    path.EndsWith("/holdrequest/" + Conversation, StringComparison.Ordinal);
                Assert.AreEqual(holdMemberContext ? PickupBranchId : organizationId, actualOrganizationId, path);
                if (request.Method == HttpMethod.Put && path.Contains("/patron/", StringComparison.Ordinal))
                {
                    Operations.Add("pickup-write");
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                    Assert.AreEqual(organizationId, body.RootElement.GetProperty("LogonBranchId").GetInt32());
                    Assert.AreEqual(PickupBranchId, body.RootElement.GetProperty("RequestPickupBranchID").GetInt32());
                    Assert.AreEqual(99, body.RootElement.GetProperty("LogonWorkstationId").GetInt32());
                    Assert.AreEqual(42, body.RootElement.GetProperty("LogonUserId").GetInt32());
                    json = """{"PAPIErrorCode":0}""";
                }
                else if (path.Contains("/search/patrons/", StringComparison.Ordinal))
                {
                    Operations.Add("patron-search");
                    json = """{"PAPIErrorCode":0,"TotalRecordsFound":0,"PatronSearchRows":[]}""";
                }
                else if (path.Contains("/search/bibs/", StringComparison.Ordinal))
                {
                    Operations.Add("bib-search");
                    json = """{"PAPIErrorCode":1,"TotalRecordsFound":1,"BibSearchRows":[{"ControlNumber":9001,"Title":"Title","ISBN":"9780000000001"}]}""";
                }
                else if (path.EndsWith("/holdings", StringComparison.Ordinal))
                {
                    Operations.Add("holdings");
                    json = """{"PAPIErrorCode":0,"BibHoldingsGetRows":[]}""";
                }
                else if (path.EndsWith("/bib/9001", StringComparison.Ordinal))
                {
                    Operations.Add("bib-detail");
                    json = """{"PAPIErrorCode":0,"BibGetRows":[{"ElementID":1,"Label":"Title","Value":"Title"}]}""";
                }
                else if (path.Contains("/holdrequests/", StringComparison.Ordinal))
                {
                    Operations.Add("holds");
                    json = JsonSerializer.Serialize(new { PAPIErrorCode = 0, PatronHoldRequestsGetRows = new[] {
                        new { HoldRequestID = 8456, BibID = 9001, StatusID = 9, StatusDescription = "Active", PickupBranchID = PickupBranchId } } });
                }
                else if (path.Contains("/itemsout/", StringComparison.Ordinal))
                {
                    Operations.Add("checkouts");
                    json = """{"PAPIErrorCode":0,"PatronItemsOutGetRows":[{"BibID":9001}]}""";
                }
                else if (path.EndsWith("/holdrequest", StringComparison.Ordinal))
                {
                    Operations.Add("create");
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                    Assert.AreEqual(PickupBranchId, body.RootElement.GetProperty("RequestingOrgID").GetInt32());
                    Assert.AreEqual(PickupBranchId, body.RootElement.GetProperty("PickupOrgID").GetInt32());
                    Assert.AreEqual(7001, body.RootElement.GetProperty("PatronID").GetInt32());
                    Assert.AreEqual(9001, body.RootElement.GetProperty("BibID").GetInt32());
                    Assert.AreEqual(99, body.RootElement.GetProperty("WorkstationID").GetInt32());
                    Assert.AreEqual(42, body.RootElement.GetProperty("UserID").GetInt32());
                    json = JsonSerializer.Serialize(new { PAPIErrorCode = 0, StatusType = 3, StatusValue = 5,
                        RequestGUID = Conversation, TxnGroupQualifer = "group", TxnQualifier = "qualifier" });
                }
                else if (path.EndsWith("/holdrequest/" + Conversation, StringComparison.Ordinal))
                {
                    Operations.Add("reply");
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                    Assert.AreEqual(PickupBranchId, body.RootElement.GetProperty("RequestingOrgID").GetInt32());
                    Assert.AreEqual("group", body.RootElement.GetProperty("TxnGroupQualifier").GetString());
                    json = JsonSerializer.Serialize(new { PAPIErrorCode = 0, StatusType = 2, StatusValue = 1, RequestGUID = Conversation });
                }
                else
                {
                    throw new AssertFailedException("Unexpected package request: " + path);
                }
            }
            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(json), RequestMessage = request };
        }
    }
}
