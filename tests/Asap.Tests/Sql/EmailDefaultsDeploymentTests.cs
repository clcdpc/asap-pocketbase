using System.Text.Encodings.Web;
using Asap.Tests.Email;
using Asap.Tests.Integration;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Development;
using Microsoft.Data.SqlClient;

namespace Asap.Tests.Sql;

[TestClass]
[DoNotParallelize]
public sealed class EmailDefaultsDeploymentTests
{
    private string databaseName = null!;
    private string master = null!;
    private string target = null!;

    [TestInitialize]
    public void Initialize()
    {
        master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        databaseName = $"AsapEmailDefaults_{Guid.NewGuid():N}";
        target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        Deploy();
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        Assert.IsTrue(databaseName.StartsWith("AsapEmailDefaults_", StringComparison.Ordinal));
        await using var connection = new SqlConnection(master);
        await connection.OpenAsync();
        var quoted = new SqlCommandBuilder().QuoteIdentifier(databaseName);
        await using var command = new SqlCommand(
            $"IF DB_ID(@name) IS NOT NULL BEGIN ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {quoted}; END;", connection);
        command.Parameters.AddWithValue("@name", databaseName);
        await command.ExecuteNonQueryAsync();
    }

    [TestMethod]
    public async Task FreshDacpacSeedsExactlyFiveOrderedTemplatesAndRedeploymentPreservesRenderedCopy()
    {
        var initial = await ReadTemplatesAsync();
        Assert.AreEqual(5, initial.Count);
        var patron = new PatronSnapshot(7, "20000000000007", "reader@example.org", "Ada", "Reader",
            null, null, 2, 2, "Library Two", null);
        foreach (var expected in BuiltInEmailTemplateExpectations.All)
        {
            var row = initial.Single(item => item.Key == expected.Key);
            Assert.AreEqual(1, row.OrganizationId);
            Assert.IsNull(row.SourceId);
            Assert.AreEqual(expected.Name, row.Name);
            Assert.AreEqual(expected.Subject, row.Subject);
            Assert.AreEqual(expected.Body, row.Body);
            Assert.AreEqual(expected.SortOrder, row.SortOrder);
            Assert.IsFalse(row.Hidden);
            Assert.IsFalse(row.Custom);
            var template = new EffectiveEmailTemplate(row.Key, row.Subject!, row.Body!);
            var rendered = PatronEmailTemplateRenderer.Render(template, patron,
                "Sample <Title>", "Writer & Editor", "Print", patron.Barcode);
            string ReplaceTokens(string text, bool escape)
            {
                foreach (var (token, value) in new[]
                {
                    ("name", "Ada Reader"), ("title", "Sample <Title>"),
                    ("author", "Writer & Editor"), ("format", "Print"), ("barcode", patron.Barcode)
                })
                {
                    text = text.Replace("{{" + token + "}}", escape ? HtmlEncoder.Default.Encode(value) : value,
                        StringComparison.Ordinal);
                }
                return text;
            }
            Assert.AreEqual(ReplaceTokens(expected.Subject, false), rendered.Subject, row.Key);
            Assert.AreEqual(ReplaceTokens(expected.Body, false), rendered.BodyText, row.Key);
            Assert.AreEqual(ReplaceTokens(expected.Body, true).Replace("\n", "<br>", StringComparison.Ordinal),
                rendered.BodyHtml, row.Key);
            var missingAuthor = PatronEmailTemplateRenderer.Render(template, patron,
                "Sample", null, "Print", patron.Barcode);
            Assert.IsFalse(missingAuthor.BodyText.Contains(" by ", StringComparison.Ordinal), row.Key);
            Assert.IsFalse(missingAuthor.BodyText.Contains("{{", StringComparison.Ordinal), row.Key);
        }
        var rejection = RejectionTemplatePolicy.Default([], 2);
        var expectedRejection = BuiltInEmailTemplateExpectations.All.Single(item => item.Key == "rejected");
        Assert.AreEqual(expectedRejection.Subject, rejection.Subject);
        Assert.AreEqual(expectedRejection.Body, rejection.Body);
        Deploy();
        CollectionAssert.AreEqual(initial, await ReadTemplatesAsync(), "Repeat deployment must preserve IDs, versions and all copy.");
        Deploy();
        CollectionAssert.AreEqual(initial, await ReadTemplatesAsync());
    }

