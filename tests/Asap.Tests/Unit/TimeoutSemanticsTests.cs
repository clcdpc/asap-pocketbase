using Asap.Web.Infrastructure.Jobs;

namespace Asap.Tests.Unit;

[TestClass]
public sealed class TimeoutSemanticsTests
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [TestMethod]
    public void ExpiryUsesStrictCalendarCutoffEquality()
    {
        var now = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        var cutoff = TimeoutSemantics.CutoffUtc(now, Eastern, 7);

        Assert.IsFalse(TimeoutSemantics.IsExpired(cutoff, now, Eastern, 7));
        Assert.IsTrue(TimeoutSemantics.IsExpired(cutoff.AddTicks(-1), now, Eastern, 7));
    }

    [TestMethod]
    public void AmbiguousCalendarCutoffUsesEarlierUtcOccurrence()
    {
        var now = new DateTimeOffset(2026, 11, 2, 6, 30, 0, TimeSpan.Zero);

        Assert.AreEqual(
            new DateTime(2026, 11, 1, 5, 30, 0, DateTimeKind.Utc),
            TimeoutSemantics.CutoffUtc(now, Eastern, 1));
    }

    [TestMethod]
    public void InvalidCalendarCutoffMovesForwardAcrossSpringTransition()
    {
        var now = new DateTimeOffset(2026, 3, 9, 6, 30, 0, TimeSpan.Zero);

        Assert.AreEqual(
            new DateTime(2026, 3, 8, 7, 30, 0, DateTimeKind.Utc),
            TimeoutSemantics.CutoffUtc(now, Eastern, 1));
    }
}
