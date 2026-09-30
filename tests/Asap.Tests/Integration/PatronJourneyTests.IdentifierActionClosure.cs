using System.Text.Json;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow("purchase", null, "outstanding_purchase")]
    [DataRow("catalogFound", 9001, "pending_hold")]
    [DataRow("alreadyOwn", 9001, "pending_hold")]
    [DataRow("silentClose", null, "closed")]
    public async Task StaffActionLeavingSuggestionsDoesNotStrandIdentifierCheck(
        string action, int? bibId, string expectedStatus)
    {
        var actor = await GetOwnershipTestActorAsync();
        var seeded = await SeedBibOwnershipRequestAsync(
            $"pending-{action}", null, staffVerified: false,
            isbnCheckStatus: "pending", status: "suggestion", autoHold: true);
        try
        {
            var input = new TitleRequestActionInput
            {
                Version = StaffVersion.Encode(seeded.RowVersion),
                Action = action,
                Bibid = bibId
            };
            var result = await factory!.Services.GetRequiredService<TitleRequestMutationService>()
                .ActionAsync(actor, seeded.Id, input, CancellationToken.None);
            Assert.AreEqual("updated", result.Code);

            var current = await ReadBibOwnershipRequestAsync(seeded.Id);
            Assert.AreEqual(expectedStatus, current.Status);
            Assert.IsNull(current.IsbnCheckStatus);
            Assert.IsFalse(string.IsNullOrWhiteSpace(current.IsbnCheckResult));
            Assert.AreEqual(0, current.IsbnCheckRetryCount);
            Assert.IsNull(current.IsbnCheckLastErrorCode);
            Assert.AreEqual(0, current.IdentifierTags.Count);

            var contexts = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
            await using var context = await contexts.CreateDbContextAsync();
            Assert.IsFalse(await context.TitleRequests.AsNoTracking().AnyAsync(item =>
                item.Id == seeded.Id && item.Status == "suggestion" && item.IsbnCheckStatus == "pending"));

            if (action == "silentClose")
            {
                var reopened = await factory.Services.GetRequiredService<TitleRequestMutationService>()
                    .ActionAsync(actor, seeded.Id, new TitleRequestActionInput
                    {
                        Version = StaffVersion.Encode(current.RowVersion),
                        Action = "reopen"
                    }, CancellationToken.None);
                Assert.AreEqual("updated", reopened.Code);
                var afterReopen = await ReadBibOwnershipRequestAsync(seeded.Id);
                Assert.AreEqual("suggestion", afterReopen.Status);
                Assert.AreEqual("pending", afterReopen.IsbnCheckStatus);
                Assert.IsNull(afterReopen.IsbnCheckResult);
            }
        }
        finally
        {
            await DeleteBibOwnershipRequestsAsync([seeded.Id]);
        }
    }
}
