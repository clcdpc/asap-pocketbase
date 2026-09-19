namespace Asap.Web.Features.Patron;

public sealed record PatronHoldRouting(
    int RequestingOrganizationId,
    int PickupOrganizationId);

public sealed record PatronHoldRoutingResult(
    PatronHoldRouting? Routing,
    string? ErrorCode)
{
    public bool IsValid => Routing is not null;
}

public static class PatronHoldRoutingResolver
{
    public static PatronHoldRoutingResult Resolve(
        PatronSnapshot patron,
        IReadOnlyList<PickupBranch> eligiblePickupBranches)
    {
        if (patron.PatronOrganizationId <= 0)
        {
            return new PatronHoldRoutingResult(null, "patron_registration_missing");
        }

        var eligibleIds = eligiblePickupBranches
            .Where(branch => branch.Id > 0)
            .Select(branch => branch.Id)
            .ToHashSet();

        if (patron.PreferredPickupBranchId is > 0)
        {
            if (!eligibleIds.Contains(patron.PreferredPickupBranchId.Value))
            {
                return new PatronHoldRoutingResult(null, "pickup_invalid");
            }

            return Resolved(patron.PatronOrganizationId, patron.PreferredPickupBranchId.Value);
        }

        if (!eligibleIds.Contains(patron.PatronOrganizationId))
        {
            return new PatronHoldRoutingResult(null, "pickup_missing");
        }

        return Resolved(patron.PatronOrganizationId, patron.PatronOrganizationId);
    }

    private static PatronHoldRoutingResult Resolved(int requestingOrganizationId, int pickupOrganizationId) =>
        new(
            new PatronHoldRouting(requestingOrganizationId, pickupOrganizationId),
            null);
}
