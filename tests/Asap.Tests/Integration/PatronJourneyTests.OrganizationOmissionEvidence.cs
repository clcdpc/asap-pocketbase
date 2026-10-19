using Asap.Web.Features.Administration;
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
    public async Task OrganizationCatalogOmissionPreservesPriorAuthorityWithoutInventingFreshObservation(bool scheduled)
    {
        const int organizationId = 36590;
        var provider = new OmissionReferenceProvider(organizationId);
        await using var scoped = CreateApplicationFactory(configurationPath, provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using (var setup = await contexts.CreateDbContextAsync())
        {
            setup.Organizations.Add(new Organization
            {
                Id = organizationId, DisplayName = "Omission fixture", OrganizationCodeId = 2,
                ParentOrganizationId = 1, IsActive = true
            });
            await setup.SaveChangesAsync();
        }
        var session = await IssueTestPatronSessionAsync("catalog-omission", organizationId, 365900);
        try
        {
            var actor = await ReadConfiguredSuperAdminAsync();
            async Task RefreshAsync()
            {
                if (scheduled)
                {
                    Assert.AreEqual("completed", (await scoped.Services
                        .GetRequiredService<WorkflowProcessingService>().RefreshOrganizationsAsync()).Code);
                }
                else
                {
                    Assert.AreEqual("synced", (await scoped.Services
                        .GetRequiredService<AdministrationService>().SyncOrganizationsAsync(actor, CancellationToken.None)).Code);
                }
            }
            await RefreshAsync();
            Organization first;
            await using (var observed = await contexts.CreateDbContextAsync())
            {
                first = await observed.Organizations.AsNoTracking().SingleAsync(item => item.Id == organizationId);
                Assert.IsNotNull(first.LastSyncedUtc);
            }
            provider.OmitLibrary = true;
            timeProvider!.SetUtcNow(timeProvider.GetUtcNow().AddMinutes(1));
            await RefreshAsync();
            await using var verify = await contexts.CreateDbContextAsync();
            var omitted = await verify.Organizations.AsNoTracking().SingleAsync(item => item.Id == organizationId);
            Assert.IsTrue(omitted.IsActive);
            Assert.AreEqual(2, omitted.OrganizationCodeId);
            Assert.AreEqual(first.LastSyncedUtc, omitted.LastSyncedUtc,
                "Omission must not be mislabeled as a new authoritative observation.");
            CollectionAssert.AreEqual(first.RowVersion, omitted.RowVersion);
            Assert.AreEqual(1, await CountCurrentSessionAsync(session.Id));
            Assert.AreEqual(0, await verify.TitleRequests.CountAsync(item => item.LibraryOrganizationId == organizationId));
        }
        finally
        {
            await DeleteTestPatronSessionAsync(session.Id);
            await using var cleanup = await contexts.CreateDbContextAsync();
            await cleanup.Organizations.Where(item => item.Id == organizationId).ExecuteDeleteAsync();
        }
    }

    private sealed class OmissionReferenceProvider(int libraryId) : IPolarisReferenceProvider
    {
        public bool OmitLibrary { get; set; }

        public Task<PolarisConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PolarisConnectionTestResult(true, OmitLibrary ? 1 : 2));
        }

        public Task<IReadOnlyList<PolarisOrganizationSnapshot>> GetOrganizationsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<PolarisOrganizationSnapshot> rows = OmitLibrary
                ? [new(1, "System", null, 1, null)]
                : [new(1, "System", null, 1, null), new(libraryId, "Omission fixture", null, 2, 1)];
            return Task.FromResult(rows);
        }

        public Task<IReadOnlyList<PolarisPatronCodeSnapshot>> GetPatronCodesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<PolarisPatronCodeSnapshot>>([new(1, "Adult")]);
        }
    }
}
