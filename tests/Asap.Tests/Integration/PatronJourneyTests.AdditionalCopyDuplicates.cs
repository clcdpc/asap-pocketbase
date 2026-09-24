using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task AdditionalCopyRejectsOpenDuplicateWithoutWritingAndAllowsClosedOrRetainedBib()
    {
        using var client = factory!.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        await using var scope = factory.Services.CreateAsyncScope();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var seed = await contexts.CreateDbContextAsync();
        var formatId = await seed.MaterialFormats.Where(item => item.Code == "book")
            .Select(item => item.Id).FirstAsync();

        TitleRequest Source(string barcode, string title, string status, string? bib = null) => new()
        {
            LibraryOrganizationId = 2,
            Barcode = barcode,
            Title = title,
            MaterialFormatId = formatId,
            Status = status,
            BibId = bib,
            CloseReason = status == "closed" ? "manual" : null,
            AutoHold = false,
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
            UpdatedUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        var duplicateBarcode = $"2{Guid.NewGuid():N}"[..14];
        var source = Source(duplicateBarcode, "Duplicate source", "suggestion");
        var competing = Source(duplicateBarcode, "Existing active request", "pending_hold", "9001");
        var outstanding = Source(duplicateBarcode, "Outstanding source", "outstanding_purchase");
        var changedBib = Source(duplicateBarcode, "Changing BIB", "pending_hold", "9002");
        var closedBarcode = $"2{Guid.NewGuid():N}"[..14];
        var allowed = Source(closedBarcode, "Closed competitor source", "suggestion");
        var closed = Source(closedBarcode, "Closed competitor", "closed", "9001");
        var retained = Source($"2{Guid.NewGuid():N}"[..14], "Retained BIB", "pending_hold", "9001");
        seed.TitleRequests.AddRange(source, competing, outstanding, changedBib, allowed, closed, retained);
        await seed.SaveChangesAsync();

        TitleRequestActionInput Action(TitleRequest request, string bib = "9001") => new()
        {
            Version = StaffVersion.Encode(request.RowVersion),
            Action = "additionalCopy",
            Status = "pending_hold",
            Bibid = JsonSerializer.SerializeToElement(bib),
            EmailPurchaseReminder = true
        };

        var mutations = scope.ServiceProvider.GetRequiredService<TitleRequestMutationService>();
        var initialDispatches = dispatcher!.EnqueuedIds.Count;
        var initialOutboxes = await seed.EmailOutbox.CountAsync(item =>
            item.BusinessKey != null && item.BusinessKey.StartsWith("additional-copy-reminder:"));
        Assert.AreEqual("duplicate_open_request",
            (await mutations.ActionAsync(actor, source.Id, Action(source), CancellationToken.None)).Code);
        Assert.AreEqual("duplicate_open_request",
            (await mutations.ActionAsync(actor, outstanding.Id, Action(outstanding), CancellationToken.None)).Code);
        Assert.AreEqual("duplicate_open_request",
            (await mutations.ActionAsync(actor, changedBib.Id, Action(changedBib), CancellationToken.None)).Code);
        using var conflict = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{source.Id}/action",
            new { version = StaffVersion.Encode(source.RowVersion), action = "additionalCopy", status = "pending_hold", bibid = "9001" });
        Assert.AreEqual(HttpStatusCode.Conflict, conflict.StatusCode, await conflict.Content.ReadAsStringAsync());
        using (var body = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync()))
        {
            Assert.AreEqual("duplicate_open_request", body.RootElement.GetProperty("code").GetString());
        }

        await using (var verify = await contexts.CreateDbContextAsync())
        {
            foreach (var original in new[] { source, competing, outstanding, changedBib })
            {
                var current = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == original.Id);
                Assert.AreEqual(original.Status, current.Status);
                Assert.AreEqual(original.BibId, current.BibId);
                Assert.IsTrue(current.RowVersion.SequenceEqual(original.RowVersion));
                Assert.AreEqual(0, await verify.AdditionalCopyRequests.CountAsync(item =>
                    item.SourceTitleRequestId == original.Id));
            }
            Assert.AreEqual(initialOutboxes, await verify.EmailOutbox.CountAsync(item =>
                item.BusinessKey != null && item.BusinessKey.StartsWith("additional-copy-reminder:")));
        }
        Assert.AreEqual(initialDispatches, dispatcher!.EnqueuedIds.Count);

        using var closeDuplicate = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{source.Id}/action",
            new { version = StaffVersion.Encode(source.RowVersion), action = "closeDuplicate", status = "closed" });
        Assert.AreEqual(HttpStatusCode.OK, closeDuplicate.StatusCode,
            await closeDuplicate.Content.ReadAsStringAsync());

        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, allowed.Id, Action(allowed), CancellationToken.None)).Code);
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, retained.Id, Action(retained), CancellationToken.None)).Code);
        await using var afterSuccess = await contexts.CreateDbContextAsync();
        Assert.AreEqual("closed", (await afterSuccess.TitleRequests.AsNoTracking()
            .SingleAsync(item => item.Id == closed.Id)).Status);
        Assert.AreEqual("pending_hold", (await afterSuccess.TitleRequests.AsNoTracking()
            .SingleAsync(item => item.Id == allowed.Id)).Status);
        Assert.AreEqual(1, await afterSuccess.AdditionalCopyRequests.CountAsync(item =>
            item.SourceTitleRequestId == retained.Id));
    }

    [TestMethod]
    public async Task ConcurrentAdditionalCopyActionsForSamePatronAndBibCannotBothCommit()
    {
        using var client = factory!.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var seed = await contexts.CreateDbContextAsync();
        var formatId = await seed.MaterialFormats.Where(item => item.Code == "book")
            .Select(item => item.Id).FirstAsync();
        var barcode = $"2{Guid.NewGuid():N}"[..14];
        TitleRequest Source(string title) => new()
        {
            LibraryOrganizationId = 2,
            Barcode = barcode,
            Title = title,
            MaterialFormatId = formatId,
            Status = "suggestion",
            AutoHold = false,
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
            UpdatedUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        var first = Source("First concurrent source");
        var second = Source("Second concurrent source");
        seed.TitleRequests.AddRange(first, second);
        await seed.SaveChangesAsync();

        var validationBarrier = new ConcurrentBibValidationBarrier();
        await using var racingFactory = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton<IStaffPolarisProvider>(validationBarrier);
        }));
        using var racingClient = racingFactory.CreateClient();
        await using var racingScope = racingFactory.Services.CreateAsyncScope();
        var mutations = racingScope.ServiceProvider.GetRequiredService<TitleRequestMutationService>();
        async Task<TitleRequestMutationResult> Run(TitleRequest request)
        {
            return await mutations.ActionAsync(actor, request.Id, new TitleRequestActionInput
            {
                Version = StaffVersion.Encode(request.RowVersion),
                Action = "additionalCopy",
                Status = "pending_hold",
                Bibid = JsonSerializer.SerializeToElement("9001")
            }, CancellationToken.None);
        }
        var firstAction = Task.Run(() => Run(first));
        var secondAction = Task.Run(() => Run(second));
        var results = await Task.WhenAll(firstAction, secondAction);
        CollectionAssert.AreEquivalent(new[] { "updated", "duplicate_open_request" },
            results.Select(item => item.Code).ToArray());

        await using var verify = await contexts.CreateDbContextAsync();
        var current = await verify.TitleRequests.AsNoTracking()
            .Where(item => item.Id == first.Id || item.Id == second.Id).ToListAsync();
        Assert.AreEqual(1, current.Count(item => item.Status == "pending_hold" && item.BibId == "9001"));
        Assert.AreEqual(1, current.Count(item => item.Status == "suggestion" && item.BibId == null));
        Assert.AreEqual(1, await verify.AdditionalCopyRequests.CountAsync(item =>
            item.SourceTitleRequestId == first.Id || item.SourceTitleRequestId == second.Id));
    }

    private sealed class ConcurrentBibValidationBarrier : IStaffPolarisProvider
    {
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;

        public async Task<BibValidationResult> ValidateBibAsync(int bibId, CancellationToken cancellationToken)
        {
            Assert.AreEqual(9001, bibId);
            if (Interlocked.Increment(ref arrivals) == 2)
            {
                gate.SetResult();
            }
            await gate.Task.WaitAsync(cancellationToken);
            return new BibValidationResult(true);
        }

        public Task<IReadOnlyList<PolarisHoldSnapshot>> GetPatronHoldsAsync(
            string barcode, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<HoldProviderResult> CreateHoldAsync(
            HoldCreateCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<HoldProviderResult> ReplyToHoldAsync(
            HoldReplyCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
