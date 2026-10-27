using System.Text.Json;
using Microsoft.Data.SqlClient;

// Read-only, fixed-query probe shipped only in the disposable review image.
// Never return connection strings, staff identities, patron data or email bodies.
internal static class AcceptanceEvidence
{
    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            Console.WriteLine(JsonSerializer.Serialize(await ReadAsync(cancellationToken)));
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("Review evidence probe timed out or was cancelled.");
            return 2;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Review evidence unavailable. Check the managed review environment.");
            return 2;
        }
    }

    public static async Task<object> ReadAsync(CancellationToken cancellationToken)
    {
            using var config = JsonDocument.Parse(await File.ReadAllTextAsync(
                "/review/config/application.json", cancellationToken));
            var root = config.RootElement;
            var connectionString = root.GetProperty("ConnectionStrings").GetProperty("AsapDatabase").GetString();
            var sql = new SqlConnectionStringBuilder(connectionString);
            if (sql.DataSource != "sql,1433" || sql.InitialCatalog != "AsapReview" ||
                sql.UserID != "asap_review_runtime" || sql.IntegratedSecurity ||
                root.GetProperty("Environment").GetProperty("Name").GetString() != "Review" ||
                !root.GetProperty("Environment").GetProperty("IsNonProduction").GetBoolean() ||
                root.GetProperty("EmailSafety").GetProperty("DeliveryMode").GetString() != "capture" ||
                root.GetProperty("ConnectionStrings").GetProperty("HangfireDatabase").GetString() != connectionString ||
                Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") != "Testing" ||
                Environment.GetEnvironmentVariable("Testing__UseDeterministicPatronProvider") != "true" ||
                Environment.GetEnvironmentVariable("ASPNETCORE_HOSTINGSTARTUPASSEMBLIES") != "Asap.ReviewProviders" ||
                await File.ReadAllTextAsync("/tmp/asap-review-provider-ready", cancellationToken) != "pr410-v1")
            {
                throw new InvalidOperationException("Review runtime isolation could not be verified.");
            }

            await using var connection = new SqlConnection(sql.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 10;
            command.CommandText = """
                SELECT DB_NAME(), ORIGINAL_LOGIN(), CONVERT(int, SERVERPROPERTY('ProductMajorVersion')),
                    CONVERT(nvarchar(100), SERVERPROPERTY('Edition'));
                SELECT r.LegacyId, CONVERT(varchar(20), r.Id), r.Status, r.AutoHold, r.BibId,
                    r.BibIdStaffVerified, CONVERT(varchar(18), r.RowVersion, 1),
                    (SELECT COUNT(*) FROM asap.TitleRequestEvent e WHERE e.TitleRequestId = r.Id),
                    r.Notes, r.CloseReason,
                    CONVERT(bit, CASE WHEN r.PatronIdSnapshot BETWEEN 41001 AND 41011 AND
                        r.Barcode = CONCAT(N'ACCEPTANCE', r.PatronIdSnapshot) THEN 1 ELSE 0 END)
                FROM asap.TitleRequest r WHERE r.LegacyId LIKE N'acceptance-pr410-%';
                SELECT c.LegacyId, CONVERT(varchar(20), c.Id), c.Status,
                    CONVERT(varchar(18), c.RowVersion, 1),
                    CONVERT(bit, CASE WHEN c.CreatedUtc = c.UpdatedUtc THEN 1 ELSE 0 END)
                FROM asap.AdditionalCopyRequest c WHERE c.LegacyId = N'acceptance-pr410-copy';
                SELECT o.BusinessKey, o.Status, o.AttemptDeliveryMode, o.SuppressionReason
                FROM asap.EmailOutbox o WHERE EXISTS
                    (SELECT 1 FROM asap.TitleRequest r
                     WHERE r.LegacyId LIKE N'acceptance-pr410-%' AND
                    (o.BusinessKey LIKE N'rejection:' + CONVERT(varchar(20), r.Id) + N':%' OR
                     o.BusinessKey LIKE N'purchase-reminder:' + CONVERT(varchar(20), r.Id) + N':%' OR
                      o.BusinessKey LIKE N'staff-patron-action:%:' + CONVERT(varchar(20), r.Id) + N':%'));
                SELECT CONVERT(varchar(20), Id) FROM asap.EmailTemplate
                WHERE OrganizationId = 2 AND TemplateKey = N'acceptance-pr410-rejection' AND IsCustom = 1;
                SELECT COUNT(*) FROM asap.Organization
                WHERE Id = 2 AND IsActive = 1 AND OrganizationCodeId = 2;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            if (reader.GetString(0) != "AsapReview" || reader.GetString(1) != "asap_review_runtime" ||
                reader.GetInt32(2) != 16 || !reader.GetString(3).StartsWith("Developer Edition", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Review SQL identity could not be verified.");
            }
            var titles = new List<object>();
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                titles.Add(new
                {
                    key = reader.GetString(0), id = reader.GetString(1), status = reader.GetString(2),
                    autohold = reader.GetBoolean(3), bibid = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4),
                    verified = reader.GetBoolean(5), version = reader.GetString(6), events = reader.GetInt32(7),
                    pristineNotes = !reader.IsDBNull(8) && reader.GetString(8) == "Acceptance fixture draft baseline",
                    closeReason = reader.IsDBNull(9) ? null : reader.GetString(9),
                    identityBound = reader.GetBoolean(10)
                });
            }
            var copies = new List<object>();
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                copies.Add(new { key = reader.GetString(0), id = reader.GetString(1),
                    status = reader.GetString(2), version = reader.GetString(3), pristine = reader.GetBoolean(4) });
            }
            var outbox = new List<object>();
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                outbox.Add(new { key = reader.GetString(0), status = reader.GetString(1),
                    deliveryMode = reader.IsDBNull(2) ? null : reader.GetString(2),
                    suppressionReason = reader.IsDBNull(3) ? null : reader.GetString(3) });
            }
            await reader.NextResultAsync(cancellationToken);
            var templates = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
            {
                templates.Add(reader.GetString(0));
            }
            await reader.NextResultAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            if (reader.GetInt32(0) != 1)
            {
                throw new InvalidOperationException("Review Library classification is unavailable.");
            }
            return new
            {
                contract = 1, database = "AsapReview", login = "asap_review_runtime", sqlMajorVersion = 16, sqlEdition = "Developer",
                environment = "Review", deterministicProviders = true, deliveryMode = "capture",
                titles, copies, outbox, rejectionTemplates = templates
            };
    }
}
