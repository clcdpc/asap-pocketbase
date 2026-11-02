using System.Net;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow("create", false)]
    [DataRow("reply", false)]
    [DataRow("create", true)]
    [DataRow("reply", true)]
    public async Task HoldJournalClosesLocalNoDispatchFailureWithoutOperatorRecovery(string phase, bool programmingDefect)
    {
        var patron = new PickupJournalProvider(3485);
        var holds = ScriptedHoldProvider.ReplyRequiredThenSuccess();
        Exception cause = programmingDefect ? new ArgumentException("Broken hold arguments")
            : new PolarisOperationalException("polaris_configuration_incomplete", "Credentials changed before dispatch.");
        var failure = new PolarisMutationNotDispatchedException(cause);
        if (phase == "create")
        {
            holds.CreateException = failure;
        }
        else
        {
            holds.ReplyException = failure;
        }
        await using var scoped = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton<IPatronProvider>(patron);
            services.AddSingleton<IStaffPolarisProvider>(holds);
        }));
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var request = await SeedPickupJournalRequestAsync(contexts, patron);
        try
        {
            var actor = await ReadConfiguredSuperAdminAsync();
            var service = scoped.Services.GetRequiredService<HoldPlacementService>();
            Task<HoldPlacementResult> Place() => service.PlaceAsync(actor, request.Id, new VersionInput(StaffVersion.Encode(request.RowVersion)), CancellationToken.None);
            if (programmingDefect)
            {
                Assert.AreSame(cause, await Assert.ThrowsExactlyAsync<ArgumentException>(Place));
            }
            else
            {
                Assert.AreEqual("hold_provider_error", (await Place()).Code);
            }
            await using var verify = await contexts.CreateDbContextAsync();
            var operation = await verify.HoldPlacementOperations.AsNoTracking().SingleAsync(row => row.TitleRequestId == request.Id);
            Assert.AreEqual("failed", operation.State);
            Assert.AreEqual(phase + "_started", operation.Phase);
            Assert.AreEqual("local_not_dispatched", operation.ResultCode);
            Assert.AreEqual("provider_transport_not_entered", operation.OutcomeEvidenceKind);
            Assert.IsNotNull(operation.CreateStartedUtc, "Durable intent must not be deleted or rewritten as never marked.");
            Assert.AreEqual(phase == "reply", operation.ReplyStartedUtc.HasValue);
            Assert.IsNotNull(operation.CompletedUtc);
            Assert.IsNull(operation.OwnerToken);
            Assert.IsNull(operation.LeaseExpiresUtc);
            Assert.AreEqual("pending_hold", (await verify.TitleRequests.SingleAsync(row => row.Id == request.Id)).Status);
            await service.RecoverBackgroundOperationAsync(operation.Id, patron.OrganizationId, CancellationToken.None);
            Assert.AreEqual(1, holds.CreateCount);
            Assert.AreEqual(phase == "reply" ? 1 : 0, holds.ReplyCount, "Recovery must not replay the failed operation.");
        }
        finally
        {
            await CleanupPickupRemediationAsync(patron.OrganizationId);
        }
    }


}
