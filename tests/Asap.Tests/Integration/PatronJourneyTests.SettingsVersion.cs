using System.Text.Json;
using Asap.Web.Features.Administration;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SettingsVersionIgnoresSyncTelemetryButRetainsConfigurationConflicts(bool systemScope)
    {
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var setup = await contexts.CreateDbContextAsync();
        var original = await setup.Organizations.AsNoTracking().SingleAsync(item => item.Id == 2);
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
                DELETE FROM [asap].[AdministrativeAudit] WHERE [Id] > @auditBefore;
                """, ("@name", original.DisplayName), ("@abbreviation", (object?)original.Abbreviation ?? DBNull.Value),
                ("@code", (object?)original.OrganizationCodeId ?? DBNull.Value), ("@parent", (object?)original.ParentOrganizationId ?? DBNull.Value),
                ("@active", original.IsActive), ("@synced", (object?)original.LastSyncedUtc ?? DBNull.Value), ("@auditBefore", auditBefore));
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
}
