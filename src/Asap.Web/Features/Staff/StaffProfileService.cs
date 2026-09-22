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
        var organizationId = await context.StaffUsers.AsNoTracking().Where(item => item.Id == currentStaff.Id)
            .Select(item => (int?)item.OrganizationId).SingleOrDefaultAsync(cancellationToken);
        if (!organizationId.HasValue)
        {
            return new StaffMutationResult("staff_session_invalid");
        }
        await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        var organization = await context.Organizations.FromSqlInterpolated(
                $"SELECT * FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {organizationId.Value}")
            .SingleOrDefaultAsync(cancellationToken);
        if (organization is null) return new StaffMutationResult("staff_session_invalid");
        var locked = await eligibility.RevalidateLockedAsync(context, currentStaff, null, StaffRoleRequirement.Any,
            true, new HashSet<int> { organization.Id }, cancellationToken);
        if (locked.Outcome != StaffEligibilityOutcome.Allowed) return new StaffMutationResult(locked.Code);
        var row = context.StaffUsers.Local.Single(item => item.Id == currentStaff.Id);
        if (!row.RowVersion.SequenceEqual(expectedVersion)) return new StaffMutationResult("stale_version");
        context.Entry(row).Property(item => item.RowVersion).OriginalValue = expectedVersion;
        row.WeeklyActionSummaryEnabled = input.WeeklyActionSummaryEnabled;
        row.WeeklyActionSummaryEmail = weeklyEmail;
        row.PurchaseReminderDefault = input.PurchaseReminderDefault;
        row.AdditionalCopyReminderDefault = input.AdditionalCopyReminderDefault;
        row.DefaultMineUnclaimedFilter = input.DefaultMineUnclaimedFilter;

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new StaffMutationResult("stale_version");
        }

        return new StaffMutationResult("updated", locked.Staff! with
        {
            WeeklyActionSummaryEnabled = row.WeeklyActionSummaryEnabled,
            WeeklyActionSummaryEmail = row.WeeklyActionSummaryEmail,
            PurchaseReminderDefault = row.PurchaseReminderDefault,
            AdditionalCopyReminderDefault = row.AdditionalCopyReminderDefault,
            DefaultMineUnclaimedFilter = row.DefaultMineUnclaimedFilter,
            RowVersion = row.RowVersion
        });
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
    public static bool IsValidAuthenticationEmail(StaffUser staff) =>
        TryNormalizeAuthenticationEmail(
            staff.UserPrincipalName,
            out _,
            out var normalizedEmail) &&
        string.Equals(
            normalizedEmail,
            staff.NormalizedUserPrincipalName,
            StringComparison.Ordinal);

    public static bool MatchesAuthenticationEmail(StaffUser staff, string normalizedAuthenticationEmail) =>
        IsValidAuthenticationEmail(staff) &&
        string.Equals(
            staff.NormalizedUserPrincipalName,
            normalizedAuthenticationEmail,
            StringComparison.Ordinal);

    public static bool TryNormalizeAuthenticationEmail(
        string? value,
        out string? email,
        out string? normalizedEmail)
    {
        email = null;
        normalizedEmail = null;
        if (!TryNormalize(value, out var candidate) || candidate is null)
        {
            return false;
        }

        email = candidate;
        normalizedEmail = candidate.ToUpperInvariant();
        return true;
    }

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
