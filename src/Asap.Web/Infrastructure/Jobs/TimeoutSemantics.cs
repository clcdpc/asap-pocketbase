namespace Asap.Web.Infrastructure.Jobs;

public enum TimeoutFamily
{
    OutstandingTimeout,
    PendingHoldTimeout,
    HoldPickupTimeout,
    AdditionalCopyTimeout
}

public static class TimeoutSemantics
{
    public static DateTime CutoffUtc(
        DateTimeOffset now,
        TimeZoneInfo businessTimeZone,
        int days)
    {
        if (days < 1) throw new ArgumentOutOfRangeException(nameof(days));
        var local = TimeZoneInfo.ConvertTime(now, businessTimeZone).DateTime;
        var localCutoff = DateTime.SpecifyKind(local.AddDays(-days), DateTimeKind.Unspecified);
        if (businessTimeZone.IsAmbiguousTime(localCutoff))
        {
            var earlier = businessTimeZone.GetAmbiguousTimeOffsets(localCutoff)
                .Select(offset => localCutoff - offset)
                .Min();
            return DateTime.SpecifyKind(earlier, DateTimeKind.Utc);
        }
        if (businessTimeZone.IsInvalidTime(localCutoff))
        {
            var adjustment = businessTimeZone.GetUtcOffset(localCutoff.AddHours(1)) -
                             businessTimeZone.GetUtcOffset(localCutoff.AddHours(-1));
            localCutoff = localCutoff.Add(adjustment);
        }
        return TimeZoneInfo.ConvertTimeToUtc(localCutoff, businessTimeZone);
    }

    public static bool IsExpired(
        DateTime? ageUtc,
        DateTimeOffset now,
        TimeZoneInfo businessTimeZone,
        int days) =>
        ageUtc.HasValue && ageUtc.Value < CutoffUtc(now, businessTimeZone, days);
}
