using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Email;

// The System row owns the credential; library rows own sender overrides.
internal sealed record EmailTestConfiguration(
    int OrganizationId,
    string? FromAddress,
    string? FromName,
    string? ProtectedSystemToken,
    string Version)
{
    public string ContextVersion(CurrentStaff actor, string mode, byte[] organizationVersion) => Hash(new
    {
        Version, mode, actor.Id, actor.AuthenticationEmail, actor.EntraTenantId,
        actor.Role, actor.OrganizationId, actor.NotificationEmail,
        StaffVersion = Convert.ToBase64String(actor.RowVersion),
        OrganizationVersion = Convert.ToBase64String(organizationVersion)
    });

    public static async Task<EmailTestConfiguration> ReadAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken,
        bool lockRows = false)
    {
        var rows = new List<EmailSettings>();
        foreach (var id in new[] { 1, organizationId }.Distinct().Order())
        {
            var query = lockRows
                ? context.EmailSettings.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[EmailSettings] WITH (UPDLOCK,HOLDLOCK) WHERE [OrganizationId] = {id}")
                : context.EmailSettings.Where(item => item.OrganizationId == id);
            var row = await query.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            if (row is not null)
            {
                rows.Add(row);
            }
        }
        return FromRows(organizationId, rows);
    }

    public static async Task<EmailTestConfiguration> ReadLockedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int organizationId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            SELECT [OrganizationId], [FromAddress], [FromName], [ProtectedServerToken], [RowVersion]
            FROM [asap].[EmailSettings] WITH (UPDLOCK,HOLDLOCK)
            WHERE [OrganizationId] IN (1, @organizationId)
            ORDER BY [OrganizationId];
            """, connection, transaction);
        command.Parameters.Add("@organizationId", SqlDbType.Int).Value = organizationId;
        var rows = new List<EmailSettings>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new EmailSettings
            {
                OrganizationId = reader.GetInt32(0),
                FromAddress = reader.IsDBNull(1) ? null : reader.GetString(1),
                FromName = reader.IsDBNull(2) ? null : reader.GetString(2),
                ProtectedServerToken = reader.IsDBNull(3) ? null : reader.GetString(3),
                RowVersion = (byte[])reader[4]
            });
        }
        return FromRows(organizationId, rows);
    }

    private static EmailTestConfiguration FromRows(int organizationId, IReadOnlyList<EmailSettings> rows)
    {
        var system = rows.SingleOrDefault(item => item.OrganizationId == 1);
        var library = organizationId == 1 ? null : rows.SingleOrDefault(item => item.OrganizationId == organizationId);
        var fromAddress = Clean(library?.FromAddress) ?? Clean(system?.FromAddress);
        var fromName = Clean(library?.FromName) ?? Clean(system?.FromName);
        return new EmailTestConfiguration(organizationId, fromAddress, fromName, system?.ProtectedServerToken,
            Hash(new
            {
                organizationId, fromAddress, fromName,
                SystemVersion = system is null ? null : Convert.ToBase64String(system.RowVersion),
                LibraryVersion = library is null ? null : Convert.ToBase64String(library.RowVersion)
            }));
    }

    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
