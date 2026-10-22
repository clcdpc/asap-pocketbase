using Asap.Web.Features.Staff;
using Asap.Web.Features.Patron;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task OrganizationRefreshPreservesConfiguredAllowListsAndCreatesInactiveLibraries()
    {
        var before = await ReadPatronCodeRowsAsync(1);
        var beforeLibrary = await ReadPatronCodeRowsAsync(2);
        var referenceProvider = new ExtraReferenceProvider();
        await using var refreshFactory = CreateApplicationFactory(configurationPath, referenceProvider);
        var service = refreshFactory.Services.GetRequiredService<WorkflowProcessingService>();

        try
        {
            var result = await service.RefreshOrganizationsAsync(CancellationToken.None);
            Assert.AreEqual("completed", result.Code);

            await using var context = await refreshFactory.Services
                .GetRequiredService<IDbContextFactory<AsapDbContext>>()
                .CreateDbContextAsync();
            var discovered = await context.Organizations.AsNoTracking().SingleAsync(item => item.Id == 99);
            Assert.IsFalse(discovered.IsActive);
            Assert.AreEqual("Discovered Library", discovered.DisplayName);
            Assert.AreEqual("DL", discovered.Abbreviation);
            Assert.AreEqual(before.SetCount, (await ReadPatronCodeRowsAsync(1)).SetCount);
            CollectionAssert.AreEqual(before.Values, (await ReadPatronCodeRowsAsync(1)).Values);
            Assert.AreEqual(beforeLibrary.SetCount, (await ReadPatronCodeRowsAsync(2)).SetCount);
            CollectionAssert.AreEqual(beforeLibrary.Values, (await ReadPatronCodeRowsAsync(2)).Values);
            Assert.IsFalse((await ReadPatronCodeRowsAsync(1)).Values.Contains(22));
        }
        finally
        {
            await ExecuteNonQueryAsync("DELETE FROM [asap].[Organization] WHERE [Id] = 99;");
        }
    }

    [TestMethod]
    public async Task OrganizationRefreshProviderFailureLeavesOrganizationsUntouched()
    {
        await using var refreshFactory = CreateApplicationFactory(
            configurationPath,
            new FailingPatronCodeReferenceProvider());
        var service = refreshFactory.Services.GetRequiredService<WorkflowProcessingService>();
        await using var context = await refreshFactory.Services
            .GetRequiredService<IDbContextFactory<AsapDbContext>>()
            .CreateDbContextAsync();
        var before = await context.Organizations.CountAsync(item => item.Id == 99);

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(
            () => service.RefreshOrganizationsAsync(CancellationToken.None));
        var after = await context.Organizations.CountAsync(item => item.Id == 99);
        Assert.AreEqual(before, after);
    }

    [TestMethod]
    public async Task ScheduledOrganizationRefreshRejectsConflictingDuplicateIdsBeforeAnyWrites()
    {
        const int organizationId = 88793;
        var barcode = $"scheduled-refresh-{Guid.NewGuid():N}";
        var auditBefore = await ReadAuditHighWatermarkAsync();
        var outboxBefore = await ReadCountAsync("SELECT COUNT(*) FROM [asap].[EmailOutbox];");
        byte[] originalVersion;
        await ExecuteNonQueryAsync("""
            INSERT INTO [asap].[Organization]
                ([Id], [DisplayName], [Abbreviation], [OrganizationCodeId], [ParentOrganizationId], [IsActive], [LastSyncedUtc])
            VALUES (88793, N'Preserved Library', N'KEEP', 2, 1, 1, '2025-01-01T00:00:00');
            INSERT INTO [asap].[PatronSettings] ([OrganizationId], [LoginNote], [UpdatedUtc])
            VALUES (88793, N'Preserve scheduled refresh setting', SYSUTCDATETIME());
            INSERT INTO [asap].[PatronSession]
                ([TokenHash], [Barcode], [EffectiveOrganizationId], [CreatedUtc], [ExpiresUtc])
            VALUES (HASHBYTES('SHA2_256', CONVERT(varbinary(100), @barcode)), @barcode, 88793,
                SYSUTCDATETIME(), DATEADD(hour, 1, SYSUTCDATETIME()));
            """, ("@barcode", barcode));
        await using (var connection = new Microsoft.Data.SqlClient.SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new Microsoft.Data.SqlClient.SqlCommand(
                "SELECT [RowVersion] FROM [asap].[Organization] WHERE [Id] = 88793;", connection);
            originalVersion = (byte[])(await command.ExecuteScalarAsync())!;
        }

        try
        {
            var library = new PolarisOrganizationSnapshot(organizationId, "Conflicting Library", "BAD", 2, 1);
            var branch = new PolarisOrganizationSnapshot(organizationId, "Conflicting Branch", "BAD", 3, 1);
            foreach (var snapshots in new IReadOnlyList<PolarisOrganizationSnapshot>[]
                     {
                         [branch, library],
                         [library, branch]
                     })
            {
                var provider = new ConflictingOrganizationReferenceProvider(snapshots);
                await using var refreshFactory = CreateApplicationFactory(configurationPath, provider);
                var service = refreshFactory.Services.GetRequiredService<WorkflowProcessingService>();
                var result = await service.RefreshOrganizationsAsync(CancellationToken.None);
                Assert.AreEqual("organization_snapshot_invalid", result.Code);
                Assert.AreEqual(0, provider.PatronCodeLookupCount,
                    "The invalid organization snapshot must be rejected before loading patron-code reference data.");

                await using var connection = new Microsoft.Data.SqlClient.SqlConnection(databaseConnectionString);
                await connection.OpenAsync();
                await using var verify = new Microsoft.Data.SqlClient.SqlCommand("""
                    SELECT [DisplayName], [Abbreviation], [OrganizationCodeId], [ParentOrganizationId], [IsActive],
                           [LastSyncedUtc], [RowVersion],
                           (SELECT [LoginNote] FROM [asap].[PatronSettings] WHERE [OrganizationId] = 88793),
                           (SELECT COUNT(*) FROM [asap].[PatronSession]
                            WHERE [Barcode] = @barcode AND [RevokedUtc] IS NULL),
                           (SELECT COUNT(*) FROM [asap].[PatronSession]
                            WHERE [Barcode] = @barcode AND [RevokedUtc] IS NOT NULL)
                    FROM [asap].[Organization] WHERE [Id] = 88793;
                    """, connection);
                verify.Parameters.AddWithValue("@barcode", barcode);
                await using var row = await verify.ExecuteReaderAsync();
                Assert.IsTrue(await row.ReadAsync());
                Assert.AreEqual("Preserved Library", row.GetString(0));
                Assert.AreEqual("KEEP", row.GetString(1));
                Assert.AreEqual(2, row.GetInt32(2));
                Assert.AreEqual(1, row.GetInt32(3));
                Assert.IsTrue(row.GetBoolean(4));
                Assert.AreEqual(new DateTime(2025, 1, 1), row.GetDateTime(5));
                CollectionAssert.AreEqual(originalVersion, (byte[])row[6]);
                Assert.AreEqual("Preserve scheduled refresh setting", row.GetString(7));
                Assert.AreEqual(1, row.GetInt32(8));
                Assert.AreEqual(0, row.GetInt32(9));
                await row.CloseAsync();
                Assert.AreEqual(auditBefore, await ReadAuditHighWatermarkAsync());
                Assert.AreEqual(outboxBefore, await ReadCountAsync("SELECT COUNT(*) FROM [asap].[EmailOutbox];"));
            }
        }
        finally
        {
            await ExecuteNonQueryAsync("""
                DELETE FROM [asap].[PatronSession]
                WHERE [Barcode] = @barcode;
                DELETE FROM [asap].[PatronSettings] WHERE [OrganizationId] = 88793;
                DELETE FROM [asap].[Organization] WHERE [Id] = 88793;
                """, ("@barcode", barcode));
        }
    }

    private sealed class ExtraReferenceProvider : IPolarisReferenceProvider
    {
        public Task<PolarisConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PolarisConnectionTestResult(true, 3));

        public Task<IReadOnlyList<PolarisOrganizationSnapshot>> GetOrganizationsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PolarisOrganizationSnapshot>>(
            [
                new(1, "System", null, 1, null),
                new(2, "Test Library", "Test", 2, 1),
                new(99, "Discovered Library", "DL", 2, 1)
            ]);

        public Task<IReadOnlyList<PolarisPatronCodeSnapshot>> GetPatronCodesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PolarisPatronCodeSnapshot>>(
            [new(1, "Adult"), new(22, "Provider-only reference")]);
    }

    private sealed class ConflictingOrganizationReferenceProvider(
        IReadOnlyList<PolarisOrganizationSnapshot> organizations) : IPolarisReferenceProvider
    {
        public int PatronCodeLookupCount { get; private set; }

        public Task<PolarisConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PolarisConnectionTestResult(true, organizations.Count));

        public Task<IReadOnlyList<PolarisOrganizationSnapshot>> GetOrganizationsAsync(
            CancellationToken cancellationToken) => Task.FromResult(organizations);

        public Task<IReadOnlyList<PolarisPatronCodeSnapshot>> GetPatronCodesAsync(
            CancellationToken cancellationToken)
        {
            PatronCodeLookupCount++;
            return Task.FromResult<IReadOnlyList<PolarisPatronCodeSnapshot>>([new(1, "Adult")]);
        }
    }
}
