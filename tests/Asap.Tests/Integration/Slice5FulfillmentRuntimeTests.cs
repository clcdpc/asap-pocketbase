using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task FulfillmentClosesOnPositiveCheckoutWithoutTerminalHoldRead()
    {
        var scope = await CreateFulfillmentLibraryAsync();
        var barcode = $"2000000000{Random.Shared.Next(100000, 999999)}";
        var provider = new FulfillmentEvidenceProvider
        {
            Checkouts = [new PolarisCheckoutSnapshot(9951, 9951, barcode)]
        };
        await using var workflowFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IStaffPolarisProvider>(provider);
            }));

        var seeded = await SeedCompletedHoldIdentityAsync(
            "positive-checkout", barcode, 9951, holdRequestId: 9951, organizationId: scope);
        await PrepareSingleItemCycleAsync(QueueNames.FulfillmentTracking, scope, seeded.RequestId);
        try
        {
            var result = await workflowFactory.Services.GetRequiredService<WorkflowProcessingService>()
                .ProcessWorkflowAsync(scope, CancellationToken.None);
            Assert.AreEqual("completed", result.Code);
            Assert.AreEqual("closed", await ReadStringAsync(
                "SELECT [Status] FROM [asap].[TitleRequest] WHERE [Id] = @id;", "@id", seeded.RequestId));
            Assert.AreEqual("hold_completed", await ReadStringAsync(
                "SELECT [CloseReason] FROM [asap].[TitleRequest] WHERE [Id] = @id;", "@id", seeded.RequestId));
            Assert.AreEqual(0, provider.HoldReadCount);
            Assert.AreEqual(1, await CountForRequestAsync(
                "[asap].[TitleRequestEvent]", "[TitleRequestId]", seeded.RequestId));
        }
        finally
        {
            await DeleteRequestAsync(seeded.RequestId);
            await DeleteFulfillmentLibraryAsync(scope);
        }
    }

    [TestMethod]
    public async Task FulfillmentCheckoutReadFailurePersistsDiagnosticAndStopsPhase()
    {
        var scope = await CreateFulfillmentLibraryAsync();
        var barcode = $"2000000000{Random.Shared.Next(100000, 999999)}";
        var provider = new FulfillmentEvidenceProvider
        {
            CheckoutReadException = new PolarisOperationalException(
                "checkout_read_failed", "Synthetic checkout read failure.")
        };
        await using var workflowFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IStaffPolarisProvider>(provider);
            }));

        var seeded = await SeedCompletedHoldIdentityAsync(
            "checkout-failure", barcode, 9952, holdRequestId: 9952, organizationId: scope);
        await PrepareSingleItemCycleAsync(QueueNames.FulfillmentTracking, scope, seeded.RequestId);
        try
        {
            var result = await workflowFactory.Services.GetRequiredService<WorkflowProcessingService>()
                .ProcessWorkflowAsync(scope, CancellationToken.None);
            Assert.AreEqual("operational_failure", result.Code);
            Assert.AreEqual("hold_placed", await ReadStringAsync(
                "SELECT [Status] FROM [asap].[TitleRequest] WHERE [Id] = @id;", "@id", seeded.RequestId));
            Assert.AreEqual("hold_tracking_provider_error", await ReadStringAsync(
                "SELECT [LastErrorCode] FROM [asap].[HoldPlacementOperation] WHERE [Id] = @id;", "@id", seeded.OperationId));
            Assert.AreEqual(1, provider.CheckoutReadCount);
            Assert.AreEqual(0, await CountForRequestAsync(
                "[asap].[TitleRequestEvent]", "[TitleRequestId]", seeded.RequestId));
        }
        finally
        {
            await DeleteRequestAsync(seeded.RequestId);
            await DeleteFulfillmentLibraryAsync(scope);
        }
    }

    [TestMethod]
    public async Task FulfillmentDoesNotCloseAfterRequestIdentityChangesDuringCheckoutRead()
    {
        var scope = await CreateFulfillmentLibraryAsync();
        var barcode = $"2000000000{Random.Shared.Next(100000, 999999)}";
        var provider = new FulfillmentEvidenceProvider
        {
            Checkouts = [new PolarisCheckoutSnapshot(9956, 9956, barcode)]
        };
        provider.BlockCheckoutRead();
        await using var workflowFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IStaffPolarisProvider>(provider);
            }));

        var seeded = await SeedCompletedHoldIdentityAsync(
            "checkout-stale-identity", barcode, 9956, holdRequestId: 9956, organizationId: scope);
        await PrepareSingleItemCycleAsync(QueueNames.FulfillmentTracking, scope, seeded.RequestId);
        try
        {
            var execution = workflowFactory.Services.GetRequiredService<WorkflowProcessingService>()
                .ProcessWorkflowAsync(scope, CancellationToken.None);
            await provider.CheckoutReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await ExecuteNonQueryAsync(
                "UPDATE [asap].[TitleRequest] SET [BibId] = N'9957' WHERE [Id] = @id;",
                ("@id", seeded.RequestId));
            provider.CompleteBlockedCheckout(provider.Checkouts);

            var result = await execution.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual("completed", result.Code);
            Assert.AreEqual("hold_placed", await ReadStringAsync(
                "SELECT [Status] FROM [asap].[TitleRequest] WHERE [Id] = @id;", "@id", seeded.RequestId));
            await using var nativeCheck = await workflowFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>().CreateDbContextAsync();
            Assert.AreEqual(9957, await nativeCheck.TitleRequests.Where(item => item.Id == seeded.RequestId)
                .Select(item => item.BibId).SingleAsync());
            Assert.AreEqual(0, await CountForRequestAsync(
                "[asap].[TitleRequestEvent]", "[TitleRequestId]", seeded.RequestId));
            Assert.AreEqual(1, provider.CheckoutReadCount);
            Assert.AreEqual(0, provider.HoldReadCount);
        }
        finally
        {
            await DeleteRequestAsync(seeded.RequestId);
            await DeleteFulfillmentLibraryAsync(scope);
        }
    }

    [TestMethod]
    public async Task FulfillmentDoesNotInheritAnOldIdentityWhenLatestOperationHasNoHoldId()
    {
        var scope = await CreateFulfillmentLibraryAsync();
        var barcode = $"2000000000{Random.Shared.Next(100000, 999999)}";
        var provider = new FulfillmentEvidenceProvider
        {
            Holds = [new PolarisHoldSnapshot(7788, 9953, 3, "Expired", 101)]
        };
        await using var workflowFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IStaffPolarisProvider>(provider);
            }));

        var seeded = await SeedCompletedHoldIdentityAsync(
            "null-latest-identity", barcode, 9953, holdRequestId: null, organizationId: scope);
        await PrepareSingleItemCycleAsync(QueueNames.FulfillmentTracking, scope, seeded.RequestId);
        try
        {
            var result = await workflowFactory.Services.GetRequiredService<WorkflowProcessingService>()
                .ProcessWorkflowAsync(scope, CancellationToken.None);
            Assert.AreEqual("completed", result.Code);
            Assert.AreEqual("hold_placed", await ReadStringAsync(
                "SELECT [Status] FROM [asap].[TitleRequest] WHERE [Id] = @id;", "@id", seeded.RequestId));
            Assert.AreEqual("hold_identity_unavailable", await ReadStringAsync(
                "SELECT [LastErrorCode] FROM [asap].[HoldPlacementOperation] WHERE [Id] = @id;", "@id", seeded.OperationId));
        }
        finally
        {
            await DeleteRequestAsync(seeded.RequestId);
            await DeleteFulfillmentLibraryAsync(scope);
        }
    }

    [TestMethod]
    public async Task FulfillmentRejectsMalformedCheckoutEvidenceAndPersistsDiagnostic()
    {
        var scope = await CreateFulfillmentLibraryAsync();
        var barcode = $"2000000000{Random.Shared.Next(100000, 999999)}";
        var provider = new FulfillmentEvidenceProvider
        {
            Checkouts = [new PolarisCheckoutSnapshot(0, 0, barcode)]
        };
        await using var workflowFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IStaffPolarisProvider>(provider);
            }));

        var seeded = await SeedCompletedHoldIdentityAsync(
            "malformed-checkout", barcode, 9954, holdRequestId: 9954, organizationId: scope);
        await PrepareSingleItemCycleAsync(QueueNames.FulfillmentTracking, scope, seeded.RequestId);
        try
        {
            var result = await workflowFactory.Services.GetRequiredService<WorkflowProcessingService>()
                .ProcessWorkflowAsync(scope, CancellationToken.None);
            Assert.AreEqual("operational_failure", result.Code);
            Assert.AreEqual("hold_placed", await ReadStringAsync(
                "SELECT [Status] FROM [asap].[TitleRequest] WHERE [Id] = @id;", "@id", seeded.RequestId));
            Assert.AreEqual("hold_tracking_provider_error", await ReadStringAsync(
                "SELECT [LastErrorCode] FROM [asap].[HoldPlacementOperation] WHERE [Id] = @id;", "@id", seeded.OperationId));
        }
        finally
        {
            await DeleteRequestAsync(seeded.RequestId);
            await DeleteFulfillmentLibraryAsync(scope);
        }
    }

    [TestMethod]
    public async Task FulfillmentSqlFailureRollsBackPositiveClosureAndQueueCheckpoint()
    {
        var scope = await CreateFulfillmentLibraryAsync();
        var barcode = $"2000000000{Random.Shared.Next(100000, 999999)}";
        var provider = new FulfillmentEvidenceProvider
        {
            Checkouts = [new PolarisCheckoutSnapshot(9955, 9955, barcode)]
        };
        await using var workflowFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IStaffPolarisProvider>(provider);
            }));

        var seeded = await SeedCompletedHoldIdentityAsync(
            "fulfillment-sql-rollback", barcode, 9955, holdRequestId: 9955, organizationId: scope);
        await PrepareSingleItemCycleAsync(QueueNames.FulfillmentTracking, scope, seeded.RequestId);
        try
        {
            await ExecuteNonQueryAsync(
                "CREATE TRIGGER [asap].[TR_Slice5FulfillmentEventRollback] ON [asap].[TitleRequestEvent] AFTER INSERT AS THROW 51056, 'Slice 5 fulfillment rollback probe', 1;");
            try
            {
                var result = await workflowFactory.Services.GetRequiredService<WorkflowProcessingService>()
                    .ProcessWorkflowAsync(scope, CancellationToken.None);
                Assert.AreEqual("sql_failure", result.Code);
                Assert.AreEqual("hold_placed", await ReadStringAsync(
                    "SELECT [Status] FROM [asap].[TitleRequest] WHERE [Id] = @id;", "@id", seeded.RequestId));
                Assert.AreEqual(0, await CountForRequestAsync(
                    "[asap].[TitleRequestEvent]", "[TitleRequestId]", seeded.RequestId));
                await using var context = await workflowFactory.Services
                    .GetRequiredService<IDbContextFactory<AsapDbContext>>()
                    .CreateDbContextAsync();
                var progress = await context.QueueProgress.AsNoTracking().SingleAsync(item =>
                    item.QueueName == QueueNames.FulfillmentTracking && item.ScopeOrganizationId == scope);
                Assert.AreEqual(0, progress.LastItemId);
            }
            finally
            {
                await ExecuteNonQueryAsync("DROP TRIGGER [asap].[TR_Slice5FulfillmentEventRollback];");
            }
        }
        finally
        {
            await DeleteRequestAsync(seeded.RequestId);
            await DeleteFulfillmentLibraryAsync(scope);
        }
    }
}