    [TestMethod]
    public async Task UpgradeInsertsOnlyMissingDefaultsPreservesLineageAndRetiresOnlyLibraryTransportTokens()
    {
        await using var connection = new SqlConnection(target);
        await connection.OpenAsync();
        await ExecuteAsync(connection, """
            DELETE FROM [asap].[EmailTemplate] WHERE [TemplateKey] IN (N'hold_placed', N'rejected');
            UPDATE [asap].[EmailTemplate] SET [BodyTemplate] = N'Old seeded automatic hold promise {{title}}',
                [DisplayName] = N'Administrator receipt', [SortOrder] = 99 WHERE [TemplateKey] = N'suggestion_submitted';
            UPDATE [asap].[EmailTemplate] SET [SubjectTemplate] = N'', [BodyTemplate] = N'   ',
                [IsHidden] = 1 WHERE [TemplateKey] = N'purchase_approved';
            UPDATE [asap].[EmailTemplate] SET [SubjectTemplate] = N'Custom title {{title}}', [BodyTemplate] = N''
                WHERE [TemplateKey] = N'already_owned';
            INSERT INTO [asap].[Organization]
                ([Id], [DisplayName], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
            VALUES (2, N'Upgrade library', 2, 1, 1), (3, N'Inactive library', 2, 1, 0);
            INSERT INTO [asap].[EmailTemplate]
                ([OrganizationId], [TemplateKey], [SourceTemplateId], [DisplayName], [SubjectTemplate], [BodyTemplate], [IsHidden], [IsCustom], [SortOrder])
            SELECT 2, [TemplateKey], [Id], NULL, NULL, N'Library body {{title}}', 1, 0, 0
            FROM [asap].[EmailTemplate] WHERE [OrganizationId] = 1 AND [TemplateKey] = N'suggestion_submitted';
            INSERT INTO [asap].[EmailTemplate]
                ([OrganizationId], [TemplateKey], [SourceTemplateId], [SubjectTemplate], [BodyTemplate], [IsHidden], [IsCustom], [SortOrder])
            SELECT 2, [TemplateKey], [Id], N'Library subject', NULL, 0, 0, 0
            FROM [asap].[EmailTemplate] WHERE [OrganizationId] = 1 AND [TemplateKey] = N'purchase_approved';
            INSERT INTO [asap].[EmailTemplate]
                ([OrganizationId], [TemplateKey], [DisplayName], [SubjectTemplate], [BodyTemplate], [IsHidden], [IsCustom], [SortOrder])
            VALUES (1, N'rejection:custom', N'Custom system', N'System custom', N'System body', 0, 0, 60),
                   (2, N'rejection:local', N'Custom library', N'Library custom', N'Library body', 0, 1, 70);
            UPDATE [asap].[EmailSettings] SET [ProtectedServerToken] = N'synthetic-system-ciphertext',
                [FromAddress] = N'system@example.org', [FromName] = N'System sender' WHERE [OrganizationId] = 1;
            INSERT INTO [asap].[EmailSettings]
                ([OrganizationId], [ProtectedServerToken], [FromAddress], [FromName], [UpdatedUtc])
            VALUES (2, N'synthetic-obsolete-library-ciphertext', N'library@example.org', N'Library sender', '2026-01-02T03:04:05'),
                   (3, NULL, N'inactive@example.org', N'Inactive sender', '2026-01-02T03:04:05');
            """);
        var before = await ReadTemplatesAsync();
        var senderBefore = await ReadSendersAsync(connection);
        Deploy();
        var upgraded = await ReadTemplatesAsync();
        CollectionAssert.AreEqual(before, upgraded.Where(row => before.Any(old => old.Id == row.Id)).ToList(),
            "Upgrade must retain each saved identity, nullable/blank field, version, hidden state and lineage.");
        Assert.AreEqual(before.Count + 2, upgraded.Count);
        foreach (var key in new[] { "hold_placed", "rejected" })
        {
            var expected = BuiltInEmailTemplateExpectations.All.Single(item => item.Key == key);
            var inserted = upgraded.Single(item => item.OrganizationId == 1 && item.Key == key);
            Assert.AreEqual(expected.Subject, inserted.Subject);
            Assert.AreEqual(expected.Body, inserted.Body);
        }
        var senderAfter = await ReadSendersAsync(connection);
        CollectionAssert.AreEqual(senderBefore, senderAfter);
        Assert.AreEqual(0, Convert.ToInt32(await new SqlCommand(
            "SELECT COUNT(*) FROM [asap].[EmailSettings] WHERE [OrganizationId] <> 1 AND [ProtectedServerToken] IS NOT NULL;", connection).ExecuteScalarAsync()));
        Assert.AreEqual("synthetic-system-ciphertext", await new SqlCommand(
            "SELECT [ProtectedServerToken] FROM [asap].[EmailSettings] WHERE [OrganizationId] = 1;", connection).ExecuteScalarAsync());
        var versions = await ReadEmailVersionsAsync(connection);
        Deploy();
        CollectionAssert.AreEqual(upgraded, await ReadTemplatesAsync());
        CollectionAssert.AreEqual(senderAfter, await ReadSendersAsync(connection));
        CollectionAssert.AreEqual(versions, await ReadEmailVersionsAsync(connection),
            "Repeated credential cleanup must not touch already clean rows.");
    }

    private void Deploy() => new DacpacDeploymentService().Deploy(target, TestArtifactPaths.FindDacpac());

    private sealed record TemplateRow(long Id, int OrganizationId, string Key, long? SourceId, string? Name,
        string? Subject, string? Body, bool Hidden, bool Custom, int SortOrder, string Version);

    private async Task<List<TemplateRow>> ReadTemplatesAsync()
    {
        await using var connection = new SqlConnection(target);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT [Id], [OrganizationId], [TemplateKey], [SourceTemplateId], [DisplayName],
                   [SubjectTemplate], [BodyTemplate], [IsHidden], [IsCustom], [SortOrder], [RowVersion]
            FROM [asap].[EmailTemplate] ORDER BY [Id];
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<TemplateRow>();
        while (await reader.ReadAsync())
        {
            string? Text(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
            rows.Add(new(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3), Text(4), Text(5), Text(6),
                reader.GetBoolean(7), reader.GetBoolean(8), reader.GetInt32(9), Convert.ToBase64String((byte[])reader[10])));
        }
        return rows;
    }

    private static async Task<List<string>> ReadSendersAsync(SqlConnection connection)
    {
        await using var command = new SqlCommand("""
            SELECT [OrganizationId], [FromAddress], [FromName], [UpdatedUtc]
            FROM [asap].[EmailSettings] ORDER BY [OrganizationId];
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add($"{reader.GetInt32(0)}|{reader.GetString(1)}|{reader.GetString(2)}|{reader.GetDateTime(3):O}");
        }
        return rows;
    }

    private static async Task<List<string>> ReadEmailVersionsAsync(SqlConnection connection)
    {
        await using var command = new SqlCommand("SELECT CONVERT(varchar(20), [RowVersion], 2) FROM [asap].[EmailSettings] ORDER BY [OrganizationId];", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }
        return rows;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
