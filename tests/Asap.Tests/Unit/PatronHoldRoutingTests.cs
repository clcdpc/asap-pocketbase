using Asap.Web.Features.Patron;

namespace Asap.Tests.Unit;

[TestClass]
public sealed class PatronHoldRoutingTests
{
    [TestMethod]
    public void UsesLivePatronOrganizationAndPreferredPickupInsteadOfRequestSnapshot()
    {
        var patron = Patron(
            patronOrganizationId: 200,
            preferredPickupBranchId: 300);

        var result = PatronHoldRoutingResolver.Resolve(
            patron,
            [new PickupBranch(100, "Home"), new PickupBranch(300, "Cross-library")]);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(200, result.Routing!.RequestingOrganizationId);
        Assert.AreEqual(300, result.Routing.PickupOrganizationId);
    }

    [TestMethod]
    public void FallsBackToRegisteredOrganizationWhenDefaultIsAbsentAndEligible()
    {
        var result = PatronHoldRoutingResolver.Resolve(
            Patron(patronOrganizationId: 200, preferredPickupBranchId: null),
            [new PickupBranch(200, "Registered library")]);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(200, result.Routing!.RequestingOrganizationId);
        Assert.AreEqual(200, result.Routing.PickupOrganizationId);
    }

    [TestMethod]
    [DataRow("pickup_invalid", 300, 200)]
    [DataRow("pickup_missing", 0, 200)]
    public void RejectsMissingOrIneligiblePickup(string expectedError, int preferredPickupBranchId, int registeredOrganizationId)
    {
        var result = PatronHoldRoutingResolver.Resolve(
            Patron(
                patronOrganizationId: registeredOrganizationId,
                preferredPickupBranchId: preferredPickupBranchId == 0 ? null : preferredPickupBranchId),
            [new PickupBranch(100, "Other library")]);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(expectedError, result.ErrorCode);
    }

    [TestMethod]
    public void RejectsPatronWithoutRegisteredOrganization()
    {
        var result = PatronHoldRoutingResolver.Resolve(
            Patron(patronOrganizationId: 0, preferredPickupBranchId: 100),
            [new PickupBranch(100, "Main")]);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("patron_registration_missing", result.ErrorCode);
    }

    private static PatronSnapshot Patron(int patronOrganizationId, int? preferredPickupBranchId) =>
        new(
            42,
            "patron-barcode",
            "patron@example.org",
            "Test",
            "Patron",
            "1",
            "Standard",
            patronOrganizationId,
            100,
            "Home library",
            preferredPickupBranchId);
}
