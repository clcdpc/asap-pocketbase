using Asap.Web.Features.Staff;
using Asap.Web.Features.Patron;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task ForcedWeeklyAuditRevalidatesActorAtItsMutationBoundary()
    {
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var created = await CreateCorrectiveStaffAsync(superAdmin, StaffRole.SuperAdmin, LibraryScope.SystemOrganizationId);
        var actor = await ReadCorrectiveStaffAsync(created);
        var manualId = Guid.NewGuid();
        var auditKey = manualId.ToString("N");
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        try
        {
            await ExecuteNonQueryAsync("UPDATE [asap].[StaffUser] SET [IsActive]=0 WHERE [Id]=@id;", ("@id", actor.Id));
            var result = await factory.Services.GetRequiredService<WorkflowProcessingService>()
                .SendWeeklyStaffSummaryAsync(manualId, 2, CancellationToken.None,
                    new StaffIdentityEvidence(actor.Id, actor.AuthenticationEmail, actor.EntraTenantId));
            Assert.AreEqual("staff_scope_forbidden", result.Code);
            await using var verify = await contexts.CreateDbContextAsync();
            Assert.IsFalse(await verify.AdministrativeAudits.AnyAsync(item =>
                item.Action == "weekly_summary_force_queued" && item.TargetId == auditKey));
            Assert.IsFalse(await verify.EmailOutbox.AnyAsync(item =>
                item.BusinessKey != null && item.BusinessKey.StartsWith($"weekly-summary-force:{auditKey}:")));
        }
        finally
        {
            await using var cleanup = await contexts.CreateDbContextAsync();
            await cleanup.AdministrativeAudits.Where(item =>
                item.Action == "weekly_summary_force_queued" && item.TargetId == auditKey).ExecuteDeleteAsync();
            await cleanup.EmailOutbox.Where(item =>
                item.BusinessKey != null && item.BusinessKey.StartsWith($"weekly-summary-force:{auditKey}:")).ExecuteDeleteAsync();
            await cleanup.StaffUsers.Where(item => item.Id == actor.Id).ExecuteDeleteAsync();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ManualFulfillmentMalformedEvidenceRevalidatesRevokedActor(bool wrongPatron)
    {
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var created = await CreateCorrectiveStaffAsync(superAdmin, StaffRole.SuperAdmin, LibraryScope.SystemOrganizationId);
        var actor = await ReadCorrectiveStaffAsync(created);
        var scope = await CreateFulfillmentLibraryAsync();
        const string barcode = "350-manual-fulfillment";
        var provider = new FulfillmentEvidenceProvider
        {
            Checkouts = [new PolarisCheckoutSnapshot(wrongPatron ? 9958 : 0, 9958,
                wrongPatron ? "different-patron" : barcode)]
        };
        provider.BlockCheckoutRead();
        await using var scoped = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton<IStaffPolarisProvider>(provider);
        }));
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var seeded = await SeedCompletedHoldIdentityAsync("350-manual-diagnostic", barcode, 9958,
            holdRequestId: 9958, organizationId: scope);
        try
        {
            await PrepareSingleItemCycleAsync(QueueNames.FulfillmentTracking, scope, seeded.RequestId);
            await using var before = await contexts.CreateDbContextAsync();
            var version = await before.HoldPlacementOperations.AsNoTracking()
                .Where(item => item.Id == seeded.OperationId).Select(item => item.RowVersion).SingleAsync();
            var execution = scoped.Services.GetRequiredService<BackgroundWorkflowJobs>().ProcessManualWorkflowAsync(
                new StaffJobEvidence(actor.Id, actor.AuthenticationEmail, actor.EntraTenantId), scope, CancellationToken.None);
            await provider.CheckoutReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await ExecuteNonQueryAsync("UPDATE [asap].[StaffUser] SET [IsActive]=0 WHERE [Id]=@id;", ("@id", actor.Id));
            provider.CompleteBlockedCheckout(provider.Checkouts);
            Assert.AreEqual("operational_failure", (await execution.WaitAsync(TimeSpan.FromSeconds(15))).Code);
            await using var verify = await contexts.CreateDbContextAsync();
            var operation = await verify.HoldPlacementOperations.AsNoTracking().SingleAsync(item => item.Id == seeded.OperationId);
            Assert.IsNull(operation.LastErrorCode, "A revoked actor cannot accept even diagnostic operation changes.");
            CollectionAssert.AreEqual(version, operation.RowVersion);
            Assert.AreEqual(RequestStatus.HoldPlaced, await verify.TitleRequests.Where(item => item.Id == seeded.RequestId)
                .Select(item => item.Status).SingleAsync());
            Assert.AreEqual("staff_scope_forbidden", await verify.QueueProgress
                .Where(item => item.QueueName == QueueNames.FulfillmentTracking && item.ScopeOrganizationId == scope)
                .Select(item => item.LastOutcomeCode).SingleAsync());
        }
        finally
        {
            provider.CompleteBlockedCheckout(provider.Checkouts);
            await DeleteRequestAsync(seeded.RequestId);
            await using var cleanup = await contexts.CreateDbContextAsync();
            await cleanup.StaffUsers.Where(item => item.Id == actor.Id).ExecuteDeleteAsync();
            await DeleteFulfillmentLibraryAsync(scope);
        }
    }
}
