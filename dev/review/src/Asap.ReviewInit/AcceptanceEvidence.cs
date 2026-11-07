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
            if (sql.DataSource != "localhost,1433" || sql.InitialCatalog != "AsapReview" ||
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
                throw new InvalidOperationException("Review runtime configuration could not be verified.");
            }

            await using var connection = new SqlConnection(sql.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 10;
            command.CommandText = """
                SELECT DB_NAME(), ORIGINAL_LOGIN(), CONVERT(int, SERVERPROPERTY('ProductMajorVersion')),
                    CONVERT(nvarchar(100), SERVERPROPERTY('Edition'));
                SELECT N'acceptance-pr410-' + scenario.[Name], CONVERT(varchar(20), r.Id), r.Status, r.AutoHold, r.BibId,
                    r.BibIdStaffVerified, CONVERT(varchar(18), r.RowVersion, 1),
                    (SELECT COUNT(*) FROM asap.TitleRequestEvent e WHERE e.TitleRequestId = r.Id),
                    r.Notes, r.CloseReason,
                    CONVERT(bit, CASE WHEN (r.PatronIdSnapshot BETWEEN 41001 AND 41011 OR r.PatronIdSnapshot IN (41013, 41014, 41015)) AND
                        r.Barcode = CONCAT(N'ACCEPTANCE', r.PatronIdSnapshot) THEN 1 ELSE 0 END),
                    CONVERT(bit, CASE WHEN EXISTS (SELECT 1 FROM asap.HoldPlacementOperation op WHERE op.TitleRequestId = r.Id AND op.State = N'succeeded') THEN 1 ELSE 0 END)
                FROM asap.TitleRequest r JOIN (VALUES
                    (41001, N'purchase-reminder-on'), (41002, N'purchase-reminder-off'),
                    (41003, N'purchase-hold'), (41004, N'purchase-no-hold'), (41005, N'purchase-duplicate'),
                    (41006, N'reject'), (41007, N'draft'), (41008, N'close-reopen'),
                    (41009, N'stale-action'), (41010, N'unverified'), (41011, N'stale-consent'),
                    (41013, N'd1-closed'), (41014, N'd1-protected'), (41015, N'd1-history')
                ) scenario([PatronId], [Name]) ON r.Barcode = CONCAT(N'ACCEPTANCE', scenario.[PatronId]);
                SELECT N'acceptance-pr410-copy', CONVERT(varchar(20), c.Id), c.Status,
                    CONVERT(varchar(18), c.RowVersion, 1),
                    CONVERT(bit, CASE WHEN c.CreatedUtc = c.UpdatedUtc THEN 1 ELSE 0 END)
                FROM asap.AdditionalCopyRequest c WHERE c.BibId = 941012;
                SELECT o.BusinessKey, o.Status, o.AttemptDeliveryMode, o.SuppressionReason
                FROM asap.EmailOutbox o WHERE EXISTS
                    (SELECT 1 FROM asap.TitleRequest r
                     WHERE r.Barcode LIKE N'ACCEPTANCE410%' AND
                    (o.BusinessKey LIKE N'rejection:' + CONVERT(varchar(20), r.Id) + N':%' OR
                     o.BusinessKey LIKE N'purchase-reminder:' + CONVERT(varchar(20), r.Id) + N':%' OR
                      o.BusinessKey LIKE N'staff-patron-action:%:' + CONVERT(varchar(20), r.Id) + N':%'));
                SELECT CONVERT(varchar(20), Id) FROM asap.EmailTemplate
                WHERE OrganizationId = 2 AND TemplateKey = N'rejection:acceptance-pr410-rejection' AND IsCustom = 1;
                SELECT COUNT(*) FROM asap.Organization
                WHERE Id = 2 AND IsActive = 1 AND OrganizationCodeId = 2;
                SELECT CONVERT(varchar(20), org.Id),
                    (SELECT COUNT(*) FROM asap.AdministrativeAudit a WHERE a.OrganizationId = org.Id AND a.Action = N'library_settings_updated'),
                    CONVERT(varchar(64), HASHBYTES('SHA2_256', COALESCE((
                        SELECT r.* FROM asap.TitleRequest r WHERE r.LibraryOrganizationId = org.Id ORDER BY r.Id FOR JSON PATH, INCLUDE_NULL_VALUES), N'[]')), 2),
                    CONVERT(varchar(64), HASHBYTES('SHA2_256', COALESCE((
                        SELECT e.* FROM asap.TitleRequestEvent e JOIN asap.TitleRequest r ON r.Id = e.TitleRequestId
                        WHERE r.LibraryOrganizationId = org.Id ORDER BY e.Id FOR JSON PATH, INCLUDE_NULL_VALUES), N'[]')), 2),
                    CONVERT(varchar(64), HASHBYTES('SHA2_256', COALESCE((
                        SELECT c.* FROM asap.FormatAutoClaimRule c WHERE c.LibraryOrganizationId = org.Id ORDER BY c.Id FOR JSON PATH, INCLUDE_NULL_VALUES), N'[]')), 2),
                    (SELECT COUNT(*) FROM asap.TitleRequest r WHERE r.LibraryOrganizationId = org.Id),
                    (SELECT COUNT(*) FROM asap.TitleRequestEvent e JOIN asap.TitleRequest r ON r.Id = e.TitleRequestId WHERE r.LibraryOrganizationId = org.Id)
                FROM asap.Organization org WHERE org.Id BETWEEN 47101 AND 47108 ORDER BY org.Id;
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
                    identityBound = reader.GetBoolean(10), holdHistoryRetained = reader.GetBoolean(11)
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
            await reader.NextResultAsync(cancellationToken);
            var settings = new List<object>();
            while (await reader.ReadAsync(cancellationToken))
            {
                settings.Add(new { organizationId = reader.GetString(0), audits = reader.GetInt32(1),
                    requestsDigest = reader.GetString(2), historyDigest = reader.GetString(3), claimsDigest = reader.GetString(4),
                    requests = reader.GetInt32(5), events = reader.GetInt32(6) });
            }
            return new
            {
                contract = 1, database = "AsapReview", login = "asap_review_runtime", sqlMajorVersion = 16, sqlEdition = "Developer",
                environment = "Review", deterministicProviders = true, deliveryMode = "capture",
                titles, copies, outbox, rejectionTemplates = templates, settings
            };
    }
}
