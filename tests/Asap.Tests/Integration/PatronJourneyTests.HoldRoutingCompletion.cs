using System.Collections.Concurrent;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task MultiplePendingRequestsReadLivePickupAndParallelNativePatronsKeepIndependentRoutes()
    {
        const int libraryA = 39111;
        const int libraryB = 39112;
        const int registeredA = 391111;
        const int registeredB = 391121;
        const int firstPickup = 391112;
        const int laterPickup = 391113;
        const int pickupB = 391122;
        var reads = new DeterministicTestingPatronProvider();
        var patronA = new PatronSnapshot(391101, "multi-native-a", "a@example.org", "Native", "A", 1, "Adult",
            registeredA, libraryA, "Home A", firstPickup);
        var patronB = new PatronSnapshot(391102, "parallel-native-b", "b@example.org", "Native", "B", 1, "Adult",
            registeredB, libraryB, "Home B", pickupB);
        PickupBranch[] branchesA = [new(firstPickup, "First live pickup"), new(laterPickup, "Later live pickup")];
        reads.AddPatron(patronA, branchesA, libraryA);
        reads.AddPatron(patronB, [new(pickupB, "B live pickup")], libraryB);
        var writes = new ConcurrentRouteWitness(reads);
        await ExecuteNonQueryAsync("""
            INSERT INTO [asap].[Organization] ([Id],[DisplayName],[OrganizationCodeId],[ParentOrganizationId],[IsActive])
            VALUES (@a,N'Home A',2,1,1),(@b,N'Home B',2,1,1),
                (@ra,N'A registered',3,@a,0),(@rb,N'B registered',3,@b,0),
                (@first,N'First live pickup',3,@a,0),(@later,N'Later live pickup',3,@a,0),(@pb,N'B live pickup',3,@b,0);
            """, ("@a", libraryA), ("@b", libraryB), ("@ra", registeredA), ("@rb", registeredB),
            ("@first", firstPickup), ("@later", laterPickup), ("@pb", pickupB));
        await using var scopedFactory = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton<IPatronProvider>(reads);
            services.AddSingleton<IStaffPolarisProvider>(writes);
        }));
        var contexts = scopedFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var requestIds = new List<long>();
        try
        {
            int workstation;
            int user;
            long format;
            await using (var setup = await contexts.CreateDbContextAsync())
            {
                var settings = await setup.PolarisSettings.SingleAsync(item => item.OrganizationId == 1);
                workstation = settings.WorkstationId!.Value;
                user = settings.SystemPolarisUserId!.Value;
                format = await setup.MaterialFormats.Where(item => item.OwnerOrganizationId == 1 && item.Code == "book")
                    .Select(item => item.Id).SingleAsync();
            }
            async Task<TitleRequest> Pending(PatronSnapshot patron, int library, int bib)
            {
                await using var db = await contexts.CreateDbContextAsync();
                var row = new TitleRequest { LibraryOrganizationId = library, PatronOrganizationId = patron.PatronOrganizationId,
                    PatronIdSnapshot = patron.PatronId, Barcode = patron.Barcode, Title = $"Routing title {bib}", AutoHold = true,
                    MaterialFormatId = format, Status = RequestStatus.PendingHold, BibId = bib, BibIdStaffVerified = true,
                    IsbnCheckStatus = "found", PreferredPickupBranchId = 101, PreferredPickupBranchName = "Old ASAP snapshot",
                    CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime, UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime };
                db.TitleRequests.Add(row);
                await db.SaveChangesAsync();
                requestIds.Add(row.Id);
                reads.SetBib(bib, library, new(true, row.Title));
                return row;
            }
            void Expect(PatronSnapshot patron, int bib, int pickup)
            {
                writes.Expect(new(patron.PatronId, bib, pickup, patron.PatronOrganizationId, workstation, user));
            }
            var pending = new[] { await Pending(patronA, libraryA, 391911), await Pending(patronA, libraryA, 391912),
                await Pending(patronA, libraryA, 391913) };
            var placement = scopedFactory.Services.GetRequiredService<HoldPlacementService>();
            Expect(patronA, 391911, firstPickup);
            Assert.AreEqual("updated", (await placement.PlaceBackgroundAsync(pending[0].Id, pending[0].RowVersion, CancellationToken.None)).Code);
            // A single account change affects each remaining unfilled request when it is attempted.
            reads.AddPatron(patronA with { PreferredPickupBranchId = laterPickup }, branchesA, libraryA);
            foreach (var request in pending.Skip(1))
            {
                Expect(patronA, request.BibId!.Value, laterPickup);
                Assert.AreEqual("updated", (await placement.PlaceBackgroundAsync(request.Id, request.RowVersion, CancellationToken.None)).Code);
            }
            var parallelA = await Pending(patronA, libraryA, 391914);
            var parallelB = await Pending(patronB, libraryB, 391915);
            Expect(patronA, parallelA.BibId!.Value, laterPickup);
            Expect(patronB, parallelB.BibId!.Value, pickupB);
            writes.RequireTwoOverlappingCreates = true;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var results = await Task.WhenAll(
                placement.PlaceBackgroundAsync(parallelA.Id, parallelA.RowVersion, deadline.Token),
                placement.PlaceBackgroundAsync(parallelB.Id, parallelB.RowVersion, deadline.Token));
            Assert.IsTrue(results.All(result => result.Code == "updated" && result.FinalStatus == RequestStatus.HoldPlaced));
            Assert.AreEqual(2, writes.OverlappingCreateCount,
                "Both independent native patrons must reach the provider before either create completes.");
            writes.Verify();
            await using var verify = await contexts.CreateDbContextAsync();
            var operations = await verify.HoldPlacementOperations.Where(item => requestIds.Contains(item.TitleRequestId))
                .OrderBy(item => item.TitleRequestId).ToArrayAsync();
            CollectionAssert.AreEqual(new int?[] { firstPickup, laterPickup, laterPickup, laterPickup, pickupB },
                operations.Select(item => item.PickupBranchIdSnapshot).ToArray());
            CollectionAssert.AreEqual(new int?[] { registeredA, registeredA, registeredA, registeredA, registeredB },
                operations.Select(item => item.RequestingOrganizationIdSnapshot).ToArray());
            CollectionAssert.AreEqual(new int?[] { patronA.PatronId, patronA.PatronId, patronA.PatronId, patronA.PatronId, patronB.PatronId },
                operations.Select(item => item.PatronIdSnapshot).ToArray());
            Assert.IsTrue(operations.All(item => item.CreateStartedUtc.HasValue && item.ReplyStartedUtc.HasValue && item.CompletedUtc.HasValue));
            var rows = await verify.TitleRequests.Where(item => requestIds.Contains(item.Id)).ToArrayAsync();
            Assert.IsTrue(rows.All(item => item.Status == RequestStatus.HoldPlaced && item.PreferredPickupBranchId == 101 &&
                item.PreferredPickupBranchName == "Old ASAP snapshot"), "Live routing must not bulk-rewrite recorded ASAP snapshots.");
            Assert.AreEqual(libraryB, rows.Single(item => item.Id == parallelB.Id).LibraryOrganizationId);
            foreach (var (barcode, library) in new[] { (patronA.Barcode, libraryA), (patronB.Barcode, libraryB) })
            {
                var patronReads = reads.Calls.Where(call => string.Equals(call.Key, barcode, StringComparison.OrdinalIgnoreCase)).ToArray();
                Assert.IsTrue(patronReads.Length > 0);
                Assert.IsTrue(patronReads.All(call => call.OrganizationId == library));
            }
            Assert.IsFalse(reads.Calls.Any(call => call.Operation == TestingPolarisOperation.PickupUpdate));
            var finished = rows.Single(item => item.Id == parallelA.Id);
            await placement.PlaceBackgroundAsync(finished.Id, finished.RowVersion, CancellationToken.None);
            writes.Verify(); // The terminal request cannot replay a native write.
        }
        finally
        {
            foreach (var id in requestIds)
            {
                await DeleteRequestAsync(id);
            }
            await ExecuteNonQueryAsync("""
                DELETE FROM [asap].[EmailOutbox] WHERE [OrganizationId] IN (@a,@b);
                DELETE FROM [asap].[Organization] WHERE [Id] IN (@ra,@rb,@first,@later,@pb);
                DELETE FROM [asap].[Organization] WHERE [Id] IN (@a,@b);
                """, ("@a", libraryA), ("@b", libraryB), ("@ra", registeredA), ("@rb", registeredB),
                ("@first", firstPickup), ("@later", laterPickup), ("@pb", pickupB));
        }
    }

    // A narrow external-write witness: reads use the shared strict fixture; writes are
    // keyed by immutable command so concurrent completion order cannot hide crossed routes.
    private sealed class ConcurrentRouteWitness(DeterministicTestingPatronProvider reads) : IStaffPolarisProvider
    {
        private readonly ConcurrentDictionary<HoldCreateCommand, Guid> expected = new();
        private readonly ConcurrentDictionary<HoldCreateCommand, int> creates = new();
        private readonly ConcurrentDictionary<Guid, int> replies = new();
        private readonly TaskCompletionSource bothCreates = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int overlap;
        public bool RequireTwoOverlappingCreates { get; set; }
        public int OverlappingCreateCount => Volatile.Read(ref overlap);
        public void Expect(HoldCreateCommand command) => Assert.IsTrue(expected.TryAdd(command, Guid.NewGuid()));
        public void Verify()
        {
            Assert.AreEqual(expected.Count, creates.Count);
            Assert.AreEqual(expected.Count, replies.Count);
            Assert.IsTrue(creates.Values.All(count => count == 1) && replies.Values.All(count => count == 1));
        }
        public Task<BibValidationResult> ValidateBibAsync(int bibId, int organizationId, CancellationToken cancellationToken) =>
            reads.ValidateBibAsync(bibId, organizationId, cancellationToken);
        public Task<IReadOnlyList<PolarisHoldSnapshot>> GetPatronHoldsAsync(string barcode, int organizationId, CancellationToken cancellationToken) =>
            reads.GetPatronHoldsAsync(barcode, organizationId, cancellationToken);
        public async Task<HoldProviderResult> CreateHoldAsync(HoldCreateCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.IsTrue(expected.TryGetValue(command, out var conversation), $"Crossed or unexpected native create: {command}");
            Assert.AreEqual(1, creates.AddOrUpdate(command, 1, (_, count) => count + 1));
            if (RequireTwoOverlappingCreates)
            {
                if (Interlocked.Increment(ref overlap) == 2)
                {
                    bothCreates.TrySetResult();
                }
                await bothCreates.Task.WaitAsync(cancellationToken);
            }
            return new(HoldProviderOutcome.ReplyRequired, conversation, null, $"group-{command.PatronId}",
                $"qualifier-{command.BibId}", 3, 5, "controlled_reply");
        }
        public Task<HoldProviderResult> ReplyToHoldAsync(HoldReplyCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var original = expected.Single(item => item.Value == command.RequestGuid).Key;
            Assert.AreEqual(new HoldReplyCommand(command.RequestGuid, $"group-{original.PatronId}",
                $"qualifier-{original.BibId}", original.RequestingOrganizationId), command);
            Assert.AreEqual(1, replies.AddOrUpdate(command.RequestGuid, 1, (_, count) => count + 1));
            return Task.FromResult(new HoldProviderResult(HoldProviderOutcome.FinalSuccess, command.RequestGuid,
                original.BibId + 1000, command.TxnGroupQualifier, command.TxnQualifier, 2, 1, "controlled_final_success"));
        }
    }
}
