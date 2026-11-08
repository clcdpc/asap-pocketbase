using System.Text.Json;
using System.Data.Common;
using System.Text.RegularExpressions;
using Asap.Web.Features.Administration;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SettingsReadsProjectVersionsAndReuseOperationScalarRows(bool systemScope)
    {
        var probe = new SettingsReadProbe();
        var contexts = new SettingsReadContextFactory(databaseConnectionString, probe);
        var service = ActivatorUtilities.CreateInstance<AdministrationService>(factory!.Services,
            contexts, new PatronConfigurationService(contexts));
        var actor = await ReadConfiguredSuperAdminAsync();
        var result = await service.GetSettingsAsync(actor,
            systemScope ? LibraryScope.System : LibraryScope.ForLibrary(2), CancellationToken.None);
        Assert.AreEqual("ok", result.Code);
        var versionReads = probe.Commands.Where(sql => sql.Contains("-- Settings version", StringComparison.Ordinal)).ToArray();
        Assert.AreEqual(17, versionReads.Length);
        foreach (var sql in versionReads)
        {
            StringAssert.Matches(sql, new Regex(@"SELECT \[\w+\]\.\[RowVersion\]\s+FROM"));
            Assert.IsFalse(sql.Contains("LogoData", StringComparison.Ordinal), "Version computation must not hydrate logos.");
        }
        foreach (var table in new[] { "SystemSettings", "WorkflowSettings", "PatronSettings", "EmailSettings" })
        {
            var reads = probe.Commands.Count(sql => sql.Contains($"FROM [asap].[{table}]", StringComparison.Ordinal) &&
                !sql.Contains("-- Settings version", StringComparison.Ordinal));
            Assert.AreEqual(systemScope || table == "SystemSettings" ? 1 : 2, reads,
                $"{table} should be read once per scope, then reused for effective configuration and override detection.\n" +
                string.Join("\n", probe.Commands.Where(sql => sql.Contains($"FROM [asap].[{table}]", StringComparison.Ordinal))));
        }
        TestContext.WriteLine($"Settings {(systemScope ? "system" : "library")} GET: {probe.Commands.Count} reader commands; {versionReads.Length} version-only single-column projections.");
    }

    [TestMethod]
    public async Task RetiredSettingsCollectionWriteAliasesFailBeforeAnyMutation()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var service = factory!.Services.GetRequiredService<AdministrationService>();
        var auditBefore = await ReadAuditHighWatermarkAsync();
        foreach (var alias in new[] { "externalSearchProviders", "materialFormats", "additionalFieldDefinitions", "patronFormatRules", "emailTemplates" })
        {
            foreach (var section in new[] { "root", "workflow", "patron", "email" })
            {
                var body = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["orgId"] = "2",
                    [section == "root" ? alias : section] = section == "root" ? Array.Empty<object>()
                        : new Dictionary<string, object> { [alias] = Array.Empty<object>() }
                });
                using var payload = JsonDocument.Parse(body);
                Assert.AreEqual("settings_payload_invalid", (await service.SaveSettingsAsync(actor,
                    AdministrationSettingsBinding.Bind(actor, payload.RootElement), CancellationToken.None)).Code, $"{section}.{alias}");
            }
        }
        Assert.AreEqual(auditBefore, await ReadAuditHighWatermarkAsync());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SettingsVersionIgnoresSyncTelemetryButRetainsConfigurationConflicts(bool systemScope)
    {
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var setup = await contexts.CreateDbContextAsync();
        var original = await setup.Organizations.AsNoTracking().SingleAsync(item => item.Id == 2);
        var originalWorkflowUpdatedUtc = await setup.WorkflowSettings.Where(item => item.OrganizationId == 1)
            .Select(item => item.UpdatedUtc).SingleAsync();
        var provider = new ConflictingOrganizationReferenceProvider([
            new(2, original.DisplayName, original.Abbreviation, original.OrganizationCodeId, original.ParentOrganizationId)
        ]);
        await using var refreshed = CreateApplicationFactory(configurationPath, provider);
        var settings = refreshed.Services.GetRequiredService<AdministrationService>();
        var actor = await ReadConfiguredSuperAdminAsync();
        var scope = systemScope ? LibraryScope.System : LibraryScope.ForLibrary(2);
        var scopeId = systemScope ? "system" : "2";
        var auditBefore = await ReadAuditHighWatermarkAsync();
        try
        {
            var version = await VersionAsync();
            timeProvider!.SetUtcNow(timeProvider.GetUtcNow().AddMinutes(5));
            Assert.AreEqual("completed", (await refreshed.Services.GetRequiredService<WorkflowProcessingService>()
                .RefreshOrganizationsAsync(CancellationToken.None)).Code);
            await using (var verify = await contexts.CreateDbContextAsync())
            {
                var synced = await verify.Organizations.AsNoTracking().SingleAsync(item => item.Id == 2);
                Assert.AreNotEqual(original.LastSyncedUtc, synced.LastSyncedUtc);
                Assert.IsFalse(original.RowVersion.SequenceEqual(synced.RowVersion));
            }
            Assert.AreEqual(version, await VersionAsync(), "Telemetry must not invalidate an existing draft.");
            Assert.AreEqual("saved", (await SaveAsync(version)).Code);

            foreach (var change in new[]
            {
                "[DisplayName] = [DisplayName] + N' changed'",
                "[Abbreviation] = N'VERSION'",
                "[ParentOrganizationId] = NULL",
                "[IsActive] = 0"
            })
            {
                version = await VersionAsync();
                await ExecuteNonQueryAsync($"UPDATE [asap].[Organization] SET {change} WHERE [Id] = 2;");
                Assert.AreEqual("stale_version", (await SaveAsync(version)).Code, change);
            }
            version = await VersionAsync();
            await ExecuteNonQueryAsync("UPDATE [asap].[WorkflowSettings] SET [UpdatedUtc] = DATEADD(second, 1, [UpdatedUtc]) WHERE [OrganizationId] = 1;");
            Assert.AreEqual("stale_version", (await SaveAsync(version)).Code, "Inherited configuration still contributes rowversion.");
            version = await VersionAsync();
            await ExecuteNonQueryAsync("UPDATE [asap].[Organization] SET [OrganizationCodeId] = 3 WHERE [Id] = 2;");
            Assert.AreEqual(systemScope ? "stale_version" : "organization_not_found", (await SaveAsync(version)).Code,
                "Reclassification must invalidate configuration or reject the removed library scope.");
        }
        finally
        {
            await ExecuteNonQueryAsync("""
                UPDATE [asap].[Organization]
                SET [DisplayName] = @name, [Abbreviation] = @abbreviation, [OrganizationCodeId] = @code,
                    [ParentOrganizationId] = @parent, [IsActive] = @active, [LastSyncedUtc] = @synced
                WHERE [Id] = 2;
                UPDATE [asap].[WorkflowSettings] SET [UpdatedUtc] = @workflowUpdatedUtc WHERE [OrganizationId] = 1;
                DELETE FROM [asap].[AdministrativeAudit] WHERE [Id] > @auditBefore;
                """, ("@name", original.DisplayName), ("@abbreviation", (object?)original.Abbreviation ?? DBNull.Value),
                ("@code", (object?)original.OrganizationCodeId ?? DBNull.Value), ("@parent", (object?)original.ParentOrganizationId ?? DBNull.Value),
                ("@active", original.IsActive), ("@synced", (object?)original.LastSyncedUtc ?? DBNull.Value),
                ("@workflowUpdatedUtc", originalWorkflowUpdatedUtc), ("@auditBefore", auditBefore));
        }

        async Task<string> VersionAsync()
        {
            var result = await settings.GetSettingsAsync(actor, scope, CancellationToken.None);
            Assert.AreEqual("ok", result.Code);
            return JsonSerializer.SerializeToElement(result.Data).GetProperty("version").GetString()!;
        }

        Task<AdministrationResult> SaveAsync(string version)
        {
            var payload = JsonSerializer.SerializeToElement(new { orgId = scopeId, version });
            return settings.SaveSettingsAsync(actor, AdministrationSettingsBinding.Bind(actor, payload), CancellationToken.None);
        }
    }

    private sealed class SettingsReadContextFactory(string connectionString, IInterceptor probe) : IDbContextFactory<AsapDbContext>
    {
        public AsapDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AsapDbContext>()
            .UseSqlServer(connectionString).AddInterceptors(probe).Options);
    }

    private sealed class SettingsReadProbe : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
