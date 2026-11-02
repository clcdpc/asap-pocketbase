using System.Net;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;

namespace Asap.Tests.Integration;

public sealed partial class PolarisProtocolTests
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

}
