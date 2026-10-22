using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Testing;

namespace Asap.Tests.Unit;

[TestClass]
public sealed class DeterministicPolarisProviderTests
{
    [TestMethod]
    public async Task EmptyProviderRejectsUnexpectedReadsReferencesAndWrites()
    {
        var provider = new DeterministicTestingPatronProvider();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.RefreshAsync("unseeded", 2, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.GetOrganizationsAsync(CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.LookupIdentifierAsync("unseeded", 2, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.CreateHoldAsync(new(7001, 9001, 101, 2, 99, 42), CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.ReplyToHoldAsync(new(Guid.NewGuid(), "group", "qualifier", 2), CancellationToken.None));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(3)]
    [DataRow(999)]
    public async Task DeclaredLibraryDoesNotAuthorizeAnotherContext(int wrongContext)
    {
        var provider = new DeterministicTestingPatronProvider();
        var patron = Patron(2);
        provider.AddPatron(patron, [new(101, "Main"), new(102, "North")], 2);
        provider.SetIdentifierResult("native", 2, new(IdentifierLookupOutcome.Found, 9001));
        provider.SetBib(9001, 2, new(true, "Title"), new(1, 0, 1, true, true));
        provider.SetPatronSearch("name", 2, [patron]);
        provider.SetBibSearch("title", "Title", "", "", 2, new([new(9001, "Title", null, null, null, null)], 1));
        provider.AllowPickupUpdate(patron.Barcode, 2, 102);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.RefreshAsync(patron.Barcode, wrongContext, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.GetPatronIdAsync(patron.Barcode, wrongContext, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.GetPickupBranchesAsync(patron, wrongContext, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.UpdatePreferredPickupBranchAsync(patron.Barcode, 102, wrongContext, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.LookupIdentifierAsync("native", wrongContext, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.ValidateBibAsync(9001, wrongContext, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.SearchPatronsAsync("name", wrongContext, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.SearchBibsAsync("title", "Title", "", "", wrongContext, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.GetBibHoldingsAsync(9001, wrongContext, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.GetPatronHoldsAsync(patron.Barcode, wrongContext, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.GetPatronCheckoutsAsync(patron.Barcode, wrongContext, CancellationToken.None));
        Assert.AreEqual(101, (await provider.RefreshAsync(patron.Barcode, 2, CancellationToken.None)).PreferredPickupBranchId);
    }

    [TestMethod]
    public async Task TwoLibrariesHaveDistinctReferencesSearchAndStatefulPickup()
    {
        var provider = new DeterministicTestingPatronProvider();
        provider.SetReferenceData([new(1, "System", null, 0, null), new(2, "West", "W", 2, 1), new(3, "East", "E", 2, 1)], [new(1, "Adult")]);
        foreach (var org in new[] { 2, 3 })
        {
            var patron = Patron(org);
            var branch = org == 2 ? 101 : 301;
            var bib = org == 2 ? 9001 : 10001;
            provider.AddPatron(patron, [new(branch, "Main"), new(branch + 1, "Second")], org);
            provider.AllowPickupUpdate(patron.Barcode, org, branch + 1);
            provider.SetIdentifierResult("same-query", org, new(IdentifierLookupOutcome.Found, bib));
            provider.SetPatronSearch("same-query", org, [patron]);
            provider.SetBibSearch("title", "same-query", "", "", org, new([new(bib, org == 2 ? "West title" : "East title", null, null, null, null)], 1));
            Assert.AreEqual(bib, (await provider.LookupIdentifierAsync("same-query", org, CancellationToken.None)).BibId);
            Assert.AreEqual(patron.Barcode, (await provider.SearchPatronsAsync("same-query", org, CancellationToken.None)).Single().Barcode);
            Assert.AreEqual(bib, (await provider.SearchBibsAsync("title", "same-query", "", "", org, CancellationToken.None)).Results.Single().BibId);
            Assert.AreEqual(branch, (await provider.GetPickupBranchesAsync(patron, org, CancellationToken.None)).First().Id);
            await provider.UpdatePreferredPickupBranchAsync(patron.Barcode, branch + 1, org, CancellationToken.None);
            Assert.AreEqual(branch + 1, (await provider.RefreshAsync(patron.Barcode, org, CancellationToken.None)).PreferredPickupBranchId);
            Assert.AreEqual(branch + 1, (await provider.AuthenticateAsync(patron.Barcode, "1234", CancellationToken.None)).PreferredPickupBranchId);
        }
        Assert.AreEqual(3, (await provider.GetOrganizationsAsync(CancellationToken.None)).Count);
        Assert.AreEqual(2, provider.Calls.Count(call => call.Operation == TestingPolarisOperation.PickupUpdate));
    }

    [TestMethod]
    public async Task HoldWritesRequireExactNativeInputsAndConversationContext()
    {
        var provider = new DeterministicTestingPatronProvider();
        var conversation = Guid.Parse("a96fdf25-0bdb-44db-98ef-6d5e832d3794");
        var create = new HoldCreateCommand(7001, 9001, 102, 101, 99, 42);
        var reply = new HoldReplyCommand(conversation, "group", "qualifier", 101);
        provider.ExpectCreate(create, new(HoldProviderOutcome.ReplyRequired, conversation, null, "group", "qualifier", 3, 5, "declared_reply"));
        provider.ExpectReply(reply, new(HoldProviderOutcome.FinalSuccess, conversation, 8123, "group", "qualifier", 2, 1, "declared_success"));
        foreach (var invalid in new[] { create with { RequestingOrganizationId = 3 }, create with { PatronId = 7002 },
                     create with { BibId = 9002 }, create with { PickupBranchId = 101 }, create with { WorkstationId = 0 }, create with { PolarisUserId = 0 } })
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.CreateHoldAsync(invalid, CancellationToken.None));
        }
        Assert.AreEqual(conversation, (await provider.CreateHoldAsync(create, CancellationToken.None)).RequestGuid);
        foreach (var invalid in new[] { reply with { RequestingOrganizationId = 3 }, reply with { RequestGuid = Guid.NewGuid() },
                     reply with { TxnGroupQualifier = "wrong" }, reply with { TxnQualifier = "wrong" } })
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.ReplyToHoldAsync(invalid, CancellationToken.None));
        }
        Assert.AreEqual(8123, (await provider.ReplyToHoldAsync(reply, CancellationToken.None)).HoldRequestId);
        Assert.AreEqual(create, provider.CreateCommands.Last());
        Assert.AreEqual(reply, provider.ReplyCommands.Last());
        provider.VerifyNoOutstandingWrites();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.CreateHoldAsync(create, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.ReplyToHoldAsync(reply, CancellationToken.None));
    }

    [TestMethod]
    public async Task StructuredFailuresAndCancellationDoNotDependOnMagicInputStrings()
    {
        var provider = new DeterministicTestingPatronProvider();
        provider.SetFailure(new(TestingPolarisOperation.Refresh, 3, "ordinary-card"), new PolarisOperationalException("declared_outage", "Unavailable"));
        var error = await Assert.ThrowsExactlyAsync<PolarisOperationalException>(() => provider.RefreshAsync("ordinary-card", 3, CancellationToken.None));
        Assert.AreEqual("declared_outage", error.Code);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.RefreshAsync("ordinary-card", 2, CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var count = provider.Calls.Count;
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => provider.UpdatePreferredPickupBranchAsync("ordinary-card", 301, 3, cancellation.Token));
        Assert.AreEqual(count, provider.Calls.Count);
    }

    private static PatronSnapshot Patron(int org) => new(org == 2 ? 7001 : 8001, org == 2 ? "west-card" : "east-card",
        "patron@example.org", "Test", "Patron", 1, "Adult", org == 2 ? 101 : 301, org, org == 2 ? "West" : "East", org == 2 ? 101 : 301);
}
