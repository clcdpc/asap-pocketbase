using System.Collections;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

// Temporary, opt-in investigation only. Shipped search/query/schema code is unchanged.
public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task SubmissionSearchIssue485Diagnostics()
    {
        Assert.AreEqual("1", Environment.GetEnvironmentVariable("ASAP_485_DIAGNOSTICS"));
        Assert.IsTrue(databaseName.StartsWith("AsapPatronJourney_", StringComparison.Ordinal));
        Assert.IsTrue(new SqlConnectionStringBuilder(databaseConnectionString).DataSource.StartsWith("localhost,", StringComparison.Ordinal));
        var mode = Environment.GetEnvironmentVariable("ASAP_485_MODE") ?? "baseline";
        Assert.IsTrue(mode is "baseline" or "trace" or "control");
        var directory = SearchEvidenceDirectory(mode);
        var start = Stopwatch.GetTimestamp();
        await SeedSubmissionSearchAsync(performance: true);
        var actor = await ReadConfiguredSuperAdminAsync();
        var seedMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        await SaveInvestigationJsonAsync(directory, "identity.json", new
        {
            mode, processId = Environment.ProcessId, framework = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            processors = Environment.ProcessorCount, serverGc = GCSettings.IsServerGC, seedMilliseconds,
            applicationSha = Environment.GetEnvironmentVariable("ASAP_485_APPLICATION_SHA"),
            toolingSha = Environment.GetEnvironmentVariable("GITHUB_SHA"),
            note = "Fresh test process; fixture startup/auth and seeding precede first search. Seeded SQL buffers are not cold. No replay, hint, cache flush, or query rewrite."
        });
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT (SELECT COUNT(*) FROM [asap].[TitleRequest]) titles,(SELECT COUNT(*) FROM [asap].[AdditionalCopyRequest]) copies FOR JSON PATH,WITHOUT_ARRAY_WRAPPER;";
            var counts = (string)(await command.ExecuteScalarAsync())!;
            await File.WriteAllTextAsync(Path.Combine(directory, "dataset.json"), counts);
            using var parsed = JsonDocument.Parse(counts);
            Assert.AreEqual(10035, parsed.RootElement.GetProperty("titles").GetInt32());
            Assert.AreEqual(2003, parsed.RootElement.GetProperty("copies").GetInt32());
        }
        try
        {
            // A second host in the same process separates search startup from process-wide caches.
            for (var host = 1; host <= 2; host++)
            {
                var capture = new InvestigationCapture(mode != "control");
                using var observer = new InvestigationEfObserver(mode != "control");
                var hostWatch = Stopwatch.StartNew();
                await using var scoped = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                    services.AddDbContextFactory<AsapDbContext>(options => options.AddInterceptors(capture))));
                using var client = scoped.CreateClient();
                AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
                client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
                hostWatch.Stop();
                var before = await SearchStateAsync();
                var provider = scoped.Services.GetRequiredService<DeterministicTestingPatronProvider>();
                var providerCalls = provider.Calls.Count;
                var hostDirectory = Path.Combine(directory, "host-" + host);
                Directory.CreateDirectory(hostDirectory);
                await SaveInvestigationJsonAsync(hostDirectory, "startup.json", new { milliseconds = hostWatch.Elapsed.TotalMilliseconds, metrics = InvestigationProcessMetrics() });
                await SaveInvestigationSqlStateAsync(hostDirectory, "before");
                var trace = mode == "trace" ? await StartInvestigationTraceAsync(hostDirectory) : null;
                var results = new List<object>();
                var stablePages = new Dictionary<string, string[]>();
                try
                {
                    // Keep all seven original classes. Three warm rounds expose variability.
                    for (var round = 0; round < 3; round++)
                    {
                        var classes = new[] { ("searchperf", "all", "all text"),
                            ("searchperf", "all", "warm all text"),
                            ("searchperf 09999", "all", "multiword"),
                            ("searchperf", SearchLibrary.ToString(CultureInfo.InvariantCulture), "library text"),
                            ("perf-09999", "all", "exact barcode"), ("%_[", "all", "literal match"),
                            ("absent-searchperf-token", "all", "no match") };
                        foreach (var (query, scope, label) in classes)
                        {
                            capture.Queries.Clear();
                            observer.Events.Clear();
                            var beforeMetrics = InvestigationProcessMetrics();
                            var startedUtc = DateTimeOffset.UtcNow;
                            var watch = Stopwatch.StartNew();
                            var result = await SearchAsync(client, query, scope, size: 25);
                            watch.Stop();
                            var finishedUtc = DateTimeOffset.UtcNow;
                            var afterMetrics = InvestigationProcessMetrics();
                            Assert.HasCount(2, capture.Queries);
                            Assert.IsTrue(capture.Queries[0].Text.Contains("GROUP BY", StringComparison.Ordinal));
                            Assert.IsTrue(capture.Queries[1].Text.Contains("FETCH NEXT", StringComparison.Ordinal));
                            Assert.IsTrue(result.Items.Count <= 25);
                            // The numeric multiword token also matches native ID 9999, as in the original fixture.
                            var expected = label switch { "all text" or "warm all text" => 12000, "multiword" => 2, "exact barcode" or "literal match" => 1, "library text" => 6000, "no match" => 0, _ => -1 };
                            Assert.AreEqual(expected, result.Total, label);
                            Assert.AreEqual(expected, result.FilteredTotal, label);
                            Assert.AreEqual(expected, result.Groups.Sum(group => group.Count), label);
                            var identities = result.Items.Select(item => item.Type + ":" + item.Id).ToArray();
                            var stableKey = query + "|" + scope;
                            if (stablePages.TryGetValue(stableKey, out var previousIdentities))
                            {
                                CollectionAssert.AreEqual(previousIdentities, identities, "Stable repeated paging.");
                            }
                            stablePages[stableKey] = identities;
                            var sequence = results.Count + 1;
                            results.Add(new
                            {
                                sequence, round, label = round == 0 && sequence == 1 ? "first-context all text" : label,
                                query, scope, startedUtc, finishedUtc, httpMilliseconds = watch.Elapsed.TotalMilliseconds,
                                result.Total, result.FilteredTotal, items = result.Items.Count,
                                identities,
                                beforeMetrics, afterMetrics, efEvents = observer.Events.ToArray(),
                                commands = capture.Queries.Select(item => item.Evidence()).ToArray()
                            });
                            // Everything written here is synthetic. No plan replay contaminates warm timings.
                            for (var queryIndex = 0; queryIndex < capture.Queries.Count; queryIndex++)
                            {
                                await File.WriteAllTextAsync(Path.Combine(hostDirectory, $"query-{sequence:00}-{(queryIndex == 0 ? "count" : "page")}.sql"), capture.Queries[queryIndex].Text);
                            }
                            await SaveInvestigationJsonAsync(hostDirectory, "requests.json", results);
                        }
                    }
                }
                finally
                {
                    if (trace is not null)
                    {
                        await StopInvestigationTraceAsync(trace, hostDirectory);
                    }
                }
                await SaveInvestigationSqlStateAsync(hostDirectory, "after");
                CollectionAssert.AreEqual(before, await SearchStateAsync(), "Read-only search, including rowversions/journals/audits/jobs.");
                Assert.AreEqual(providerCalls, provider.Calls.Count);
                Assert.HasCount(0, dispatcher!.EnqueuedIds);
                using var cancellation = new CancellationTokenSource();
                await cancellation.CancelAsync();
                await Assert.ThrowsAsync<OperationCanceledException>(() => scoped.Services.GetRequiredService<SubmissionSearchService>()
                    .SearchAsync(actor, LibraryScope.All, new("searchperf"), "searchperf", cancellation.Token));
                await SaveInvestigationJsonAsync(hostDirectory, "assertions.json", new { readOnly = true, boundedTwoQueries = true, exactCounts = true, cancellation = true, providerCallsUnchanged = true });
            }
            TestContext.WriteLine("Issue485 evidence: " + directory);
        }
        finally
        {
            await ClearSubmissionSearchAsync();
        }
    }

    private static readonly JsonSerializerOptions InvestigationJsonOptions = new() { WriteIndented = true };
    private static Task SaveInvestigationJsonAsync(string directory, string name, object value) =>
        File.WriteAllTextAsync(Path.Combine(directory, name), JsonSerializer.Serialize(value, InvestigationJsonOptions));

    private static object InvestigationProcessMetrics()
    {
        using var process = Process.GetCurrentProcess();
        return new { cpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds, workingSetBytes = process.WorkingSet64,
            allocatedBytes = GC.GetTotalAllocatedBytes(), heapBytes = GC.GetTotalMemory(false),
            gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2),
            gcPauseMilliseconds = GC.GetTotalPauseDuration().TotalMilliseconds,
            jitMilliseconds = System.Runtime.JitInfo.GetCompilationTime().TotalMilliseconds,
            jitMethods = System.Runtime.JitInfo.GetCompiledMethodCount(), jitIlBytes = System.Runtime.JitInfo.GetCompiledILBytes() };
    }

    private sealed class InvestigationQuery(string text, Guid commandId, SqlParameter[] parameters)
    {
        public string Text { get; } = text;
        public Guid CommandId { get; } = commandId;
        public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
        public long Started { get; } = Stopwatch.GetTimestamp();
        public double? ExecuteReaderMilliseconds { get; set; }
        public double? ReaderLifetimeMilliseconds { get; set; }
        public object? Statistics { get; set; }
        public object Evidence() => new { CommandId, StartedUtc, ExecuteReaderMilliseconds, ReaderLifetimeMilliseconds, Statistics,
            parameters = parameters.Select(item => new { item.ParameterName, type = item.SqlDbType.ToString(), item.Size, value = item.Value is DBNull ? null : item.Value }) };
    }

    private sealed class InvestigationCapture(bool detailed) : DbCommandInterceptor
    {
        public List<InvestigationQuery> Queries { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[asap].[TitleRequest]", StringComparison.Ordinal) || command.CommandText.Contains("[asap].[AdditionalCopyRequest]", StringComparison.Ordinal))
            {
                if (detailed && command.Connection is SqlConnection connection)
                {
                    connection.StatisticsEnabled = true;
                    connection.ResetStatistics();
                }
                Queries.Add(new(command.CommandText, eventData.CommandId, command.Parameters.Cast<SqlParameter>().Select(item => (SqlParameter)((ICloneable)item).Clone()).ToArray()));
            }
            return ValueTask.FromResult(result);
        }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            var query = Queries.LastOrDefault(item => item.CommandId == eventData.CommandId);
            if (query is not null)
            {
                query.ExecuteReaderMilliseconds = eventData.Duration.TotalMilliseconds;
            }
            return ValueTask.FromResult(result);
        }
        public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
        {
            var query = Queries.LastOrDefault(item => item.CommandId == eventData.CommandId);
            if (query is not null)
            {
                query.ReaderLifetimeMilliseconds = Stopwatch.GetElapsedTime(query.Started).TotalMilliseconds;
                if (detailed && command.Connection is SqlConnection connection)
                {
                    query.Statistics = connection.RetrieveStatistics().Cast<DictionaryEntry>().ToDictionary(item => (string)item.Key, item => item.Value);
                }
            }
            return result;
        }
    }

    private sealed class InvestigationEfObserver : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly List<IDisposable> subscriptions = [];
        public List<object> Events { get; } = [];
        public InvestigationEfObserver(bool enabled)
        {
            if (enabled)
            {
                subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));
            }
        }
        public void OnNext(DiagnosticListener value)
        {
            if (value.Name == "Microsoft.EntityFrameworkCore")
            {
                subscriptions.Add(value.Subscribe(this, name => name.EndsWith("QueryCompilationStarting", StringComparison.Ordinal) ||
                    name.EndsWith("QueryExecutionPlanned", StringComparison.Ordinal) || name.EndsWith("ContextInitialized", StringComparison.Ordinal)));
            }
        }
        public void OnNext(KeyValuePair<string, object?> value) => Events.Add(new { name = value.Key, utc = DateTimeOffset.UtcNow, ticks = Stopwatch.GetTimestamp(), frequency = Stopwatch.Frequency });
        public void OnError(Exception error) { }
        public void OnCompleted() { }
        public void Dispose()
        {
            foreach (var subscription in subscriptions)
            {
                subscription.Dispose();
            }
        }
    }

    private sealed record InvestigationTrace(string Name, string FilePrefix);
    private static async Task<InvestigationTrace> StartInvestigationTraceAsync(string directory)
    {
        await using var connection = new SqlConnection(masterConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DB_ID(@database);";
        command.Parameters.AddWithValue("@database", databaseName);
        var databaseId = Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        Assert.IsGreaterThan(4, databaseId);
        command.Parameters.Clear();
        var trace = new InvestigationTrace("asap485_" + Guid.NewGuid().ToString("N"), "/var/opt/mssql/log/asap485_" + Guid.NewGuid().ToString("N"));
        var events = new[] { "rpc_completed", "sql_statement_completed", "query_post_compilation_showplan", "query_post_execution_showplan" };
        command.CommandText = "SELECT (SELECT object_name,name,type_name,description FROM sys.dm_xe_object_columns WHERE object_name IN ('rpc_completed','sql_statement_completed','query_post_compilation_showplan','query_post_execution_showplan') FOR JSON PATH);";
        await File.WriteAllTextAsync(Path.Combine(directory, "trace-field-metadata.json"), (string)(await command.ExecuteScalarAsync())!);
        command.CommandText = $"CREATE EVENT SESSION [{trace.Name}] ON SERVER " + string.Join(",", events.Select(name =>
            $"ADD EVENT sqlserver.{name}(ACTION(sqlserver.database_id,sqlserver.session_id) WHERE ([sqlserver].[database_id]=({databaseId})))")) +
            $" ADD TARGET package0.event_file(SET filename=N'{trace.FilePrefix}.xel',max_file_size=(100),max_rollover_files=(2)) WITH (MAX_MEMORY=65536 KB,EVENT_RETENTION_MODE=ALLOW_SINGLE_EVENT_LOSS,MAX_DISPATCH_LATENCY=1 SECONDS,MAX_EVENT_SIZE=16384 KB,TRACK_CAUSALITY=ON); ALTER EVENT SESSION [{trace.Name}] ON SERVER STATE=START;";
        await command.ExecuteNonQueryAsync();
        await SaveInvestigationJsonAsync(directory, "trace.json", new { trace.Name, databaseId, events, mode = "actual execution, heavyweight showplan; separate from baseline timings; no query replay" });
        return trace;
    }

    private static async Task StopInvestigationTraceAsync(InvestigationTrace trace, string directory)
    {
        await using var connection = new SqlConnection(masterConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        try
        {
            command.CommandText = $"SELECT (SELECT dropped_event_count,dropped_buffer_count,largest_event_dropped_size FROM sys.dm_xe_sessions WHERE name=N'{trace.Name}' FOR JSON PATH);";
            await File.WriteAllTextAsync(Path.Combine(directory, "trace-loss.json"), (string)(await command.ExecuteScalarAsync())!);
            command.CommandText = $"ALTER EVENT SESSION [{trace.Name}] ON SERVER STATE=STOP;";
            await command.ExecuteNonQueryAsync();
            command.CommandText = "SELECT CONVERT(nvarchar(max),event_data) FROM sys.fn_xe_file_target_read_file(@path,NULL,NULL,NULL) ORDER BY file_name,file_offset;";
            command.Parameters.AddWithValue("@path", trace.FilePrefix + "*.xel");
            await using var reader = await command.ExecuteReaderAsync();
            var index = 0;
            while (await reader.ReadAsync())
            {
                await File.WriteAllTextAsync(Path.Combine(directory, $"xe-{++index:0000}.xml"), reader.GetString(0));
            }
            Assert.IsGreaterThan(0, index, "Actual first-execution XE evidence is required.");
        }
        finally
        {
            await using var cleanup = connection.CreateCommand();
            cleanup.CommandText = $"DROP EVENT SESSION [{trace.Name}] ON SERVER;";
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private static async Task SaveInvestigationSqlStateAsync(string directory, string label)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT CONVERT(nvarchar(200),SERVERPROPERTY('ProductVersion')) productVersion,
                CONVERT(nvarchar(200),SERVERPROPERTY('Edition')) edition,@@VERSION version,
                (SELECT cpu_count,hyperthread_ratio,physical_memory_kb,sqlserver_start_time FROM sys.dm_os_sys_info FOR JSON PATH) host,
                (SELECT compatibility_level,is_read_committed_snapshot_on FROM sys.databases WHERE database_id=DB_ID() FOR JSON PATH) databaseOptions,
                (SELECT name,CONVERT(bigint,value_in_use) value FROM sys.configurations WHERE name IN ('max degree of parallelism','cost threshold for parallelism','max server memory (MB)') FOR JSON PATH) configuration,
                (SELECT wait_type,waiting_tasks_count,wait_time_ms,signal_wait_time_ms FROM sys.dm_os_wait_stats WHERE waiting_tasks_count>0 FOR JSON PATH) waits,
                (SELECT COUNT(*) activeRequests FROM sys.dm_exec_requests WHERE session_id<>@@SPID FOR JSON PATH) workload
                FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
            """;
        await File.WriteAllTextAsync(Path.Combine(directory, "sql-state-" + label + ".json"), (string)(await command.ExecuteScalarAsync())!);
        command.CommandText = """
            SELECT COALESCE((SELECT qs.creation_time,qs.last_execution_time,qs.execution_count,qs.total_worker_time,qs.total_elapsed_time,
                qs.total_logical_reads,qs.total_physical_reads,qs.total_logical_writes,qs.total_rows,st.text,
                CONVERT(nvarchar(max),qp.query_plan) query_plan
            FROM sys.dm_exec_query_stats qs CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
            OUTER APPLY sys.dm_exec_query_plan(qs.plan_handle) qp
            WHERE EXISTS (SELECT 1 FROM sys.dm_exec_plan_attributes(qs.plan_handle) a WHERE a.attribute=N'dbid' AND CONVERT(int,a.value)=DB_ID())
                AND (st.text LIKE '%[[]asap].[[]TitleRequest]%' OR st.text LIKE '%[[]asap].[[]AdditionalCopyRequest]%')
                AND st.text NOT LIKE '%sys.dm_exec_query_stats%'
            FOR JSON PATH),N'[]');
            """;
        await File.WriteAllTextAsync(Path.Combine(directory, "cached-search-plans-" + label + ".json"), (string)(await command.ExecuteScalarAsync())!);
    }
}
