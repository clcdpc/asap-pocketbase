using System.Text.Json;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow("purchase", "suggestion", true, false)]
    [DataRow("alreadyOwn", "suggestion", false, false)]
    [DataRow("catalogFound", "suggestion", false, false)]
    [DataRow("edit", "outstanding_purchase", true, true)]
    [DataRow("edit", "outstanding_purchase", false, false)]
    [DataRow("edit", "pending_hold", true, false)]
    [DataRow("edit", "pending_hold", false, false)]
    public async Task StaffBibActionsCloseWhenAutomaticHoldIsDisabled(
        string action, string initialStatus, bool initialAutoHold, bool supplyBib)
    {
        var actor = await GetOwnershipTestActorAsync();
        var seeded = await SeedBibOwnershipRequestAsync(
            $"optout-{action}-{initialStatus}", supplyBib ? null : 9001,
            staffVerified: !supplyBib, isbnCheckStatus: supplyBib ? "pending" : "found",
            status: initialStatus, autoHold: initialAutoHold);
        try
        {
            var input = supplyBib || action == "edit"
                ? new TitleRequestActionInput
                {
                    Version = StaffVersion.Encode(seeded.RowVersion),
                    Action = action,
                    Autohold = JsonSerializer.SerializeToElement(false),
                    Bibid = 9001
                }
                : new TitleRequestActionInput
                {
                    Version = StaffVersion.Encode(seeded.RowVersion),
                    Action = action,
                    Autohold = JsonSerializer.SerializeToElement(false)
                };
            var result = await factory!.Services.GetRequiredService<TitleRequestMutationService>()
                .ActionAsync(actor, seeded.Id, input, CancellationToken.None);
            Assert.AreEqual("updated", result.Code);

            var contexts = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
            await using var context = await contexts.CreateDbContextAsync();
            var current = await context.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == seeded.Id);
            Assert.AreEqual("closed", current.Status);
            Assert.AreEqual("purchased_no_hold", current.CloseReason);
            Assert.IsFalse(current.AutoHold);
            Assert.AreEqual(9001, current.BibId);
            Assert.IsTrue(await context.TitleRequestEvents.AsNoTracking().AnyAsync(item =>
                item.TitleRequestId == seeded.Id && item.EventType == "autohold_opt_out"));
            Assert.IsFalse(await context.HoldPlacementOperations.AsNoTracking().AnyAsync(item =>
                item.TitleRequestId == seeded.Id));
        }
        finally
        {
            await DeleteBibOwnershipRequestsAsync([seeded.Id]);
        }
    }
}
