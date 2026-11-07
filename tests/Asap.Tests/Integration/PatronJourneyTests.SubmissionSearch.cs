using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    private const int SearchLibrary = 46701, OtherSearchLibrary = 46702, InactiveSearchLibrary = 46703;
    private const long SearchBigId = 9_223_372_036_854_774_467;
    private static readonly string[] SearchStages = ["suggestion", "outstanding_purchase", "pending_hold", "hold_placed", "closed"];

    [TestMethod]
    public async Task SubmissionSearchHttpScopesCountsLiteralMatchingRankingAndPagingUseRealSql()
    {
        using var client = factory!.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        await SeedSubmissionSearchAsync();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        try
        {
            var before = await SearchStateAsync();
            var provider = factory.Services.GetRequiredService<DeterministicTestingPatronProvider>();
            var providerCalls = provider.Calls.Count;
            var all = await SearchAsync(client, "  NEEDLE   ", "all");
            Assert.AreEqual("NEEDLE", all.Query);
            Assert.AreEqual(37, all.Total);
            Assert.HasCount(12, all.Items);
            Assert.AreEqual(7, all.Groups.Count(item => item.Count > 0));
            Assert.IsFalse(all.Items.Any(item => item.LibraryOrgId == InactiveSearchLibrary));
            var scoped = await SearchAsync(client, "needle", SearchLibrary.ToString(CultureInfo.InvariantCulture));
            Assert.AreEqual(36, scoped.Total);
            CollectionAssert.AreEqual(new[] { 28, 1, 1, 1, 2, 2, 1 }, scoped.Groups.Select(item => item.Count).ToArray());
            var pages = new List<SubmissionSearchItem>();
            for (var page = 1; page <= 3; page++)
            {
                pages.AddRange((await SearchAsync(client, "needle", SearchLibrary.ToString(CultureInfo.InvariantCulture), page: page)).Items);
            }
            Assert.HasCount(36, pages);
            Assert.AreEqual(36, pages.Select(item => (item.Type, item.Id)).Distinct().Count());
            CollectionAssert.AreEqual(pages.Select(item => item.Type + ":" + item.Id).ToArray(),
                (await SearchAsync(client, "needle", SearchLibrary.ToString(CultureInfo.InvariantCulture), size: 50)).Items.Select(item => item.Type + ":" + item.Id).ToArray());
            var copyClosed = await SearchAsync(client, "needle", "all", "copy:closed");
            Assert.AreEqual(37, copyClosed.Total); Assert.AreEqual(1, copyClosed.FilteredTotal);
            Assert.AreEqual("copy", copyClosed.Items.Single().Type); Assert.IsNull(copyClosed.Items.Single().Patron);
            foreach (var term in new[] { "  ADA   O'BRIEN  ", "ada", "O'Brien", "Éloïse", "literal%_[~", "<img src=x>", "needle ada" })
            {
                var match = await SearchAsync(client, term, SearchLibrary.ToString(CultureInfo.InvariantCulture));
                Assert.IsTrue(match.Total > 0, term);
                Assert.IsTrue(match.Items.All(item => item.Type == "title"), term);
            }
            Assert.AreEqual(1, (await SearchAsync(client, "literal%_[~", "all")).Total, "SQL wildcard input is literal.");
            var id = SearchBigId.ToString(CultureInfo.InvariantCulture);
            var exact = await SearchAsync(client, id, "all");
            Assert.AreEqual(2, exact.Total);
            CollectionAssert.AreEqual(new[] { "copy:" + id, "title:" + id }, exact.Items.Select(item => item.Type + ":" + item.Id).ToArray());
            Assert.AreEqual(0, (await SearchAsync(client, id + "000", "all")).Total,
                "An overlong numeric token must not be truncated to an existing bigint identity by SQL parameter mapping.");
            foreach (var term in new[] { "EXACT-BARCODE", "EXACT-IDENTIFIER", "467999" })
            {
                var match = await SearchAsync(client, term, "all");
                Assert.AreEqual("title", match.Items[0].Type, term);
                Assert.AreEqual(id, match.Items[0].Id, term);
            }
            var fullName = await SearchAsync(client, "Ada O'Brien", "all");
            Assert.AreEqual(2, fullName.Total, "Two real submissions of the same title/patron remain distinct.");
            Assert.AreEqual(0, (await SearchAsync(client, "Ada nonexistent", "all")).Total);
            Assert.AreEqual(0, (await SearchAsync(client, " \t \n ", "all")).Total);
            CollectionAssert.AreEqual(before, await SearchStateAsync(), "Search must not change rows, rowversions, journals, audits or jobs.");
            Assert.AreEqual(providerCalls, provider.Calls.Count);
            Assert.HasCount(0, dispatcher!.EnqueuedIds);
        }
        finally { await ClearSubmissionSearchAsync(); }
    }

    [TestMethod]
    [DataRow(StaffRole.Staff)]
    [DataRow(StaffRole.Admin)]
    public async Task SubmissionSearchHttpRejectsInvalidInputAndRechecksActiveIdentityLibraryAndDetails(string role)
    {
        using var superClient = factory!.CreateClient();
        var super = await ReadConfiguredSuperAdminAsync();
        await SeedSubmissionSearchAsync();
        var row = await CreateCorrectiveStaffAsync(super, role, SearchLibrary);
        var ordinary = await ReadCorrectiveStaffAsync(row);
        using var client = factory.CreateClient();
        AddTestingStaffHeaders(client, ordinary.Id, ordinary.EntraTenantId, ordinary.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        AddTestingStaffHeaders(superClient, super.Id, super.EntraTenantId, super.AuthenticationEmail);
        superClient.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(superClient));
        try
        {
            foreach (var scope in new[] { "all", OtherSearchLibrary.ToString(CultureInfo.InvariantCulture), "999999" })
            {
                var result = await SearchAsync(client, "needle", scope);
                Assert.AreEqual(SearchLibrary.ToString(CultureInfo.InvariantCulture), result.Scope);
                Assert.AreEqual(36, result.Total);
                Assert.HasCount(1, result.Libraries);
                Assert.IsTrue(result.Items.All(item => item.LibraryOrgId == SearchLibrary));
            }
            foreach (var input in new[] {
                new SubmissionSearchInput(new string('x', 161)), new SubmissionSearchInput(string.Join(' ', Enumerable.Repeat("x", 13))),
                new SubmissionSearchInput("needle", "system"), new SubmissionSearchInput("needle", "1"),
                new SubmissionSearchInput("needle", "bad"), new SubmissionSearchInput("needle", "all", "title:open"),
                new SubmissionSearchInput("needle", Page: 0), new SubmissionSearchInput("needle", PageSize: 51),
                new SubmissionSearchInput("needle", InactiveSearchLibrary.ToString(CultureInfo.InvariantCulture)) })
            {
                using var response = await superClient.PostAsJsonAsync("/api/asap/staff/search", input);
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
            }
            using var missingToken = factory.CreateClient();
            AddTestingStaffHeaders(missingToken, super.Id, super.EntraTenantId, super.AuthenticationEmail);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await missingToken.PostAsJsonAsync("/api/asap/staff/search", new { query = "needle" })).StatusCode);
            using var unauthenticated = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            Assert.AreEqual(HttpStatusCode.Redirect, (await unauthenticated.PostAsJsonAsync("/api/asap/staff/search", new { query = "needle" })).StatusCode);
            using var missingStaff = factory.CreateClient();
            AddTestingStaffHeaders(missingStaff, long.MaxValue, super.EntraTenantId, super.AuthenticationEmail);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await missingStaff.PostAsJsonAsync("/api/asap/staff/search", new { query = "needle" })).StatusCode);
            var other = (await SearchAsync(superClient, "needle", OtherSearchLibrary.ToString(CultureInfo.InvariantCulture))).Items.Single();
            Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync($"/api/asap/staff/title-requests/{other.Id}")).StatusCode);
            var copy = (await SearchAsync(superClient, "needle", SearchLibrary.ToString(CultureInfo.InvariantCulture), "copy:open")).Items[0];
            Assert.AreEqual(HttpStatusCode.NotFound, (await superClient.GetAsync($"/api/asap/staff/additional-copies/{copy.Id}?scope={OtherSearchLibrary}")).StatusCode);
            await ExecuteNonQueryAsync("UPDATE [asap].[StaffUser] SET [OrganizationId]=@other WHERE [Id]=@id;", ("@other", OtherSearchLibrary), ("@id", ordinary.Id));
            Assert.AreEqual(1, (await SearchAsync(client, "needle", "all")).Total, "Current SQL library supersedes cookie/header scope.");
            Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync($"/api/asap/staff/additional-copies/{copy.Id}")).StatusCode);
            await ExecuteNonQueryAsync("UPDATE [asap].[StaffUser] SET [IsActive]=0 WHERE [Id]=@id;", ("@id", ordinary.Id));
            Assert.IsTrue((await client.PostAsJsonAsync("/api/asap/staff/search", new { query = "needle" })).StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
            Assert.IsTrue((await client.GetAsync($"/api/asap/staff/additional-copies/{copy.Id}")).StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
            await ExecuteNonQueryAsync("UPDATE [asap].[Organization] SET [IsActive]=0 WHERE [Id]=@id;", ("@id", SearchLibrary));
            Assert.AreEqual(1, (await SearchAsync(superClient, "needle", "all")).Total);
            Assert.AreEqual(HttpStatusCode.NotFound, (await superClient.GetAsync($"/api/asap/staff/additional-copies/{copy.Id}?scope=all")).StatusCode);
            await ExecuteNonQueryAsync("UPDATE [asap].[StaffUser] SET [IsActive]=1 WHERE [Id]=@id; UPDATE [asap].[Organization] SET [IsActive]=0 WHERE [Id]=@other;", ("@id", ordinary.Id), ("@other", OtherSearchLibrary));
            Assert.AreEqual(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/asap/staff/search", new { query = "needle" })).StatusCode);
            Assert.AreEqual(0, (await SearchAsync(superClient, "needle", "all")).Total);
        }
        finally { await DeactivateCorrectiveStaffAsync(ordinary.Id); await ClearSubmissionSearchAsync(); }
    }

    [TestMethod]
    public async Task SubmissionSearchMeasuredSqlIsBoundedReadOnlyAndHonorsCancellation()
    {
        var capture = new SearchSqlCapture();
        await using var scoped = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContextFactory<AsapDbContext>(options => options.AddInterceptors(capture))));
        using var client = scoped.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        await SeedSubmissionSearchAsync(performance: true);
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        var directory = SearchEvidenceDirectory("sql");
        var timings = new List<object>();
        try
        {
            foreach (var (query, scope, label) in new[] { ("searchperf", "all", "first-context all text"), ("searchperf", "all", "warm all text"),
                ("searchperf 09999", "all", "warm multiword"), ("searchperf", SearchLibrary.ToString(CultureInfo.InvariantCulture), "warm library text"),
                ("perf-09999", "all", "warm exact barcode"), ("%_[", "all", "warm literal match"),
                ("absent-searchperf-token", "all", "warm no match") })
            {
                capture.Queries.Clear();
                var watch = Stopwatch.StartNew();
                var result = await SearchAsync(client, query, scope, size: 25);
                watch.Stop();
                Assert.IsTrue(result.Items.Count <= 25);
                Assert.HasCount(2, capture.Queries, "One UNION count and one bounded UNION page; no result-level database calls.");
                Assert.IsTrue(capture.Queries[1].Text.Contains("FETCH NEXT", StringComparison.Ordinal));
                timings.Add(new { label, query, scope, milliseconds = watch.Elapsed.TotalMilliseconds, result.Total, result.FilteredTotal, items = result.Items.Count });
                await File.WriteAllTextAsync(Path.Combine(directory, $"query-{timings.Count}.sql"), string.Join("\n\n", capture.Queries.Select(item => item.Text)));
                if (timings.Count == 2)
                {
                    await WriteSearchPlanAsync(capture.Queries[1], Path.Combine(directory, "warm-all-page-plan.xml"));
                }
            }
            var service = scoped.Services.GetRequiredService<SubmissionSearchService>();
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.SearchAsync(actor, LibraryScope.All,
                new("searchperf"), "searchperf", cancellation.Token));
            await File.WriteAllTextAsync(Path.Combine(directory, "timings.json"), JsonSerializer.Serialize(new { titles = 10035, copies = 2003, timings }, new JsonSerializerOptions { WriteIndented = true }));
            TestContext.WriteLine($"Search SQL/performance evidence: {directory}");
        }
        finally { await ClearSubmissionSearchAsync(); }
    }

    private static async Task<SubmissionSearchResult> SearchAsync(HttpClient client, string query, string scope,
        string? group = null, int page = 1, int size = 12)
    {
        using var response = await client.PostAsJsonAsync("/api/asap/staff/search", new SubmissionSearchInput(query, scope, group, page, size));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SubmissionSearchResult>())!;
    }

    private async Task SeedSubmissionSearchAsync(bool performance = false)
    {
        await ExecuteNonQueryAsync("""
            UPDATE [asap].[Organization] SET [IsActive]=CASE WHEN [Id]=46703 THEN 0 ELSE 1 END WHERE [Id] IN (46701,46702,46703);
            INSERT INTO [asap].[Organization] ([Id],[DisplayName],[OrganizationCodeId],[ParentOrganizationId],[IsActive])
            SELECT * FROM (VALUES (46701,N'Search Library',2,1,1),(46702,N'Other Search Library',2,1,1),(46703,N'Inactive Search Library',2,1,0)) o([Id],[Name],[Code],[Parent],[Active])
            WHERE NOT EXISTS (SELECT 1 FROM [asap].[Organization] WHERE [Id]=o.[Id]);
            DECLARE @format bigint = (SELECT [Id] FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId]=1 AND [Code]='book');
            INSERT INTO [asap].[TitleRequest] ([LibraryOrganizationId],[Barcode],[Title],[Author],[NameFirst],[NameLast],[Identifier],[MaterialFormatId],[Status],[BibId],[AutoHold],[CloseReason],[CreatedUtc],[UpdatedUtc])
            SELECT 46701,CONCAT('needle-',s.[Status]),N'Needle duplicate',N'Éloïse author',N'Ada',N'O''Brien',
                CASE WHEN s.[Status]='closed' THEN N'literal%_[~ <img src=x>' ELSE NULL END,@format,s.[Status],
                CASE WHEN s.[Status] IN ('pending_hold','hold_placed') THEN 467123 ELSE NULL END,0,CASE WHEN s.[Status]='closed' THEN 'manual' ELSE NULL END,SYSUTCDATETIME(),SYSUTCDATETIME()
            FROM (VALUES ('suggestion'),('outstanding_purchase'),('pending_hold'),('hold_placed'),('closed')) s([Status]);
            UPDATE [asap].[TitleRequest] SET [NameFirst]=NULL,[NameLast]=NULL WHERE [LibraryOrganizationId]=46701 AND [Status]<>'closed';
            INSERT INTO [asap].[TitleRequest] ([LibraryOrganizationId],[Barcode],[Title],[MaterialFormatId],[Status],[AutoHold],[CreatedUtc],[UpdatedUtc])
            SELECT 46701,CONCAT('needle-page-',n),N'Needle page',@format,'suggestion',0,SYSUTCDATETIME(),SYSUTCDATETIME()
            FROM (SELECT TOP (27) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) n FROM sys.all_objects) n;
            INSERT INTO [asap].[TitleRequest] ([LibraryOrganizationId],[Barcode],[Title],[MaterialFormatId],[Status],[AutoHold],[CloseReason],[CreatedUtc],[UpdatedUtc]) VALUES
                (46702,N'other-needle',N'Needle other',@format,'closed',0,'manual',SYSUTCDATETIME(),SYSUTCDATETIME()),
                (46703,N'needle',N'Needle',@format,'closed',0,'manual',SYSUTCDATETIME(),SYSUTCDATETIME());
            INSERT INTO [asap].[AdditionalCopyRequest] ([LibraryOrganizationId],[Title],[Author],[BibId],[Status],[ClosedUtc],[CreatedUtc],[UpdatedUtc]) VALUES
                (46701,N'Needle copy',N'Copy author',467222,'open',NULL,SYSUTCDATETIME(),SYSUTCDATETIME()),
                (46701,N'Needle copy',NULL,467223,'closed',SYSUTCDATETIME(),SYSUTCDATETIME(),SYSUTCDATETIME());
            """);
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        foreach (var table in new[] { "TitleRequest", "AdditionalCopyRequest" })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT CONVERT(bigint,IDENT_CURRENT(N'[asap].[{table}]'));";
            var previous = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            var columns = table == "TitleRequest" ? "[Barcode],[NameFirst],[NameLast],[Identifier],[MaterialFormatId],[AutoHold],[CloseReason]," : "";
            var values = table == "TitleRequest" ? "N'EXACT-BARCODE',N'Ada',N'O''Brien',N'EXACT-IDENTIFIER',(SELECT [Id] FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId]=1 AND [Code]='book'),0,'manual'," : "";
            command.CommandText = $"""
                SET IDENTITY_INSERT [asap].[{table}] ON;
                INSERT INTO [asap].[{table}] ([Id],[LibraryOrganizationId],[Title],{columns}[BibId],[Status],[CreatedUtc],[UpdatedUtc])
                VALUES ({SearchBigId},46701,N'Needle duplicate',{values}{(table == "TitleRequest" ? 467999 : 467998)},'{(table == "TitleRequest" ? "closed" : "open")}',SYSUTCDATETIME(),SYSUTCDATETIME());
                SET IDENTITY_INSERT [asap].[{table}] OFF;
                DBCC CHECKIDENT ('[asap].[{table}]', RESEED, {previous});
                """;
            await command.ExecuteNonQueryAsync();
        }
        if (performance)
        {
            await ExecuteNonQueryAsync("""
                DECLARE @format bigint = (SELECT [Id] FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId]=1 AND [Code]='book');
                INSERT INTO [asap].[TitleRequest] ([LibraryOrganizationId],[Barcode],[Title],[Author],[MaterialFormatId],[Status],[AutoHold],[CloseReason],[CreatedUtc],[UpdatedUtc])
                SELECT CASE WHEN n%2=0 THEN 46701 ELSE 46702 END,CONCAT('perf-',FORMAT(n,'00000')),CONCAT('Searchperf title ',FORMAT(n,'00000')),N'Performance author',@format,
                    CASE n%5 WHEN 0 THEN 'suggestion' WHEN 1 THEN 'outstanding_purchase' WHEN 2 THEN 'pending_hold' WHEN 3 THEN 'hold_placed' ELSE 'closed' END,
                    0,CASE WHEN n%5=4 THEN 'manual' ELSE NULL END,SYSUTCDATETIME(),SYSUTCDATETIME()
                FROM (SELECT TOP (10000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) n FROM sys.all_objects a CROSS JOIN sys.all_objects b) n;
                INSERT INTO [asap].[AdditionalCopyRequest] ([LibraryOrganizationId],[Title],[BibId],[Status],[ClosedUtc],[CreatedUtc],[UpdatedUtc])
                SELECT CASE WHEN n%2=0 THEN 46701 ELSE 46702 END,CONCAT('Searchperf copy ',n),467000+n,CASE WHEN n%2=0 THEN 'open' ELSE 'closed' END,
                    CASE WHEN n%2=1 THEN SYSUTCDATETIME() ELSE NULL END,SYSUTCDATETIME(),SYSUTCDATETIME()
                FROM (SELECT TOP (2000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) n FROM sys.all_objects) n;
                """);
        }
    }

    private async Task ClearSubmissionSearchAsync() => await ExecuteNonQueryAsync("""
        DELETE FROM [asap].[AdditionalCopyRequest] WHERE [LibraryOrganizationId] IN (46701,46702,46703);
        DELETE FROM [asap].[TitleRequest] WHERE [LibraryOrganizationId] IN (46701,46702,46703);
        UPDATE [asap].[Organization] SET [IsActive]=0 WHERE [Id] IN (46701,46702,46703);
        """);

    private static string SearchEvidenceDirectory(string kind)
    {
        var directory = Path.Combine(Environment.GetEnvironmentVariable("ASAP_SEARCH_EVIDENCE_DIR") ?? Path.Combine(Path.GetTempPath(), "asap-search-evidence"), kind + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task<byte[]> SearchStateAsync()
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT HASHBYTES('SHA2_256',CONVERT(varbinary(max),CONCAT(
                (SELECT * FROM [asap].[TitleRequest] ORDER BY [Id] FOR JSON PATH,INCLUDE_NULL_VALUES),
                (SELECT * FROM [asap].[AdditionalCopyRequest] ORDER BY [Id] FOR JSON PATH,INCLUDE_NULL_VALUES),
                (SELECT COUNT(*) FROM [asap].[TitleRequestEvent]),(SELECT COUNT(*) FROM [asap].[EmailOutbox]),
                (SELECT COUNT(*) FROM [asap].[AdministrativeAudit]),(SELECT COUNT(*) FROM [HangFire].[Job]))));
            """;
        return (byte[])(await command.ExecuteScalarAsync())!;
    }

    private sealed record CapturedSearchSql(string Text, SqlParameter[] Parameters);
    private sealed class SearchSqlCapture : DbCommandInterceptor
    {
        public List<CapturedSearchSql> Queries { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UNION ALL", StringComparison.Ordinal))
            {
                Queries.Add(new(command.CommandText, command.Parameters.Cast<SqlParameter>().Select(item => (SqlParameter)((ICloneable)item).Clone()).ToArray()));
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static async Task WriteSearchPlanAsync(CapturedSearchSql sql, string path)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SET STATISTICS XML ON;\n" + sql.Text;
        command.Parameters.AddRange(sql.Parameters);
        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            if (reader.FieldCount == 1 && reader.GetName(0).Contains("Showplan", StringComparison.OrdinalIgnoreCase) && await reader.ReadAsync())
            {
                await File.WriteAllTextAsync(path, reader.GetString(0));
            }
        } while (await reader.NextResultAsync());
    }
}
