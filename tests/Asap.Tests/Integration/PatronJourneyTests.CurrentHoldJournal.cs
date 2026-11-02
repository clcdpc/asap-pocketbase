using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task CurrentHoldJournalBlocksPlacementReopenAndDeletionWithoutChangingRequestVersion()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var provider = new PickupJournalProvider(38134);
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var request = await SeedPickupJournalRequestAsync(contexts, provider);
        var staffProvider = scoped.Services.GetRequiredService<DeterministicTestingPatronProvider>();
        var creates = staffProvider.CreateCommands.Count;
        var replies = staffProvider.ReplyCommands.Count;
        var actorRevoked = false;
        try
        {
            var originalVersion = request.RowVersion.ToArray();
            await using (var update = await contexts.CreateDbContextAsync())
            {
                var row = await update.TitleRequests.SingleAsync(item => item.Id == request.Id);
                row.Title += " updated";
                await update.SaveChangesAsync();
                request.RowVersion = row.RowVersion;
            }
            await SeedCurrentSuccessfulHoldAsync(contexts, request.Id);
            var placement = scoped.Services.GetRequiredService<HoldPlacementService>();
            Assert.AreEqual("stale_version", (await placement.PlaceAsync(actor, request.Id,
                new VersionInput(StaffVersion.Encode(originalVersion)), CancellationToken.None)).Code);
            Assert.AreEqual("hold_history_retained", (await placement.PlaceAsync(actor, request.Id,
                new VersionInput(StaffVersion.Encode(request.RowVersion)), CancellationToken.None)).Code);

            await using (var update = await contexts.CreateDbContextAsync())
            {
                var row = await update.TitleRequests.SingleAsync(item => item.Id == request.Id);
                CollectionAssert.AreEqual(request.RowVersion, row.RowVersion,
                    "Journal insertion must not rely on a request rowversion change.");
                Assert.AreEqual(1, await update.HoldPlacementOperations.CountAsync(item => item.TitleRequestId == request.Id));
                row.Status = RequestStatus.Closed;
                row.CloseReason = "manual";
                await update.SaveChangesAsync();
                request.RowVersion = row.RowVersion;
            }
            var mutations = scoped.Services.GetRequiredService<TitleRequestMutationService>();
            Assert.AreEqual("hold_history_retained", (await mutations.ActionAsync(actor, request.Id,
                new TitleRequestActionInput { Version = StaffVersion.Encode(request.RowVersion), Action = "reopen" }
                    .ToCommand(), CancellationToken.None)).Code);
            Assert.AreEqual("hold_history_retained", (await mutations.DeleteClosedAsync(actor, request.Id,
                new VersionInput(StaffVersion.Encode(request.RowVersion), StaffVersion.Encode(actor.RowVersion)),
                CancellationToken.None)).Code);

            await SetHoldActorActiveAsync(actor.Id, false);
            actorRevoked = true;
            Assert.AreEqual("staff_scope_forbidden", (await placement.PlaceAsync(actor, request.Id,
                new VersionInput(StaffVersion.Encode(request.RowVersion)), CancellationToken.None)).Code);
            Assert.AreEqual(creates, staffProvider.CreateCommands.Count);
            Assert.AreEqual(replies, staffProvider.ReplyCommands.Count);
            Assert.AreEqual(0, provider.Writes);
            await using var verify = await contexts.CreateDbContextAsync();
            Assert.IsTrue(await verify.TitleRequests.AnyAsync(item => item.Id == request.Id && item.Status == RequestStatus.Closed));
            Assert.AreEqual(1, await verify.HoldPlacementOperations.CountAsync(item =>
                item.TitleRequestId == request.Id && item.State == HoldOperationState.Succeeded));
        }
        finally
        {
            if (actorRevoked)
            {
                await SetHoldActorActiveAsync(actor.Id, true);
            }
            await DeleteRequestAsync(request.Id);
            await CleanupPickupJournalLibraryAsync(provider.OrganizationId);
        }
    }

    [TestMethod]
    public async Task SuccessfulJournalArrivingDuringHoldPreflightPreventsAcquisitionWithoutVersionChange()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var provider = new PickupJournalProvider(38135);
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var request = await SeedPickupJournalRequestAsync(contexts, provider);
        var staffProvider = scoped.Services.GetRequiredService<DeterministicTestingPatronProvider>();
        var creates = staffProvider.CreateCommands.Count;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.AfterRefresh = async token =>
        {
            reached.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var placement = scoped.Services.GetRequiredService<HoldPlacementService>().PlaceAsync(actor, request.Id,
            new VersionInput(StaffVersion.Encode(request.RowVersion)), timeout.Token);
        try
        {
            await reached.Task.WaitAsync(timeout.Token);
            await SeedCurrentSuccessfulHoldAsync(contexts, request.Id);
            release.TrySetResult();
            Assert.AreEqual("hold_history_retained", (await placement).Code);
            Assert.AreEqual(creates, staffProvider.CreateCommands.Count);
            Assert.AreEqual(0, provider.Writes);
            await using var verify = await contexts.CreateDbContextAsync();
            CollectionAssert.AreEqual(request.RowVersion,
                (await verify.TitleRequests.SingleAsync(item => item.Id == request.Id)).RowVersion);
            Assert.AreEqual(1, await verify.HoldPlacementOperations.CountAsync(item => item.TitleRequestId == request.Id));
        }
        finally
        {
            release.TrySetResult();
            provider.AfterRefresh = null;
            await placement;
            await DeleteRequestAsync(request.Id);
            await CleanupPickupJournalLibraryAsync(provider.OrganizationId);
        }
    }

    [TestMethod]
    public async Task SuccessfulJournalArrivingAfterAcquisitionPreventsCreateDispatch()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var provider = ScriptedHoldProvider.ReplyRequiredThenSuccess();
        provider.BlockHoldRead();
        await using var scoped = HoldAuthorityFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var requestId = await SeedPendingHoldRequestAsync("Current journal dispatch barrier", "20000000003216", 93216);
        await using var read = await contexts.CreateDbContextAsync();
        var request = await read.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == requestId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var placement = scoped.Services.GetRequiredService<HoldPlacementService>().PlaceAsync(actor, requestId,
            new VersionInput(StaffVersion.Encode(request.RowVersion)), timeout.Token);
        try
        {
            await provider.HoldReadStarted.Task.WaitAsync(timeout.Token);
            await SeedCurrentSuccessfulHoldAsync(contexts, requestId);
            provider.CompleteBlockedHoldRead();
            Assert.AreEqual("hold_history_retained", (await placement).Code);
            Assert.AreEqual(0, provider.CreateCount);
            Assert.AreEqual(0, provider.ReplyCount);
            await using var verify = await contexts.CreateDbContextAsync();
            Assert.AreEqual(RequestStatus.PendingHold,
                (await verify.TitleRequests.SingleAsync(item => item.Id == requestId)).Status);
            Assert.AreEqual(1, await verify.HoldPlacementOperations.CountAsync(item =>
                item.TitleRequestId == requestId && item.State == HoldOperationState.Succeeded));
            Assert.IsFalse(await verify.HoldPlacementOperations.AnyAsync(item =>
                item.TitleRequestId == requestId && (item.CreateStartedUtc != null || item.ReplyStartedUtc != null)));
        }
        finally
        {
            provider.CompleteBlockedHoldRead();
            await placement;
            await DeleteRequestAsync(requestId);
        }
    }

    private static async Task SeedCurrentSuccessfulHoldAsync(
        IDbContextFactory<AsapDbContext> contexts, long requestId)
    {
        await using var context = await contexts.CreateDbContextAsync();
        var request = await context.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == requestId);
        var attempt = await context.HoldPlacementOperations.Where(item => item.TitleRequestId == requestId)
            .Select(item => (int?)item.AttemptNumber).MaxAsync() ?? 0;
        context.HoldPlacementOperations.Add(new HoldPlacementOperation
        {
            TitleRequestId = requestId, PatronBarcodeSnapshot = request.Barcode, BibIdSnapshot = request.BibId!.Value,
            AttemptNumber = attempt + 1, State = HoldOperationState.Succeeded, Phase = HoldOperationPhase.ResultRecorded,
            ExecutionEpoch = 1, RequestStartedUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }
}
