using System.Globalization;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Testing;
using Microsoft.EntityFrameworkCore;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow(null, false, true)]
    [DataRow(9001, false, true)]
    [DataRow(9001, true, true)]
    [DataRow(null, false, false)]
    public async Task AlreadyOwnLinkCommitsVerifiedAssociationAndPolicyWithoutOtherEdits(int? bibId, bool verified, bool autoHold)
    {
        await using var fixture = await SeedPolarisQueueAsync(autoHold: autoHold);
        await PrepareAlreadyOwnFixtureAsync(fixture, bibId, verified);
        var command = AlreadyOwnLinkCommand(fixture.Source);
        var result = await QueueService().ActionAsync(fixture.Actor, fixture.Source.Id, command, CancellationToken.None);
        Assert.AreEqual("updated", result.Code);
        Assert.AreEqual(autoHold ? "pending_hold" : "closed", result.FinalStatus);
        await using var verify = await fixture.Contexts.CreateDbContextAsync();
        var saved = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == fixture.Source.Id);
        Assert.AreEqual(9001, saved.BibId);
        Assert.IsTrue(saved.BibIdStaffVerified);
        Assert.AreEqual(autoHold, saved.AutoHold);
        Assert.AreEqual(fixture.Source.Title, saved.Title);
        Assert.AreEqual(fixture.Source.Author, saved.Author);
        Assert.AreEqual(fixture.Source.Identifier, saved.Identifier);
        Assert.AreEqual(fixture.Source.Notes, saved.Notes);
        Assert.AreEqual(fixture.Source.Publication, saved.Publication);
        Assert.AreEqual(fixture.Source.MaterialFormatId, saved.MaterialFormatId);
        Assert.AreEqual(1, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == saved.Id && item.EventType == "status_changed"));
        var notificationKey = $"staff-patron-action:already_owned:{saved.Id}:";
        var notifications = await verify.EmailOutbox.Where(item => item.BusinessKey!.StartsWith(notificationKey)).ToListAsync();
        Assert.AreEqual(1, notifications.Count);
        Assert.AreEqual(0, fixture.Provider.CreateCommands.Count);
        Assert.AreEqual("stale_version", (await QueueService().ActionAsync(fixture.Actor, saved.Id, command, CancellationToken.None)).Code,
            "A repeated old action cannot append history or another notification.");
        Assert.AreEqual(1, await verify.EmailOutbox.CountAsync(item => item.BusinessKey!.StartsWith(notificationKey)));
    }

    [TestMethod]
    [DataRow("invalid_bib", "bib_not_found")]
    [DataRow("provider_outage", "bib_validation_unavailable")]
    [DataRow("revoke", "staff_scope_forbidden")]
    [DataRow("stale", "stale_version")]
    public async Task AlreadyOwnLinkRechecksValidationActorAndVersionBeforeAtomicWrite(string change, string expected)
    {
        await using var fixture = await SeedPolarisQueueAsync(autoHold: true);
        await PrepareAlreadyOwnFixtureAsync(fixture);
        var command = AlreadyOwnLinkCommand(fixture.Source);
        if (change == "invalid_bib")
        {
            fixture.Provider.SetBib(9001, 2, new(false));
        }
        if (change == "provider_outage")
        {
            fixture.Provider.SetFailure(new(TestingPolarisOperation.BibValidation, 2, NativeId: 9001),
                new PolarisOperationalException("fixture_failure", "Deterministic validation outage"));
        }
        var provider = new QueueReadProbe(fixture.Provider, async () =>
        {
            await using var context = await fixture.Contexts.CreateDbContextAsync();
            if (change == "revoke")
            {
                (await context.StaffUsers.SingleAsync(item => item.Id == fixture.Actor.Id)).IsActive = false;
            }
            else if (change == "stale")
            {
                (await context.TitleRequests.SingleAsync(item => item.Id == fixture.Source.Id)).Notes = "Concurrent saved note";
            }
            await context.SaveChangesAsync();
        });
        var service = QueueServiceWithContexts(fixture.Contexts, provider);
        var result = await service.ActionAsync(fixture.Actor, fixture.Source.Id, command, CancellationToken.None);
        Assert.AreEqual(expected, result.Code);
        await using var verify = await fixture.Contexts.CreateDbContextAsync();
        var saved = await verify.TitleRequests.SingleAsync(item => item.Id == fixture.Source.Id);
        Assert.IsNull(saved.BibId);
        Assert.AreEqual("suggestion", saved.Status);
        Assert.AreEqual(0, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == saved.Id));
        var notificationKey = $"staff-patron-action:already_owned:{saved.Id}:";
        Assert.AreEqual(0, await verify.EmailOutbox.CountAsync(item => item.BusinessKey!.StartsWith(notificationKey)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AlreadyOwnLinkSqlCommitBoundaryRetainsAtomicAssociationAndExactlyOneAction(bool afterCommit)
    {
        await using var fixture = await SeedPolarisQueueAsync(autoHold: true);
        await PrepareAlreadyOwnFixtureAsync(fixture);
        var command = AlreadyOwnLinkCommand(fixture.Source);
        var service = QueueServiceWithContexts(new QueueCommitProbeFactory(databaseConnectionString, afterCommit), fixture.Provider);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ActionAsync(fixture.Actor, fixture.Source.Id, command, CancellationToken.None));
        if (!afterCommit)
        {
            await AssertQueueUnchangedAsync(fixture);
        }
        var retry = await QueueService().ActionAsync(fixture.Actor, fixture.Source.Id, command, CancellationToken.None);
        Assert.AreEqual(afterCommit ? "stale_version" : "updated", retry.Code);
        await using var verify = await fixture.Contexts.CreateDbContextAsync();
        var saved = await verify.TitleRequests.SingleAsync(item => item.Id == fixture.Source.Id);
        Assert.AreEqual(9001, saved.BibId);
        Assert.AreEqual("pending_hold", saved.Status);
        Assert.AreEqual(1, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == saved.Id && item.EventType == "status_changed"));
        var notificationKey = $"staff-patron-action:already_owned:{saved.Id}:";
        Assert.AreEqual(1, await verify.EmailOutbox.CountAsync(item => item.BusinessKey!.StartsWith(notificationKey)));
    }

    [TestMethod]
    [TestCategory(TestCategories.Browser)]
    public async Task AlreadyOwnLinkBrowserPreservesDraftsAndRecoversInsideTheExistingRequestModal()
    {
        factory!.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        await using var missing = await SeedPolarisQueueAsync(autoHold: true);
        await using var invalid = await SeedPolarisQueueAsync(autoHold: true);
        await using var stale = await SeedPolarisQueueAsync(autoHold: true);
        await using var unknown = await SeedPolarisQueueAsync(autoHold: true);
        await using var optout = await SeedPolarisQueueAsync();
        await using var existing = await SeedPolarisQueueAsync(autoHold: true);
        await using var duplicate = await SeedPolarisQueueAsync("pending_hold", true);
        var fixtures = new[] { missing, invalid, stale, unknown, optout, existing, duplicate };
        foreach (var item in fixtures)
        {
            await PrepareAlreadyOwnFixtureAsync(item, item == invalid || item == existing || item == duplicate ? 9001 : null,
                item == existing || item == duplicate);
        }
        await using (var seedDuplicate = await missing.Contexts.CreateDbContextAsync())
        {
            (await seedDuplicate.TitleRequests.SingleAsync(row => row.Id == duplicate.Source.Id)).Barcode = invalid.Source.Barcode;
            await seedDuplicate.SaveChangesAsync();
        }
        missing.Provider.SetBibSearch("title", "Queue fixture", "", "", 2,
            new([new(9001, "Verified catalog title", "Catalog author", "2026", "Book", "CATALOG-ID")], 1));
        var actor = await ReadConfiguredSuperAdminAsync();
        using var client = factory.CreateClient();
        var root = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
        var artifacts = Path.Combine(root, ".artifacts", "i439", $"already-own-link-{Guid.NewGuid():N}");
        var (exitCode, stdout, stderr) = await RunBrowserScriptAsync(root, "already-own-linking.cjs",
            client.BaseAddress!.GetLeftPart(UriPartial.Authority), artifacts,
            new[] { actor.Id.ToString(CultureInfo.InvariantCulture), actor.EntraTenantId.ToString(), actor.AuthenticationEmail }
                .Concat(fixtures.Select(item => item.Source.Id.ToString(CultureInfo.InvariantCulture))).ToArray());
        Assert.AreEqual(0, exitCode, $"{artifacts}{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(artifacts, "browser-results.json")));
        Assert.IsTrue(report.RootElement.GetProperty("staleRetry").GetBoolean());
        Assert.IsTrue(report.RootElement.GetProperty("unknownRecovered").GetBoolean());
        Assert.IsTrue(report.RootElement.GetProperty("reloadStayedReadOnly").GetBoolean());
        Assert.IsTrue(report.RootElement.GetProperty("duplicateDraftsPreserved").GetBoolean());
        Assert.IsTrue(report.RootElement.GetProperty("latestReloadDraftPreserved").GetBoolean());
        Assert.IsTrue(report.RootElement.GetProperty("canceledReloadStayedClosed").GetBoolean());
        await using var verify = await missing.Contexts.CreateDbContextAsync();
        foreach (var item in new[] { missing, stale, unknown, optout, existing })
        {
            var saved = await verify.TitleRequests.AsNoTracking().SingleAsync(row => row.Id == item.Source.Id);
            Assert.AreEqual(item.Source.AutoHold ? "pending_hold" : "closed", saved.Status);
            Assert.AreEqual(9001, saved.BibId);
            Assert.IsTrue(saved.BibIdStaffVerified);
            Assert.AreEqual(item.Source.AutoHold, saved.AutoHold);
            Assert.AreEqual(item.Source.Title, saved.Title, "Catalog linking must not silently save unrelated title drafts.");
            Assert.AreEqual(item.Source.Notes, saved.Notes);
            Assert.AreEqual(1, await verify.TitleRequestEvents.CountAsync(row => row.TitleRequestId == saved.Id && row.EventType == "status_changed"));
            var notificationKey = $"staff-patron-action:already_owned:{saved.Id}:";
            Assert.AreEqual(1, await verify.EmailOutbox.CountAsync(row => row.BusinessKey!.StartsWith(notificationKey)));
        }
        var rejected = await verify.TitleRequests.SingleAsync(row => row.Id == invalid.Source.Id);
        Assert.AreEqual("suggestion", rejected.Status);
        Assert.AreEqual(invalid.Source.Title, rejected.Title);
        Assert.AreEqual(invalid.Source.Notes, rejected.Notes);
        Assert.IsFalse(rejected.BibIdStaffVerified);
        CollectionAssert.AreEqual(invalid.Source.RowVersion, rejected.RowVersion);
        Assert.AreEqual(0, await verify.TitleRequestEvents.CountAsync(row => row.TitleRequestId == rejected.Id));
        Assert.AreEqual(0, missing.Provider.CreateCommands.Count);
    }

    private async Task PrepareAlreadyOwnFixtureAsync(PolarisQueueFixture fixture, int? bibId = null, bool verified = false)
    {
        fixture.Provider.AddPatron(new PatronSnapshot(checked(88001 + (int)fixture.Source.Id), fixture.Source.Barcode, "synthetic@example.org", "Synthetic", "Patron",
            1, "Adult", 101, 2, "Test Library", 101), [new(101, "Main Library")], 2);
        fixture.Provider.SetHolds(fixture.Source.Barcode, 2, []);
        await using var context = await fixture.Contexts.CreateDbContextAsync();
        var request = await context.TitleRequests.SingleAsync(row => row.Id == fixture.Source.Id);
        request.BibId = bibId;
        request.BibIdStaffVerified = verified;
        await context.SaveChangesAsync();
        fixture.Source.BibId = bibId;
        fixture.Source.BibIdStaffVerified = verified;
        fixture.Source.RowVersion = request.RowVersion;
    }

    private static TitleRequestActionCommand AlreadyOwnLinkCommand(Asap.Web.Infrastructure.Data.TitleRequest request) =>
        new TitleRequestActionInput
        {
            Version = StaffVersion.Encode(request.RowVersion), Action = "alreadyOwn", Bibid = 9001, StaffSelectedBibId = 9001
        }.ToCommand();
}
