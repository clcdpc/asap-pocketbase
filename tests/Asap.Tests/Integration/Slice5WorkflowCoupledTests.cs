using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task HourlyWorkflowGivesRecoveryAndNewPlacementIndependentPageBudgets()
    {
        var configuration = TestConfigurationFactory.Create(allowedDomains: ["example.org"]);
        configuration.ConnectionStrings.AsapDatabase = databaseConnectionString;
        configuration.ConnectionStrings.HangfireDatabase = databaseConnectionString;
        configuration.Application.DataProtectionKeysPath = Path.Combine(temporaryDirectory, "keys");
        configuration.Application.LogPath = Path.Combine(temporaryDirectory, "logs");
        configuration.Application.DataProtectionKeyEncryptionCertificateThumbprint = certificateThumbprint;
        configuration.Hangfire.ProcessingLimits.Default!.PageSize = 1;
        configuration.Hangfire.ProcessingLimits.Default.MaxPerRun = 2;
        var provider = new QueueFairnessProvider();
        await using var lowCapFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ExternalConfiguration>();
                services.AddSingleton(configuration);
                services.RemoveAll<Asap.Web.Features.Patron.IPatronProvider>();
                services.AddSingleton<Asap.Web.Features.Patron.IPatronProvider>(provider);
                services.RemoveAll<Asap.Web.Features.Staff.IStaffPolarisProvider>();
                services.AddSingleton<Asap.Web.Features.Staff.IStaffPolarisProvider>(provider);
            }));
        var contextFactory = lowCapFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var seed = await SeedCoupledRowsAsync(contextFactory, scope: 2, recoveryCount: 3, placementCount: 3);
        await IsolateOtherWorkflowQueuesAsync(contextFactory, QueueNames.HoldRecovery);
        await IsolateOtherWorkflowQueuesAsync(contextFactory, QueueNames.HoldPlacement);
        try
        {
            var result = await lowCapFactory.Services.GetRequiredService<WorkflowProcessingService>()
                .ProcessWorkflowAsync(2, CancellationToken.None);
            Assert.AreEqual("completed", result.Code);
            Assert.AreEqual(4, provider.CreateCount,
                "Recovery and new placement each get two provider mutation opportunities in one invocation.");

            await using var verify = await contextFactory.CreateDbContextAsync();
            var recovery = await verify.QueueProgress.AsNoTracking().SingleAsync(item =>
                item.QueueName == QueueNames.HoldRecovery && item.ScopeOrganizationId == 2);
            var placement = await verify.QueueProgress.AsNoTracking().SingleAsync(item =>
                item.QueueName == QueueNames.HoldPlacement && item.ScopeOrganizationId == 2);
            Assert.AreEqual(seed.OperationIds[1], recovery.LastItemId);
            Assert.AreEqual(seed.PlacementRequestIds[1], placement.LastItemId);
            Assert.IsNotNull(recovery.CycleMaxId);
            Assert.IsNotNull(placement.CycleMaxId);
        }
        finally
        {
            await DeleteCoupledRowsAsync(contextFactory, seed, 2);
        }
    }

    [TestMethod]
    public async Task GlobalWorkflowFinishesInactiveAcquiredRecoveryButBlocksNewPlacement()
    {
        var configuration = TestConfigurationFactory.Create(allowedDomains: ["example.org"]);
        configuration.ConnectionStrings.AsapDatabase = databaseConnectionString;
        configuration.ConnectionStrings.HangfireDatabase = databaseConnectionString;
        configuration.Application.DataProtectionKeysPath = Path.Combine(temporaryDirectory, "keys");
        configuration.Application.LogPath = Path.Combine(temporaryDirectory, "logs");
        configuration.Application.DataProtectionKeyEncryptionCertificateThumbprint = certificateThumbprint;
        configuration.Hangfire.ProcessingLimits.Default!.PageSize = 1;
        configuration.Hangfire.ProcessingLimits.Default.MaxPerRun = 2;
        var provider = new QueueFairnessProvider();
        await using var lowCapFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ExternalConfiguration>();
                services.AddSingleton(configuration);
                services.RemoveAll<Asap.Web.Features.Patron.IPatronProvider>();
                services.AddSingleton<Asap.Web.Features.Patron.IPatronProvider>(provider);
                services.RemoveAll<Asap.Web.Features.Staff.IStaffPolarisProvider>();
                services.AddSingleton<Asap.Web.Features.Staff.IStaffPolarisProvider>(provider);
            }));
        var contextFactory = lowCapFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var seed = await SeedCoupledRowsAsync(contextFactory, scope: 1, recoveryCount: 1, placementCount: 1);
        await IsolateOtherWorkflowQueuesAsync(contextFactory, QueueNames.HoldRecovery, scope: 1);
        await IsolateOtherWorkflowQueuesAsync(contextFactory, QueueNames.HoldPlacement, scope: 1);
        var wasActive = await SetOrganizationActiveAsync(contextFactory, 2, false);
        try
        {
            var result = await lowCapFactory.Services.GetRequiredService<WorkflowProcessingService>()
                .ProcessWorkflowAsync(null, CancellationToken.None);
            Assert.AreEqual("completed", result.Code);
            Assert.AreEqual(1, provider.CreateCount,
                "Inactive recovery may finish its acquired operation; inactive new placement must not call Polaris.");

            await using var verify = await contextFactory.CreateDbContextAsync();
            Assert.IsTrue(await verify.HoldPlacementOperations.AsNoTracking()
                .Where(item => seed.OperationIds.Contains(item.Id)).AllAsync(item => item.CompletedUtc.HasValue));
            Assert.AreEqual("pending_hold", await verify.TitleRequests.AsNoTracking()
                .Where(item => seed.PlacementRequestIds.Contains(item.Id)).Select(item => item.Status).SingleAsync());
        }
        finally
        {
            await SetOrganizationActiveAsync(contextFactory, 2, wasActive);
            await DeleteCoupledRowsAsync(contextFactory, seed, 1);
        }
    }

    private static async Task<CoupledSeed> SeedCoupledRowsAsync(
        IDbContextFactory<AsapDbContext> contextFactory,
        int scope,
        int recoveryCount,
        int placementCount)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var format = await context.MaterialFormats.SingleAsync(item => item.Code == "book");
        var baseUtc = DateTime.UtcNow.AddMinutes(-10);
        var recoveryRequests = Enumerable.Range(0, recoveryCount).Select(index => new TitleRequest
        {
            LibraryOrganizationId = 2,
            Barcode = $"s5-cr-{Guid.NewGuid():N}",
            Title = $"Slice 5 coupled recovery {index}",
            MaterialFormatId = format.Id,
            Status = "suggestion",
            BibId = (93000 + index).ToString(),
            IsbnCheckStatus = "found",
            CreatedUtc = baseUtc.AddTicks(index),
            UpdatedUtc = baseUtc.AddTicks(index)
        }).ToList();
        var placementRequests = Enumerable.Range(0, placementCount).Select(index => new TitleRequest
        {
            LibraryOrganizationId = 2,
            Barcode = $"s5-cp-{Guid.NewGuid():N}",
            Title = $"Slice 5 coupled placement {index}",
            MaterialFormatId = format.Id,
            Status = "pending_hold",
            BibId = (94000 + index).ToString(),
            AutoHold = true,
            IsbnCheckStatus = "found",
            CreatedUtc = baseUtc.AddTicks(100 + index),
            UpdatedUtc = baseUtc.AddTicks(100 + index)
        }).ToList();
        context.TitleRequests.AddRange(recoveryRequests);
        context.TitleRequests.AddRange(placementRequests);
        await context.SaveChangesAsync();
        var operations = recoveryRequests.Select((request, index) => new HoldPlacementOperation
        {
            TitleRequestId = request.Id,
            PatronBarcodeSnapshot = request.Barcode,
            BibIdSnapshot = request.BibId!,
            PickupBranchIdSnapshot = 101,
            AttemptNumber = 1,
            State = "in_progress",
            Phase = "acquired",
            ExecutionEpoch = 1,
            RequestStartedUtc = baseUtc.AddTicks(index),
            DetailJson = "{}"
        }).ToList();
        context.HoldPlacementOperations.AddRange(operations);
        await context.SaveChangesAsync();
        foreach (var queueName in new[] { QueueNames.HoldRecovery, QueueNames.HoldPlacement })
        {
            var progress = await context.QueueProgress.SingleOrDefaultAsync(item =>
                item.QueueName == queueName && item.ScopeOrganizationId == scope);
            progress ??= new QueueProgress { QueueName = queueName, ScopeOrganizationId = scope };
            if (progress.RowVersion.Length == 0) context.QueueProgress.Add(progress);
            progress.CycleMaxId = null;
            progress.LastCreatedUtc = null;
            progress.LastItemId = null;
            progress.LastOutcomeItemId = null;
            progress.LastOutcomeCode = null;
            progress.LastOutcomeUtc = null;
            progress.UpdatedUtc = DateTime.UtcNow;
        }
        await context.SaveChangesAsync();
        return new CoupledSeed(
            operations.Select(item => item.Id).ToArray(),
            placementRequests.Select(item => item.Id).ToArray(),
            recoveryRequests.Concat(placementRequests).Select(item => item.Id).ToArray());
    }

    private static async Task<bool> SetOrganizationActiveAsync(
        IDbContextFactory<AsapDbContext> contextFactory,
        int organizationId,
        bool? active = null)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var organization = await context.Organizations.SingleAsync(item => item.Id == organizationId);
        var previous = organization.IsActive;
        if (active.HasValue)
        {
            organization.IsActive = active.Value;
            await context.SaveChangesAsync();
        }
        return previous;
    }

    private static async Task DeleteCoupledRowsAsync(
        IDbContextFactory<AsapDbContext> contextFactory,
        CoupledSeed seed,
        int scope)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        context.TitleRequestEvents.RemoveRange(context.TitleRequestEvents.Where(item => seed.RequestIds.Contains(item.TitleRequestId)));
        context.TitleRequestWorkflowTags.RemoveRange(context.TitleRequestWorkflowTags.Where(item => seed.RequestIds.Contains(item.TitleRequestId)));
        context.HoldPlacementOperations.RemoveRange(context.HoldPlacementOperations.Where(item => seed.RequestIds.Contains(item.TitleRequestId)));
        context.TitleRequests.RemoveRange(context.TitleRequests.Where(item => seed.RequestIds.Contains(item.Id)));
        await context.SaveChangesAsync();
        foreach (var queueName in new[] { QueueNames.HoldRecovery, QueueNames.HoldPlacement })
        {
            var progress = await context.QueueProgress.SingleOrDefaultAsync(item =>
                item.QueueName == queueName && item.ScopeOrganizationId == scope);
            if (progress is null) continue;
            progress.CycleMaxId = null;
            progress.LastCreatedUtc = null;
            progress.LastItemId = null;
            progress.LastOutcomeItemId = null;
            progress.LastOutcomeCode = null;
            progress.LastOutcomeUtc = null;
            progress.UpdatedUtc = DateTime.UtcNow;
        }
        await context.SaveChangesAsync();
    }

    private sealed record CoupledSeed(
        IReadOnlyList<long> OperationIds,
        IReadOnlyList<long> PlacementRequestIds,
        IReadOnlyList<long> RequestIds);
}
