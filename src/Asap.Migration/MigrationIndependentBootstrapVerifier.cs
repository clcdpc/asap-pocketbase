using System.Globalization;
using System.Net.Mail;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Asap.Migration;

internal sealed record ExpectedMigrationBootstrap(
    long TargetStaffUserId,
    bool Inserted,
    string AuthenticationEmail,
    string? DisplayName,
    string NotificationEmail);

/// <summary>Derives bootstrap authority from source staff and operator configuration, never report claims.</summary>
internal static class MigrationIndependentBootstrapVerifier
{
    internal static ExpectedMigrationBootstrap? Derive(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        string? externalConfigurationPath)
    {
        var staff = MigrationPackageReader.ReadRows(package, "staff-users.json", "staff_users");
        // Imported email identities have null Entra observation metadata. The tenant allowlist
        // governs future authentication; source tid/oid values are not authorization identities.
        if (staff.Any(row => row.RequiredString("role").Equals("super_admin", StringComparison.OrdinalIgnoreCase) &&
                             row.Bool("active") && AuthenticationEmail(row.String("email")) is not null))
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(externalConfigurationPath))
        {
            throw InvalidBootstrap("The source requires bootstrap, but no operator identity configuration was supplied.");
        }

        string email;
        string? displayName;
        string notificationEmail;
        try
        {
            using var configuration = JsonDocument.Parse(File.ReadAllText(externalConfigurationPath));
            var entra = Property(Property(configuration.RootElement, "Authentication"), "Entra");
            var tenants = Property(entra, "AllowedTenantIds");
            if (tenants.ValueKind != JsonValueKind.Array || tenants.GetArrayLength() == 0 ||
                tenants.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String ||
                    !Guid.TryParse(item.GetString(), out var tenantId) || tenantId == Guid.Empty))
            {
                throw InvalidBootstrap("Bootstrap requires a nonempty valid allowed-tenant configuration.");
            }
            var bootstrap = Property(entra, "InitialSuperAdmin");
            email = AuthenticationEmail(Property(bootstrap, "UserPrincipalName").GetString()) ??
                throw InvalidBootstrap("The permitted bootstrap authentication email is invalid.");
            displayName = OptionalText(bootstrap, "DisplayName");
            notificationEmail = AuthenticationEmail(OptionalText(bootstrap, "NotificationEmail")) ?? email;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw InvalidBootstrap("The operator bootstrap configuration is malformed.");
        }

        var sourceMatches = staff.Where(row => string.Equals(
            AuthenticationEmail(row.String("email"))?.ToUpperInvariant(), email.ToUpperInvariant(), StringComparison.Ordinal)).ToArray();
        if (sourceMatches.Length > 1)
        {
            throw InvalidBootstrap("The configured bootstrap identity is not unique in the source staff population.");
        }
        var inserted = sourceMatches.Length == 0;
        using var identity = new SqlCommand(inserted
            ? "SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = @identity;"
            : "SELECT [NewId] FROM [asap].[LegacyPocketBaseMapping] WHERE [EntityType] = N'staff_user' AND [PocketBaseId] = @identity;",
            connection, transaction);
        identity.Parameters.AddWithValue("@identity", inserted ? email.ToUpperInvariant() : sourceMatches[0].RequiredString("id"));
        var targetId = identity.ExecuteScalar();
        if (targetId is null or DBNull)
        {
            throw InvalidBootstrap("The independently configured bootstrap staff identity is absent from the imported target.");
        }
        return new ExpectedMigrationBootstrap(Convert.ToInt64(targetId, CultureInfo.InvariantCulture), inserted, email, displayName, notificationEmail);
    }

    internal static void VerifyReport(
        string connectionString,
        ValidatedMigrationPackage package,
        JsonElement report,
        string? externalConfigurationPath)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        var expected = Derive(connection, transaction: null, package, externalConfigurationPath);
        var claims = report.GetProperty("transformations").EnumerateArray()
            .Where(item => item.GetProperty("entity").GetString() == "migration_bootstrap_super_admin").ToArray();
        if (expected is null)
        {
            if (claims.Length != 0 || report.GetProperty("importedCounts").GetProperty("migration_bootstrap_staff_users").GetInt32() != 0)
            {
                throw InvalidBootstrap("The source already contains a usable super-admin; no bootstrap insertion or promotion was permitted.");
            }
            return;
        }
        if (claims.Length != 1 ||
            claims[0].GetProperty("targetStaffUserId").GetInt64() != expected.TargetStaffUserId ||
            claims[0].GetProperty("action").GetString() != (expected.Inserted ? "inserted" : "promoted_existing") ||
            claims[0].GetProperty("authenticationEmail").GetString() != expected.AuthenticationEmail ||
            report.GetProperty("importedCounts").GetProperty("migration_bootstrap_staff_users").GetInt32() != (expected.Inserted ? 1 : 0))
        {
            throw InvalidBootstrap("The report bootstrap claim differs from source necessity and the configured identity's permitted insertion or promotion.");
        }
    }

    private static string? AuthenticationEmail(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (text is null || text.EndsWith("@staff.asap.local", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        try
        {
            var address = new MailAddress(text);
            return address.Address.Equals(text, StringComparison.OrdinalIgnoreCase) ? text : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static JsonElement Property(JsonElement value, string name)
    {
        var matches = value.EnumerateObject().Where(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
        {
            throw InvalidBootstrap("The operator bootstrap configuration has a missing or ambiguous property.");
        }
        return matches[0].Value;
    }

    private static string? OptionalText(JsonElement value, string name)
    {
        var matches = value.EnumerateObject().Where(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0)
        {
            return null;
        }
        if (matches.Length > 1)
        {
            throw InvalidBootstrap("The operator bootstrap configuration has an ambiguous optional property.");
        }
        var text = matches[0].Value.ValueKind == JsonValueKind.String ? matches[0].Value.GetString() : null;
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static MigrationOperationException InvalidBootstrap(string message) =>
        new("reconciliation_bootstrap_not_authorized", message);
}
