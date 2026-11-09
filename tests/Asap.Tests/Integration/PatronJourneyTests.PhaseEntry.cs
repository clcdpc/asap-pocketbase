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
    public async Task PhaseEntryTracksRealManualReentryAndIgnoresLaterEdits()
    {
        await using var fixture = await SeedPolarisQueueAsync();
        fixture.Provider.AddPatron(new PatronSnapshot(88002, fixture.Source.Barcode, "phase@example.org", "Phase", "Patron",
            1, "Adult", 101, 2, "Test Library", 101), [new(101, "Main Library")], 2);
        var views = factory!.Services.GetRequiredService<TitleRequestViewService>();
        var now = timeProvider!.GetUtcNow();
        var initial = await views.GetAsync(fixture.Actor, fixture.Source.Id.ToString(), CancellationToken.None);
        Assert.AreEqual(fixture.Source.CreatedUtc, initial!.PhaseEnteredAt, "Missing transition history uses creation, not updates.");
        foreach (var (action, minute) in new[] { ("purchase", 1), ("edit", 2), ("closeDuplicate", 3),
                     ("reopen", 4), ("purchase", 5), ("edit", 6) })
        {
            timeProvider.SetUtcNow(now.AddMinutes(minute));
            var current = await views.GetAsync(fixture.Actor, initial.Id, CancellationToken.None);
            var result = await QueueService().ActionAsync(fixture.Actor, fixture.Source.Id,
                new TitleRequestActionInput { Version = current!.Version, Action = action, Notes = $"Note {minute}" }.ToCommand(),
                CancellationToken.None);
            Assert.AreEqual("updated", result.Code);
            if (minute is 1 or 2 or 5 or 6)
            {
                var expected = now.AddMinutes(minute < 5 ? 1 : 5).UtcDateTime;
                var detail = await views.GetAsync(fixture.Actor, initial.Id, CancellationToken.None);
                Assert.AreEqual(expected, detail!.PhaseEnteredAt);
            }
        }
        await AssertTransitionPhaseAsync(fixture.Source.Id, "status_changed");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PhaseEntryTracksBothAutomaticPromotionDestinations(bool autoHold)
    {
        await GetOwnershipTestActorAsync();
        var scope = await CreateFulfillmentLibraryAsync();
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var seeded = await SeedBibOwnershipRequestAsync("phase-promotion", 9001, staffVerified: true,
            isbnCheckStatus: "found", status: "outstanding_purchase", autoHold: autoHold, libraryOrganizationId: scope);
        long? newerRequestId = null;
        try
        {
            await using (var context = await contexts.CreateDbContextAsync())
            {
                context.WorkflowSettings.Add(new WorkflowSettings { OrganizationId = scope, AutoPromote = true });
                await context.SaveChangesAsync();
            }
            await PrepareSingleItemCycleAsync(QueueNames.PurchasePromotion, scope, seeded.Id);
            await using (var context = await contexts.CreateDbContextAsync())
            {
                var newer = new TitleRequest { LibraryOrganizationId = scope, Barcode = "phase-order", Title = "Newer creation without a transition",
                    MaterialFormatId = await context.MaterialFormats.Select(item => item.Id).FirstAsync(), Status = RequestStatus.Suggestion,
                    CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime.AddTicks(-1), UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime };
                context.TitleRequests.Add(newer);
                await context.SaveChangesAsync();
                newerRequestId = newer.Id;
            }
            var result = await factory.Services.GetRequiredService<WorkflowProcessingService>()
                .ProcessWorkflowAsync(scope, CancellationToken.None);
            Assert.AreEqual("completed", result.Code);
            await using var verify = await contexts.CreateDbContextAsync();
            Assert.AreEqual(autoHold ? RequestStatus.PendingHold : RequestStatus.Closed,
                await verify.TitleRequests.Where(item => item.Id == seeded.Id).Select(item => item.Status).SingleAsync());
            await AssertTransitionPhaseAsync(seeded.Id, "promoted");
            var actor = await ReadConfiguredSuperAdminAsync();
            var list = await factory.Services.GetRequiredService<TitleRequestViewService>()
                .ListAsync(actor, LibraryScope.ForLibrary(scope), CancellationToken.None);
            CollectionAssert.AreEqual(new[] { seeded.Id.ToString(), newerRequestId.Value.ToString() }, list!.Items.Select(item => item.Id).ToArray(),
                "The older request's genuine promotion must sort ahead of a newer creation with no transition.");
        }
        finally
        {
            await DeleteBibOwnershipRequestsAsync([seeded.Id]);
            if (newerRequestId.HasValue)
            {
                await DeleteBibOwnershipRequestsAsync([newerRequestId.Value]);
            }
            await using var context = await contexts.CreateDbContextAsync();
            await context.WorkflowSettings.Where(item => item.OrganizationId == scope).ExecuteDeleteAsync();
            await DeleteFulfillmentLibraryAsync(scope);
        }
    }

    [TestMethod]
    public async Task PhaseEntryPreservesTimestampTiesAndSecondaryListOrdering()
    {
        await using var first = await SeedPolarisQueueAsync();
        await using var second = await SeedPolarisQueueAsync();
        var stamp = timeProvider!.GetUtcNow().UtcDateTime;
        await using (var context = await first.Contexts.CreateDbContextAsync())
        {
            // Equal-time history is intentionally seeded; ordinary cases exercise real producers.
            context.TitleRequestEvents.AddRange(
                new TitleRequestEvent { TitleRequestId = first.Source.Id, EventType = "status_changed", Status = "suggestion",
                    ActorType = "staff", CreatedUtc = stamp },
                new TitleRequestEvent { TitleRequestId = first.Source.Id, EventType = "staff_note", Status = "suggestion",
                    ActorType = "staff", CreatedUtc = stamp.AddHours(1) },
                new TitleRequestEvent { TitleRequestId = first.Source.Id, EventType = "status_changed", Status = "suggestion",
                    ActorType = "staff", CreatedUtc = stamp },
                new TitleRequestEvent { TitleRequestId = second.Source.Id, EventType = "status_changed", Status = "suggestion",
                    ActorType = "staff", CreatedUtc = stamp });
            await context.TitleRequests.Where(item => item.Id == first.Source.Id || item.Id == second.Source.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.CreatedUtc, stamp.AddDays(-1))
                    .SetProperty(item => item.UpdatedUtc, stamp.AddHours(2)));
            await context.SaveChangesAsync();
        }
        await AssertTransitionPhaseAsync(first.Source.Id, "status_changed");
        var service = factory!.Services.GetRequiredService<TitleRequestViewService>();
        var list = await service.ListAsync(first.Actor, LibraryScope.ForLibrary(2), CancellationToken.None);
        var pair = list!.Items.Where(item => item.Id == first.Source.Id.ToString() || item.Id == second.Source.Id.ToString()).ToArray();
        CollectionAssert.AreEqual(new[] { second.Source.Id.ToString(), first.Source.Id.ToString() }, pair.Select(item => item.Id).ToArray());
    }

    private async Task AssertTransitionPhaseAsync(long requestId, string eventType)
    {
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contexts.CreateDbContextAsync();
        var request = await context.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == requestId);
        var transition = await context.TitleRequestEvents.Where(item => item.TitleRequestId == requestId &&
                item.EventType == eventType && item.Status == request.Status)
            .OrderByDescending(item => item.CreatedUtc).ThenByDescending(item => item.Id).FirstAsync();
        var actor = await ReadConfiguredSuperAdminAsync();
        var service = factory.Services.GetRequiredService<TitleRequestViewService>();
        var detail = await service.GetAsync(actor, requestId.ToString(), CancellationToken.None);
        var list = await service.ListAsync(actor, LibraryScope.ForLibrary(request.LibraryOrganizationId), CancellationToken.None);
        Assert.AreEqual(transition.CreatedUtc, detail!.PhaseEnteredAt);
        Assert.AreEqual(DateTimeKind.Utc, detail.PhaseEnteredAt.Kind);
        Assert.AreEqual(detail.PhaseEnteredAt, list!.Items.Single(item => item.Id == detail.Id).PhaseEnteredAt);
        CollectionAssert.AreEqual(detail.Activity.OrderBy(item => item.Created).ThenByDescending(item => item.Source, StringComparer.Ordinal)
            .ThenBy(item => long.Parse(item.Id)).Select(item => item.Id).ToArray(), detail.Activity.Select(item => item.Id).ToArray());
    }
}
