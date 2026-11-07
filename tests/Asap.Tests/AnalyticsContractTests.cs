using Asap.Web.Features.Analytics;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Features.Staff;

namespace Asap.Tests;

[TestClass]
public sealed class AnalyticsContractTests
{
    [TestMethod]
    public void DateRangesUseBusinessMidnightAcrossTheAutumnDstBoundary()
    {
        var now = new DateTimeOffset(2026, 11, 2, 5, 30, 0, TimeSpan.Zero);

        var thisMonth = AssertDateRange("thisMonth", now);
        Assert.AreEqual("thisMonth", thisMonth.Key);
        Assert.AreEqual(new DateTime(2026, 11, 1, 4, 0, 0, DateTimeKind.Utc), thisMonth.StartUtc);
        Assert.AreEqual(new DateTime(2026, 11, 3, 5, 0, 0, DateTimeKind.Utc), thisMonth.EndExclusiveUtc);

        var lastMonth = AssertDateRange("lastMonth", now);
        Assert.AreEqual(new DateTime(2026, 10, 1, 4, 0, 0, DateTimeKind.Utc), lastMonth.StartUtc);
        Assert.AreEqual(new DateTime(2026, 11, 1, 4, 0, 0, DateTimeKind.Utc), lastMonth.EndExclusiveUtc);

        var unknown = AssertDateRange("unknown", now);
        Assert.AreEqual("last30", unknown.Key);
        Assert.AreEqual(new DateTime(2026, 10, 3, 4, 0, 0, DateTimeKind.Utc), unknown.StartUtc);
        Assert.AreEqual(new DateTime(2026, 11, 3, 5, 0, 0, DateTimeKind.Utc), unknown.EndExclusiveUtc);
    }

    [TestMethod]
    [DataRow("2024-02-29", "2024-02-29", "2024-03-01")]
    [DataRow("2026-12-31", "2026-12-31", "2027-01-01")]
    [DataRow("2026-01-31", "2026-02-01", "2026-02-02")]
    [DataRow("2050-01-01", "2050-12-31", "2051-01-01")]
    [DataRow("0001-01-01", "0001-01-01", "0001-01-02")]
    [DataRow("9999-12-30", "9999-12-30", "9999-12-31")]
    public void CustomDatesUseCalendarTransitionsWithoutSpanOrFutureRestrictions(
        string startDate,
        string endDate,
        string endExclusiveDate)
    {
        var result = AnalyticsService.ResolveDateRange(
            "custom", new DateTimeOffset(2026, 11, 2, 5, 30, 0, TimeSpan.Zero), "UTC", startDate, endDate);

        Assert.AreEqual(0, result.Errors.Count);
        Assert.IsNotNull(result.Range);
        Assert.AreEqual("custom", result.Range.Key);
        Assert.AreEqual(DateOnly.ParseExact(startDate, "yyyy-MM-dd"), result.Range.StartDate);
        Assert.AreEqual(DateOnly.ParseExact(endDate, "yyyy-MM-dd"), result.Range.EndDate);
        Assert.AreEqual("UTC", result.Range.TimeZoneId);
        Assert.AreEqual(DateOnly.ParseExact(startDate, "yyyy-MM-dd").ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            result.Range.StartUtc);
        Assert.AreEqual(DateOnly.ParseExact(endExclusiveDate, "yyyy-MM-dd").ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            result.Range.EndExclusiveUtc);
        Assert.AreEqual(result.Range.EndExclusiveUtc.AddTicks(-1), result.Range.End.UtcDateTime);
    }

    [TestMethod]
    [DataRow("America/Sao_Paulo", "2018-11-04", "2018-11-04", "startDate")]
    [DataRow("America/Sao_Paulo", "2018-11-03", "2018-11-03", "endDate")]
    [DataRow("America/Havana", "2026-11-01", "2026-11-01", "startDate")]
    [DataRow("America/Havana", "2026-10-31", "2026-10-31", "endDate")]
    public void CustomDatesRejectMissingOrAmbiguousBusinessMidnights(
        string timeZoneId,
        string startDate,
        string endDate,
        string field)
    {
        var result = AnalyticsService.ResolveDateRange(
            "custom", new DateTimeOffset(2026, 11, 2, 5, 30, 0, TimeSpan.Zero), timeZoneId, startDate, endDate);

        Assert.IsNull(result.Range);
        CollectionAssert.AreEquivalent(new[] { field }, result.Errors.Keys.ToArray());
        StringAssert.Contains(result.Errors[field][0], "unambiguous midnight");
    }

    [TestMethod]
    public void CustomDatesRejectUtcUnderflowInsteadOfClampingToTheMinimumInstant()
    {
        var result = AnalyticsService.ResolveDateRange(
            "custom", new DateTimeOffset(2026, 11, 2, 5, 30, 0, TimeSpan.Zero),
            "Etc/GMT-14", "0001-01-01", "0001-01-01");

        Assert.IsNull(result.Range);
        CollectionAssert.AreEquivalent(new[] { "startDate" }, result.Errors.Keys.ToArray());
        StringAssert.Contains(result.Errors["startDate"][0], "cannot be represented in UTC");
    }

    private static AnalyticsResolvedDateRange AssertDateRange(string? key, DateTimeOffset now)
    {
        var result = AnalyticsService.ResolveDateRange(key, now, "America/New_York");
        Assert.AreEqual(0, result.Errors.Count);
        Assert.IsNotNull(result.Range);
        return result.Range;
    }

    [TestMethod]
    public void ScopeIsResolvedFromDurableStaffRoleAndActiveLibraries()
    {
        var organizations = new[]
        {
            new AnalyticsLibrary(20, "Library twenty"),
            new AnalyticsLibrary(21, "Library twenty-one")
        };
        var ordinary = Staff("staff", 20);
        var ordinaryResult = AnalyticsService.ResolveScope(ordinary, LibraryScope.ForLibrary(21), organizations);
        Assert.IsTrue(ordinaryResult.IsValid);
        Assert.AreEqual(20, ordinaryResult.OrganizationId);
        Assert.AreEqual(20, ordinaryResult.LibraryOrgId);

        var superAdmin = Staff("super_admin", 1);
        var all = AnalyticsService.ResolveScope(superAdmin, LibraryScope.All, organizations);
        Assert.IsTrue(all.IsValid);
        Assert.IsNull(all.OrganizationId);
        Assert.AreEqual("all", all.Mode);

        var selected = AnalyticsService.ResolveScope(superAdmin, LibraryScope.ForLibrary(21), organizations);
        Assert.IsTrue(selected.IsValid);
        Assert.AreEqual(21, selected.OrganizationId);
        Assert.AreEqual("Library twenty-one", selected.Label);
        Assert.IsFalse(AnalyticsService.ResolveScope(superAdmin, LibraryScope.ForLibrary(999), organizations).IsValid);
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
