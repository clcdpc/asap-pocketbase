using System.Net.Mail;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Staff;

public sealed record StaffProfileInput(
    string? Version,
    bool WeeklyActionSummaryEnabled,
    string? WeeklyActionSummaryEmail,
    bool PurchaseReminderDefault,
    bool AdditionalCopyReminderDefault,
    bool DefaultMineUnclaimedFilter);

public sealed record StaffMutationResult(string Code, CurrentStaff? Staff = null);

public sealed class StaffProfileService(
    IDbContextFactory<AsapDbContext> contextFactory,
    StaffEligibilityService eligibility)
{
    public async Task<StaffMutationResult> UpdateAsync(
        CurrentStaff currentStaff,
        StaffProfileInput input,
        CancellationToken cancellationToken)
    {
        if (!StaffVersion.TryDecode(input.Version, out var expectedVersion))
        {
            return new StaffMutationResult("invalid_version");
        }

        if (!StaffEmail.TryNormalize(input.WeeklyActionSummaryEmail, out var weeklyEmail))
        {
            return new StaffMutationResult("invalid_weekly_email");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await context.StaffUsers.SingleOrDefaultAsync(
            item => item.Id == currentStaff.Id,
            cancellationToken);
        if (row is null || !row.IsActive ||
            row.EntraTenantId != currentStaff.EntraTenantId ||
            row.EntraObjectId != currentStaff.EntraObjectId)
        {
            return new StaffMutationResult("staff_session_invalid");
        }

        context.Entry(row).Property(item => item.RowVersion).OriginalValue = expectedVersion;
        row.WeeklyActionSummaryEnabled = input.WeeklyActionSummaryEnabled;
        row.WeeklyActionSummaryEmail = weeklyEmail;
        row.PurchaseReminderDefault = input.PurchaseReminderDefault;
        row.AdditionalCopyReminderDefault = input.AdditionalCopyReminderDefault;
        row.DefaultMineUnclaimedFilter = input.DefaultMineUnclaimedFilter;

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new StaffMutationResult("stale_version");
        }

        var refreshed = await eligibility.EvaluateAsync(
            new StaffIdentityEvidence(currentStaff.Id, currentStaff.EntraTenantId, currentStaff.EntraObjectId),
            null,
            StaffRoleRequirement.Any,
            requireParticipation: true,
            cancellationToken);
        return refreshed.Outcome == StaffEligibilityOutcome.Allowed
            ? new StaffMutationResult("updated", refreshed.Staff)
            : new StaffMutationResult(refreshed.Code);
    }
}

public static class StaffVersion
{
    public static bool TryDecode(string? value, out byte[] version)
    {
        version = [];
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            version = Convert.FromBase64String(value);
            return version.Length == 8;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string Encode(byte[] value) => Convert.ToBase64String(value);
}

public static class StaffEmail
{
    public static bool TryNormalize(string? value, out string? normalized)
    {
        normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (normalized is null)
        {
            return true;
        }

        try
        {
            var parsed = new MailAddress(normalized);
            return string.Equals(parsed.Address, normalized, StringComparison.OrdinalIgnoreCase) &&
                   !normalized.EndsWith("@staff.asap.local", StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
