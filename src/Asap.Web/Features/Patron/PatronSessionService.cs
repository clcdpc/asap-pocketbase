using System.Data;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.SqlClient;
using Asap.Web.Infrastructure.Configuration;

namespace Asap.Web.Features.Patron;

public sealed record PatronSessionContext(
    long Id,
    string Barcode,
    int? HomeOrganizationId,
    int? ExperienceOrganizationId,
    int EffectiveOrganizationId,
    DateTime ExpiresUtc);

public sealed record IssuedPatronSession(string Token, PatronSessionContext Context);

public sealed class PatronSessionService(ExternalConfiguration configuration)
{
    private readonly string connectionString = configuration.ConnectionStrings.AsapDatabase!;

    public async Task<IssuedPatronSession?> IssueAsync(
        string barcode,
        int? homeOrganizationId,
        int? experienceOrganizationId,
        int effectiveOrganizationId,
        CancellationToken cancellationToken)
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = WebEncoders.Base64UrlEncode(tokenBytes);
        var tokenHash = SHA256.HashData(tokenBytes);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        await using (var organizationCommand = new SqlCommand(
            "SELECT [IsActive] FROM [asap].[Organization] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = @organizationId;",
            connection,
            transaction))
        {
            organizationCommand.Parameters.Add("@organizationId", SqlDbType.Int).Value = effectiveOrganizationId;
            var active = await organizationCommand.ExecuteScalarAsync(cancellationToken);
            if (active is not true)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }

        long id;
        DateTime expiresUtc;
        await using (var insertCommand = new SqlCommand(
            """
            INSERT INTO [asap].[PatronSession]
                ([TokenHash], [Barcode], [HomeOrganizationId], [ExperienceOrganizationId],
                 [EffectiveOrganizationId], [CreatedUtc], [ExpiresUtc])
            OUTPUT inserted.[Id], inserted.[ExpiresUtc]
            VALUES
                (@tokenHash, @barcode, @homeOrganizationId, @experienceOrganizationId,
                 @effectiveOrganizationId, SYSUTCDATETIME(), DATEADD(hour, 1, SYSUTCDATETIME()));
            """,
            connection,
            transaction))
        {
            insertCommand.Parameters.Add("@tokenHash", SqlDbType.Binary, 32).Value = tokenHash;
            insertCommand.Parameters.Add("@barcode", SqlDbType.NVarChar, 50).Value = barcode;
            insertCommand.Parameters.Add("@homeOrganizationId", SqlDbType.Int).Value = DbValue(homeOrganizationId);
            insertCommand.Parameters.Add("@experienceOrganizationId", SqlDbType.Int).Value = DbValue(experienceOrganizationId);
            insertCommand.Parameters.Add("@effectiveOrganizationId", SqlDbType.Int).Value = effectiveOrganizationId;
            await using var reader = await insertCommand.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            id = reader.GetInt64(0);
            expiresUtc = reader.GetDateTime(1);
        }

        await transaction.CommitAsync(cancellationToken);
        return new IssuedPatronSession(
            token,
            new PatronSessionContext(
                id,
                barcode,
                homeOrganizationId,
                experienceOrganizationId,
                effectiveOrganizationId,
                expiresUtc));
    }

    public async Task<PatronSessionContext?> AuthenticateAsync(
        string token,
        CancellationToken cancellationToken)
    {
        if (!TryHashToken(token, out var tokenHash))
        {
            return null;
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT s.[Id], s.[Barcode], s.[HomeOrganizationId], s.[ExperienceOrganizationId],
                   s.[EffectiveOrganizationId], s.[ExpiresUtc]
            FROM [asap].[PatronSession] AS s
            INNER JOIN [asap].[Organization] AS o ON o.[Id] = s.[EffectiveOrganizationId]
            WHERE s.[TokenHash] = @tokenHash
              AND s.[RevokedUtc] IS NULL
              AND s.[ExpiresUtc] > SYSUTCDATETIME()
              AND o.[IsActive] = 1;
            """,
            connection);
        command.Parameters.Add("@tokenHash", SqlDbType.Binary, 32).Value = tokenHash;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PatronSessionContext(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetDateTime(5));
    }

    public async Task RevokeAsync(string token, CancellationToken cancellationToken)
    {
        if (!TryHashToken(token, out var tokenHash))
        {
            return;
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            UPDATE [asap].[PatronSession]
            SET [RevokedUtc] = COALESCE([RevokedUtc], SYSUTCDATETIME())
            WHERE [TokenHash] = @tokenHash;
            """,
            connection);
        command.Parameters.Add("@tokenHash", SqlDbType.Binary, 32).Value = tokenHash;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetOrganizationActiveAsync(
        int organizationId,
        bool isActive,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        await using (var lockCommand = new SqlCommand(
            "SELECT [Id] FROM [asap].[Organization] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = @organizationId;",
            connection,
            transaction))
        {
            lockCommand.Parameters.Add("@organizationId", SqlDbType.Int).Value = organizationId;
            if (await lockCommand.ExecuteScalarAsync(cancellationToken) is null)
            {
                throw new InvalidOperationException("Organization does not exist.");
            }
        }

        await using (var updateCommand = new SqlCommand(
            "UPDATE [asap].[Organization] SET [IsActive] = @isActive WHERE [Id] = @organizationId;",
            connection,
            transaction))
        {
            updateCommand.Parameters.Add("@isActive", SqlDbType.Bit).Value = isActive;
            updateCommand.Parameters.Add("@organizationId", SqlDbType.Int).Value = organizationId;
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!isActive)
        {
            await using var revokeCommand = new SqlCommand(
                """
                UPDATE [asap].[PatronSession]
                SET [RevokedUtc] = COALESCE([RevokedUtc], SYSUTCDATETIME())
                WHERE [EffectiveOrganizationId] = @organizationId;
                """,
                connection,
                transaction);
            revokeCommand.Parameters.Add("@organizationId", SqlDbType.Int).Value = organizationId;
            await revokeCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static bool TryHashToken(string token, out byte[] tokenHash)
    {
        tokenHash = [];
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            var bytes = WebEncoders.Base64UrlDecode(token.Trim());
            if (bytes.Length != 32)
            {
                return false;
            }

            tokenHash = SHA256.HashData(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static object DbValue(int? value) => value.HasValue ? value.Value : DBNull.Value;
}
