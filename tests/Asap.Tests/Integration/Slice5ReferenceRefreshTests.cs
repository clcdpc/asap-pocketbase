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
            Assert.IsFalse((await ReadPatronCodeRowsAsync(1)).Values.Contains("provider-extra"));
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

    private sealed class ExtraReferenceProvider : IPolarisReferenceProvider
    {
        public Task<PolarisConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PolarisConnectionTestResult(true, 3));

        public Task<IReadOnlyList<PolarisOrganizationSnapshot>> GetOrganizationsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PolarisOrganizationSnapshot>>(
            [
                new(1, "System", null, 0, null),
                new(2, "Test Library", "Test", 2, 1),
                new(99, "Discovered Library", "DL", 99, 1)
            ]);

        public Task<IReadOnlyList<PolarisPatronCodeSnapshot>> GetPatronCodesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PolarisPatronCodeSnapshot>>(
            [new("1", "Adult"), new("provider-extra", "Provider-only reference")]);
    }
}
