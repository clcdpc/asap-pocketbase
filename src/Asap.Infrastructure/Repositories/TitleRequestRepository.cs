using Asap.Core.Models;
using Asap.Infrastructure.Data;
using Dapper;

namespace Asap.Infrastructure.Repositories;

public sealed class TitleRequestRepository(ISqlConnectionFactory connectionFactory) : ITitleRequestRepository
{
    public async Task<IReadOnlyList<TitleRequest>> ListAsync(string? status, string? libraryOrgId, int page, int pageSize, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                id AS Id,
                patron_name AS PatronName,
                patron_email AS PatronEmail,
                library_org_id AS LibraryOrgId,
                status AS Status,
                close_reason AS CloseReason,
                close_message AS CloseMessage,
                created_at AS CreatedAtUtc,
                updated_at AS UpdatedAtUtc,
                row_version AS RowVersion
            FROM dbo.title_requests
            WHERE (@Status IS NULL OR status = @Status)
              AND (@LibraryOrgId IS NULL OR library_org_id = @LibraryOrgId)
            ORDER BY created_at DESC
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;

        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var results = await connection.QueryAsync<TitleRequest>(new CommandDefinition(
            sql,
            new
            {
                Status = string.IsNullOrWhiteSpace(status) ? null : status,
                LibraryOrgId = string.IsNullOrWhiteSpace(libraryOrgId) ? null : libraryOrgId,
                Offset = Math.Max(0, page - 1) * pageSize,
                PageSize = pageSize,
            },
            cancellationToken: cancellationToken));

        return results.ToList();
    }

    public async Task<bool> RejectAsync(RejectTitleRequestCommand command, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.title_requests
            SET
                status = 'closed',
                close_reason = @CloseReason,
                close_message = @CloseMessage,
                closed_by_staff_id = @StaffUserId,
                updated_at = SYSUTCDATETIME()
            WHERE id = @RequestId
              AND status NOT IN ('closed', 'hold_completed');
            """;

        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var changed = await connection.ExecuteAsync(new CommandDefinition(sql, command, cancellationToken: cancellationToken));
        return changed > 0;
    }

    public async Task<int> TimeoutOutstandingAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.title_requests
            SET
                status = 'closed',
                close_reason = 'timeout',
                close_message = 'Closed automatically after outstanding request timeout.',
                updated_at = SYSUTCDATETIME()
            WHERE status = 'outstanding'
              AND DATEDIFF(day, created_at, SYSUTCDATETIME()) >= 30;
            """;

        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }
}
