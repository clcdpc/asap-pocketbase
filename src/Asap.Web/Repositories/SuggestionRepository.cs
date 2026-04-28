using Asap.Web.Data;
using Asap.Web.Models;
using Dapper;

namespace Asap.Web.Repositories;

public sealed class SuggestionRepository(IDbConnectionFactory connectionFactory) : ISuggestionRepository
{
    public async Task<IReadOnlyList<Suggestion>> GetOpenAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT Id, PatronBarcode, Title, Author, Format, Status, CreatedUtc
            FROM dbo.Suggestions
            WHERE Status <> 'Closed'
            ORDER BY CreatedUtc DESC;
            """;

        using var connection = connectionFactory.CreateConnection();
        var command = new CommandDefinition(sql, cancellationToken: cancellationToken);
        var suggestions = await connection.QueryAsync<Suggestion>(command);
        return suggestions.AsList();
    }

    public async Task<int> CreateAsync(SuggestionCreateInput input, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.Suggestions (PatronBarcode, Title, Author, Format, Status)
            VALUES (@PatronBarcode, @Title, @Author, @Format, 'New');

            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """;

        var parameters = new
        {
            input.PatronBarcode,
            input.Title,
            Author = string.IsNullOrWhiteSpace(input.Author) ? "Unknown" : input.Author,
            input.Format
        };

        using var connection = connectionFactory.CreateConnection();
        var command = new CommandDefinition(sql, parameters, cancellationToken: cancellationToken);
        return await connection.ExecuteScalarAsync<int>(command);
    }
}
