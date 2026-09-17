using Asap.Web.Features.Analytics;
using Asap.Web.Features.Staff;

namespace Asap.Tests;

[TestClass]
public sealed class AnalyticsContractTests
{
    [TestMethod]
    public void DateRangesUseBusinessMidnightAcrossTheAutumnDstBoundary()
    {
        var now = new DateTimeOffset(2026, 11, 2, 5, 30, 0, TimeSpan.Zero);

        var thisMonth = AnalyticsService.ResolveDateRange("thisMonth", now, "America/New_York");
        Assert.AreEqual("thisMonth", thisMonth.Key);
        Assert.AreEqual(new DateTime(2026, 11, 1, 4, 0, 0, DateTimeKind.Utc), thisMonth.StartUtc);
        Assert.AreEqual(new DateTime(2026, 11, 3, 5, 0, 0, DateTimeKind.Utc), thisMonth.EndExclusiveUtc);

        var lastMonth = AnalyticsService.ResolveDateRange("lastMonth", now, "America/New_York");
        Assert.AreEqual(new DateTime(2026, 10, 1, 4, 0, 0, DateTimeKind.Utc), lastMonth.StartUtc);
        Assert.AreEqual(new DateTime(2026, 11, 1, 4, 0, 0, DateTimeKind.Utc), lastMonth.EndExclusiveUtc);

        var unknown = AnalyticsService.ResolveDateRange("unknown", now, "America/New_York");
        Assert.AreEqual("last30", unknown.Key);
        Assert.AreEqual(new DateTime(2026, 10, 3, 4, 0, 0, DateTimeKind.Utc), unknown.StartUtc);
        Assert.AreEqual(new DateTime(2026, 11, 3, 5, 0, 0, DateTimeKind.Utc), unknown.EndExclusiveUtc);
    }

    [TestMethod]
    public void ScopeIsResolvedFromDurableStaffRoleAndActiveLibraries()
    {
        var organizations = new[]
        {
            new AnalyticsLibrary("20", "Library twenty"),
            new AnalyticsLibrary("21", "Library twenty-one")
        };
        var ordinary = Staff("staff", 20);
        var ordinaryResult = AnalyticsService.ResolveScope(ordinary, "21", organizations);
        Assert.IsTrue(ordinaryResult.IsValid);
        Assert.AreEqual(20, ordinaryResult.OrganizationId);
        Assert.AreEqual("20", ordinaryResult.LibraryOrgId);

        var superAdmin = Staff("super_admin", 1);
        var all = AnalyticsService.ResolveScope(superAdmin, "system", organizations);
        Assert.IsTrue(all.IsValid);
        Assert.IsNull(all.OrganizationId);
        Assert.AreEqual("all", all.Mode);

        var selected = AnalyticsService.ResolveScope(superAdmin, "21", organizations);
        Assert.IsTrue(selected.IsValid);
        Assert.AreEqual(21, selected.OrganizationId);
        Assert.AreEqual("Library twenty-one", selected.Label);
        Assert.IsFalse(AnalyticsService.ResolveScope(superAdmin, "999", organizations).IsValid);
    }

    private static CurrentStaff Staff(string role, int organizationId) => new(
        1,
        "STAFF@EXAMPLE.ORG",
        Guid.Parse("00000000-0000-0000-0000-000000000001"),
        "staff@example.org",
        "Analytics tester",
        "staff@example.org",
        role,
        organizationId,
        organizationId == 1 ? "System" : $"Library {organizationId}",
        true,
        false,
        null,
        false,
        false,
        false,
        []);
}
