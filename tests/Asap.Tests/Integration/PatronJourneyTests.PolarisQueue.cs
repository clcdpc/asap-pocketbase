using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Testing;
using Asap.Web.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow("suggestion", false, false)]
    [DataRow("suggestion", false, true)]
    [DataRow("suggestion", true, false)]
    [DataRow("suggestion", true, true)]
    [DataRow("outstanding_purchase", false, false)]
    [DataRow("outstanding_purchase", false, true)]
    [DataRow("outstanding_purchase", true, false)]
    [DataRow("outstanding_purchase", true, true)]
    public async Task PolarisQueueAdditionalCopyCommitsTaskForcedAutoholdAuditAndReminder(
        string status, bool autoHold, bool reminder)
    {
        await using var fixture = await SeedPolarisQueueAsync(status, autoHold);
        var command = QueueCommand(fixture.Source, "additionalCopy", reminder);
        var result = await QueueService().ActionAsync(fixture.Actor, fixture.Source.Id, command, CancellationToken.None);
        Assert.AreEqual("updated", result.Code);
        Assert.AreEqual("pending_hold", result.FinalStatus);
        Assert.AreEqual(command.OperationId, result.OperationId);
        await using var verify = await fixture.Contexts.CreateDbContextAsync();
        var source = await verify.TitleRequests.SingleAsync(item => item.Id == fixture.Source.Id);
        var task = await verify.AdditionalCopyRequests.SingleAsync(item => item.SourceTitleRequestId == source.Id);
        Assert.IsTrue(source.AutoHold, "The approved override must be persisted, including when it was initially false.");
        Assert.AreEqual(9001, source.BibId);
        Assert.IsTrue(source.BibIdStaffVerified);
        Assert.AreEqual("pending_hold", source.Status);
        Assert.AreEqual("found", source.IsbnCheckStatus);
        Assert.AreEqual("9780000000001", source.Identifier);
        Assert.AreEqual("Catalog title 9001", source.Title);
        Assert.AreEqual(fixture.Source.Publication, source.Publication);
        Assert.AreEqual(fixture.Source.MaterialFormatId, source.MaterialFormatId);
        Assert.AreEqual(fixture.Source.Notes, source.Notes);
        Assert.AreEqual(source.BibId, task.BibId);
        Assert.AreEqual(source.LibraryOrganizationId, task.LibraryOrganizationId);
        Assert.AreEqual(source.MaterialFormatId, task.MaterialFormatId);
        Assert.AreEqual(source.Identifier, task.Identifier);
        Assert.AreEqual(fixture.Actor.Id, task.CreatedByStaffUserId);
        Assert.AreEqual(fixture.Actor.Id, task.ClaimedByStaffUserId);
        Assert.AreEqual(task.Id.ToString(), result.AdditionalCopyRequestId);
        var history = await verify.TitleRequestEvents.Where(item => item.TitleRequestId == source.Id).ToListAsync();
        Assert.AreEqual(1, history.Count(item => item.EventType == "additional_copy_created"));
        Assert.AreEqual(1, history.Count(item => item.EventType == "polaris_queue_completed"));
        StringAssert.Contains(history.Single(item => item.EventType == "additional_copy_created").MetadataJson!, "\"autoHold\":true");
        var outbox = await verify.EmailOutbox.SingleOrDefaultAsync(item => item.BusinessKey == $"additional-copy-reminder:{task.Id}");
        Assert.AreEqual(reminder, outbox is not null);
        if (reminder)
        {
            Assert.AreEqual(fixture.Actor.Id, outbox!.RecipientStaffUserId);
            Assert.AreEqual(2, outbox.AuthorizationOrganizationId);
            Assert.AreEqual("staff_authorization_sensitive", outbox.DeliveryClass);
        }
        Assert.AreEqual(0, fixture.Provider.CreateCommands.Count);
        Assert.AreEqual(0, fixture.Provider.Calls.Count(call => call.Operation == TestingPolarisOperation.PatronHolds));
    }

    [TestMethod]
    [DataRow(false, null, false)]
    [DataRow(true, null, true)]
    [DataRow(true, false, false)]
    [DataRow(false, true, true)]
    public async Task PolarisQueueReminderUsesStaffDefaultAndExplicitChoice(bool staffDefault, bool? choice, bool expected)
    {
        await using var fixture = await SeedPolarisQueueAsync(reminderDefault: staffDefault);
        var result = await QueueService().ActionAsync(fixture.Actor, fixture.Source.Id,
            QueueCommand(fixture.Source, "additionalCopy", choice), CancellationToken.None);
        Assert.AreEqual("updated", result.Code);
        await using var verify = await fixture.Contexts.CreateDbContextAsync();
        Assert.AreEqual(expected ? 1 : 0, await verify.EmailOutbox.CountAsync(item =>
            item.BusinessKey == $"additional-copy-reminder:{result.AdditionalCopyRequestId}"));
        Assert.IsTrue(expected ? result.NotificationStatus is "queued" or "suppressed" : result.NotificationStatus == "not_requested");
    }

    [TestMethod]
    [DataRow("suggestion", false)]
    [DataRow("suggestion", true)]
    [DataRow("outstanding_purchase", false)]
    [DataRow("outstanding_purchase", true)]
    public async Task PolarisQueueUseBibPreservesAutoholdAndReturnsAuthoritativeState(string status, bool autoHold)
    {
        await using var fixture = await SeedPolarisQueueAsync(status, autoHold);
        using var client = await PolarisQueueClientAsync(fixture.Actor);
        var command = QueueCommand(fixture.Source, "catalogFound");
        using var response = await client.PostAsJsonAsync($"/api/asap/staff/title-requests/{fixture.Source.Id}/action", QueuePayload(command));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var expected = autoHold ? "pending_hold" : "closed";
        Assert.AreEqual(expected, body.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(expected, body.RootElement.GetProperty("finalStatus").GetString());
        Assert.IsTrue(body.RootElement.GetProperty("committed").GetBoolean());
        Assert.AreEqual(command.OperationId, body.RootElement.GetProperty("operationId").GetGuid());
        await using var verify = await fixture.Contexts.CreateDbContextAsync();
        var source = await verify.TitleRequests.SingleAsync(item => item.Id == fixture.Source.Id);
        Assert.AreEqual(autoHold, source.AutoHold);
        Assert.AreEqual(expected, source.Status);
        Assert.AreEqual(9001, source.BibId);
        Assert.AreEqual("found", source.IsbnCheckStatus);
        Assert.AreEqual(0, await verify.AdditionalCopyRequests.CountAsync(item => item.SourceTitleRequestId == source.Id));
        Assert.AreEqual(autoHold ? 0 : 1, await verify.TitleRequestEvents.CountAsync(item =>
            item.TitleRequestId == source.Id && item.EventType == "autohold_opt_out"));
    }

    [TestMethod]
    [DataRow("additionalCopy", "pending_hold")]
    [DataRow("additionalCopy", "hold_placed")]
    [DataRow("additionalCopy", "closed")]
    [DataRow("catalogFound", "pending_hold")]
    [DataRow("catalogFound", "hold_placed")]
    [DataRow("catalogFound", "closed")]
    public async Task PolarisQueueRejectsOtherLifecycleStates(string action, string status)
    {
        await using var fixture = await SeedPolarisQueueAsync(status);
        var result = await QueueService().ActionAsync(fixture.Actor, fixture.Source.Id,
            QueueCommand(fixture.Source, action), CancellationToken.None);
        Assert.AreNotEqual("updated", result.Code);
        await AssertQueueUnchangedAsync(fixture);
    }

    [TestMethod]
    [DataRow("invalid_bib", "bib_not_found")]
    [DataRow("bib_outage", "bib_validation_unavailable")]
    [DataRow("bib_timeout", "bib_validation_unavailable")]
    [DataRow("unowned", "bib_not_owned")]
    [DataRow("unholdable", "bib_not_holdable")]
    [DataRow("holdings_outage", "holdings_unavailable")]
    [DataRow("holdings_timeout", "holdings_unavailable")]
    [DataRow("incoherent_holdings", "holdings_unavailable")]
    public async Task PolarisQueueAdditionalCopyFailsClosedOnProviderEvidence(string condition, string expected)
    {
        await using var fixture = await SeedPolarisQueueAsync();
        var provider = fixture.Provider;
        switch (condition)
        {
            case "invalid_bib": provider.SetBib(9001, 2, new(false)); break;
            case "bib_outage": provider.SetFailure(new(TestingPolarisOperation.BibValidation, 2, NativeId: 9001),
                new PolarisOperationalException("test", "Unavailable")); break;
            case "bib_timeout": provider.SetFailure(new(TestingPolarisOperation.BibValidation, 2, NativeId: 9001),
                new OperationCanceledException("Provider timeout")); break;
            case "unowned": provider.SetBib(9001, 2, new(true), new(0, 0, 0, false, false)); break;
            case "unholdable": provider.SetBib(9001, 2, new(true), new(1, 0, 1, false, false)); break;
            case "holdings_outage": provider.SetFailure(new(TestingPolarisOperation.BibHoldings, 2, NativeId: 9001),
                new PolarisOperationalException("test", "Unavailable")); break;
            case "holdings_timeout": provider.SetFailure(new(TestingPolarisOperation.BibHoldings, 2, NativeId: 9001),
                new OperationCanceledException("Provider timeout")); break;
            case "incoherent_holdings": provider.SetBib(9001, 2, new(true), new(1, 1, 99, true, true)); break;
        }
        var result = await QueueService().ActionAsync(fixture.Actor, fixture.Source.Id,
            QueueCommand(fixture.Source, "additionalCopy"), CancellationToken.None);
        Assert.AreEqual(expected, result.Code);
        await AssertQueueUnchangedAsync(fixture);
    }

    [TestMethod]
    [DataRow("catalogFound", false)]
    [DataRow("additionalCopy", false)]
    [DataRow("catalogFound", true)]
    [DataRow("additionalCopy", true)]
    public async Task PolarisQueueRejectsStaleVersionDuplicateRequestAndForeignScope(string action, bool otherHasPatronId)
    {
        await using var fixture = await SeedPolarisQueueAsync(autoHold: true);
        var command = QueueCommand(fixture.Source, action);
        await using (var change = await fixture.Contexts.CreateDbContextAsync())
        {
            var source = await change.TitleRequests.SingleAsync(item => item.Id == fixture.Source.Id);
            source.Notes = "A concurrent edit";
            await change.SaveChangesAsync();
        }
        Assert.AreEqual("stale_version", (await QueueService().ActionAsync(
            fixture.Actor, fixture.Source.Id, command, CancellationToken.None)).Code);

        await using var duplicate = await SeedPolarisQueueAsync();
        await using (var context = await fixture.Contexts.CreateDbContextAsync())
        {
            var source = await context.TitleRequests.SingleAsync(item => item.Id == fixture.Source.Id);
            var other = await context.TitleRequests.SingleAsync(item => item.Id == duplicate.Source.Id);
            other.Barcode = source.Barcode;
            other.PatronIdSnapshot = otherHasPatronId ? 88001 : null;
            other.BibId = 9001;
            await context.SaveChangesAsync();
            command = QueueCommand(source, action);
        }
        Assert.AreEqual("duplicate_open_request", (await QueueService().ActionAsync(
            fixture.Actor, fixture.Source.Id, command, CancellationToken.None)).Code);

        await using (var context = await fixture.Contexts.CreateDbContextAsync())
        {
            if (!await context.Organizations.AnyAsync(item => item.Id == 3))
            {
                context.Organizations.Add(new Organization
                {
                    Id = 3, DisplayName = "Other queue library", OrganizationCodeId = 2, IsActive = true
                });
            }
            var row = await context.StaffUsers.SingleAsync(item => item.Id == fixture.Actor.Id);
            row.OrganizationId = 3;
            await context.SaveChangesAsync();
        }
        var denied = await QueueService().ActionAsync(fixture.Actor, fixture.Source.Id, command, CancellationToken.None);
        Assert.AreEqual("staff_scope_forbidden", denied.Code);
        await using var verify = await fixture.Contexts.CreateDbContextAsync();
        Assert.AreEqual("suggestion", (await verify.TitleRequests.SingleAsync(item => item.Id == fixture.Source.Id)).Status);
        Assert.AreEqual(0, await verify.AdditionalCopyRequests.CountAsync(item => item.SourceTitleRequestId == fixture.Source.Id));
    }

    [TestMethod]
    [DataRow("catalogFound", true)]
    [DataRow("additionalCopy", true)]
    [DataRow("additionalCopy", false)]
    public async Task PolarisQueueConcurrentAndRepeatedSubmissionsHaveOneDurableOutcome(string action, bool identical)
    {
        await using var fixture = await SeedPolarisQueueAsync();
        var first = QueueCommand(fixture.Source, action);
        var second = identical ? first : first with { OperationId = Guid.NewGuid() };
        var results = await Task.WhenAll(
            QueueService().ActionAsync(fixture.Actor, fixture.Source.Id, first, CancellationToken.None),
            QueueService().ActionAsync(fixture.Actor, fixture.Source.Id, second, CancellationToken.None));
        Assert.AreEqual(identical ? 2 : 1, results.Count(result => result.Code == "updated"));
        Assert.AreEqual(identical ? 1 : 0, results.Count(result => result.Replayed));
        fixture.Provider.SetFailure(new(TestingPolarisOperation.BibValidation, 2, NativeId: 9001),
            new PolarisOperationalException("test", "Offline after commit"));
        var winningCommand = results[0].Code == "updated" ? first : second;
        var replay = await QueueService().ActionAsync(fixture.Actor, fixture.Source.Id, winningCommand, CancellationToken.None);
        Assert.AreEqual("updated", replay.Code, "Recovery must not depend on Polaris remaining available after commit.");
        Assert.IsTrue(replay.Replayed);
        Assert.AreEqual("operation_conflict", (await QueueService().ActionAsync(fixture.Actor, fixture.Source.Id,
            winningCommand with { EmailPurchaseReminder = true }, CancellationToken.None)).Code);
        await using var verify = await fixture.Contexts.CreateDbContextAsync();
        Assert.AreEqual(action == "additionalCopy" ? 1 : 0, await verify.AdditionalCopyRequests.CountAsync(item => item.SourceTitleRequestId == fixture.Source.Id));
        Assert.AreEqual(1, await verify.TitleRequestEvents.CountAsync(item =>
            item.TitleRequestId == fixture.Source.Id && item.EventType == "polaris_queue_completed"));
        Assert.AreEqual(0, fixture.Provider.CreateCommands.Count);
    }

    [TestMethod]
    [DataRow("AdditionalCopyRequest")]
    [DataRow("TitleRequest")]
    [DataRow("TitleRequestEvent")]
    [DataRow("EmailOutbox")]
    public async Task PolarisQueueRollsBackTaskTransitionHistoryReceiptAndReminder(string table)
    {
        await using var fixture = await SeedPolarisQueueAsync();
        var probe = new QueueWriteProbe(table);
        var contexts = new QueueProbeContextFactory(databaseConnectionString, probe);
        var service = QueueServiceWithContexts(contexts, fixture.Provider);
        var command = QueueCommand(fixture.Source, "additionalCopy", true);
        await Assert.ThrowsAsync<DbUpdateException>(() => service.ActionAsync(fixture.Actor, fixture.Source.Id,
            command, CancellationToken.None));
        Assert.IsTrue(probe.Executed, "The SQL write must have executed before injecting failure.");
        await AssertQueueUnchangedAsync(fixture);
        await using var verify = await fixture.Contexts.CreateDbContextAsync();
        Assert.AreEqual(0, await verify.EmailOutbox.CountAsync(item => item.RecipientStaffUserId == fixture.Actor.Id));
        var recovered = await QueueService().ActionAsync(fixture.Actor, fixture.Source.Id,
            command, CancellationToken.None);
        Assert.AreEqual("updated", recovered.Code);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PolarisQueueTimeoutAroundSqlCommitRecoversWithoutDuplicateTask(bool afterCommit)
    {
        await using var fixture = await SeedPolarisQueueAsync();
        var command = QueueCommand(fixture.Source, "additionalCopy");
        var contexts = new QueueCommitProbeFactory(databaseConnectionString, afterCommit);
        var service = QueueServiceWithContexts(contexts, fixture.Provider);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ActionAsync(
            fixture.Actor, fixture.Source.Id, command, CancellationToken.None));
        if (!afterCommit)
        {
            await AssertQueueUnchangedAsync(fixture);
        }
        var recovery = await QueueService().ActionAsync(fixture.Actor, fixture.Source.Id, command, CancellationToken.None);
        Assert.AreEqual("updated", recovery.Code);
        Assert.AreEqual(afterCommit, recovery.Replayed);
        await using var verify = await fixture.Contexts.CreateDbContextAsync();
        Assert.AreEqual(1, await verify.AdditionalCopyRequests.CountAsync(item => item.SourceTitleRequestId == fixture.Source.Id));
        Assert.IsTrue((await verify.TitleRequests.SingleAsync(item => item.Id == fixture.Source.Id)).AutoHold);
    }

    [TestMethod]
    [DataRow("cancel")]
    [DataRow("failure_cancel")]
    [DataRow("revoke")]
    [DataRow("change_bib")]
    public async Task PolarisQueueRechecksCancellationActorAndVersionAfterProviderRead(string change)
    {
        await using var fixture = await SeedPolarisQueueAsync();
        using var cancellation = new CancellationTokenSource();
        var provider = new QueueReadProbe(fixture.Provider, async () =>
        {
            if (change is "cancel" or "failure_cancel")
            {
                cancellation.Cancel();
                if (change == "failure_cancel")
                {
                    throw new PolarisOperationalException("test_failure", "Failure during caller cancellation.");
                }
                return;
            }
            await using var context = await fixture.Contexts.CreateDbContextAsync();
            if (change == "revoke")
            {
                (await context.StaffUsers.SingleAsync(item => item.Id == fixture.Actor.Id)).IsActive = false;
            }
            else
            {
                (await context.TitleRequests.SingleAsync(item => item.Id == fixture.Source.Id)).BibId = 9002;
            }
            await context.SaveChangesAsync();
        });
        var service = CreatePostCommitTitleMutationService(staffPolarisProvider: provider);
        if (change is "cancel" or "failure_cancel")
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.ActionAsync(fixture.Actor, fixture.Source.Id,
                QueueCommand(fixture.Source, "additionalCopy"), cancellation.Token));
            await AssertQueueUnchangedAsync(fixture);
        }
        else
        {
            var result = await service.ActionAsync(fixture.Actor, fixture.Source.Id,
                QueueCommand(fixture.Source, "additionalCopy"), cancellation.Token);
            Assert.AreEqual(change == "revoke" ? "staff_scope_forbidden" : "stale_version", result.Code);
        }
    }

    [TestMethod]
    public async Task PolarisQueueDoesNotBroadenOrdinaryAdditionalCopyEligibilityOrAllowExistingSourceTask()
    {
        await using var fixture = await SeedPolarisQueueAsync();
        var ordinary = factory!.Services.GetRequiredService<AdditionalCopyService>();
        Assert.AreEqual("source_stage_invalid", (await ordinary.PreviewAsync(fixture.Actor, fixture.Source.Id, CancellationToken.None)).Code);
        Assert.AreEqual("source_stage_invalid", (await ordinary.CreateAsync(fixture.Actor, fixture.Source.Id,
            new(StaffVersion.Encode(fixture.Source.RowVersion), false), CancellationToken.None)).Code);
        await using (var context = await fixture.Contexts.CreateDbContextAsync())
        {
            context.AdditionalCopyRequests.Add(new AdditionalCopyRequest
            {
                SourceTitleRequestId = fixture.Source.Id, LibraryOrganizationId = 2, BibId = 9001,
                Title = "Existing source task", Status = "open", CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }
        Assert.AreEqual("additional_copy_exists", (await QueueService().ActionAsync(fixture.Actor, fixture.Source.Id,
            QueueCommand(fixture.Source, "additionalCopy"), CancellationToken.None)).Code);
        await using var verify = await fixture.Contexts.CreateDbContextAsync();
        var source = await verify.TitleRequests.SingleAsync(item => item.Id == fixture.Source.Id);
        Assert.AreEqual("suggestion", source.Status);
        Assert.IsFalse(source.AutoHold);
        Assert.AreEqual(1, await verify.AdditionalCopyRequests.CountAsync(item => item.SourceTitleRequestId == source.Id));
        Assert.AreEqual(0, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == source.Id));
    }

    [TestMethod]
    [DataRow("missing_identity")]
    [DataRow("additional_copy_identity")]
    [DataRow("missing_operation")]
    [DataRow("mismatched_bib")]
    [DataRow("client_autohold")]
    public async Task PolarisQueueRequiresExplicitTypedIdentityCorrelationAndSelectedBib(string invalid)
    {
        await using var fixture = await SeedPolarisQueueAsync();
        var command = QueueCommand(fixture.Source, "additionalCopy");
        command = invalid switch
        {
            "missing_identity" => command with { RequestType = null },
            "additional_copy_identity" => command with { RequestType = "additional_copy" },
            "missing_operation" => command with { OperationId = null },
            "mismatched_bib" => command with { StaffSelectedBibId = 9002 },
            "client_autohold" => command with { Autohold = new(true, false) },
            _ => throw new AssertFailedException("Unknown fixture")
        };
        Assert.AreEqual("invalid_queue_action", (await QueueService().ActionAsync(fixture.Actor, fixture.Source.Id,
            command, CancellationToken.None)).Code);
        await AssertQueueUnchangedAsync(fixture);
    }

    [TestMethod]
    [DataRow("catalogFound", true)]
    [DataRow("catalogFound", false)]
    [DataRow("additionalCopy", true)]
    [DataRow("additionalCopy", false)]
    public async Task PolarisQueueReconcilesSameBibIdentifierWithoutStrandingPendingOrDuplicatingTags(string action, bool hasIdentifier)
    {
        await using var fixture = await SeedPolarisQueueAsync();
        await using (var setup = await fixture.Contexts.CreateDbContextAsync())
        {
            setup.Attach(fixture.Source);
            fixture.Source.BibId = 9001;
            fixture.Source.BibIdStaffVerified = true;
            var tag = await setup.WorkflowTags.SingleAsync(item => item.Code == "polaris_bib_found");
            setup.TitleRequestWorkflowTags.Add(new TitleRequestWorkflowTag { TitleRequestId = fixture.Source.Id, WorkflowTagId = tag.Id });
            await setup.SaveChangesAsync();
        }
        fixture.Provider.SetBib(9001, 2, new(true, "Verified title", Identifier: hasIdentifier ? "CATALOG" : null),
            new(0, 3, 3, true, false));
        var result = await QueueService().ActionAsync(fixture.Actor, fixture.Source.Id,
            QueueCommand(fixture.Source, action), CancellationToken.None);
        Assert.AreEqual("updated", result.Code);
        await using var verify = await fixture.Contexts.CreateDbContextAsync();
        var source = await verify.TitleRequests.SingleAsync(item => item.Id == fixture.Source.Id);
        Assert.AreEqual(hasIdentifier ? "CATALOG" : null, source.Identifier);
        Assert.AreEqual(hasIdentifier ? "found" : "skipped_no_isbn", source.IsbnCheckStatus);
        Assert.AreEqual(hasIdentifier ? 1 : 0, await verify.TitleRequestWorkflowTags.CountAsync(item => item.TitleRequestId == source.Id));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PolarisQueueHttpUnconfirmedCommitCanBeRecoveredByIdenticalSubmission(bool afterCommit)
    {
        await using var fixture = await SeedPolarisQueueAsync();
        var probe = new QueueCommitProbe(afterCommit) { Enabled = false };
        await using var scoped = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDbContextFactory<AsapDbContext>>();
            services.AddSingleton<IDbContextFactory<AsapDbContext>>(new QueueProbeContextFactory(databaseConnectionString, probe));
        }));
        using var client = scoped.CreateClient();
        AddTestingStaffHeaders(client, fixture.Actor.Id, fixture.Actor.EntraTenantId, fixture.Actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        var command = QueueCommand(fixture.Source, "additionalCopy");
        probe.Enabled = true;
        using var uncertain = await client.PostAsJsonAsync($"/api/asap/staff/title-requests/{fixture.Source.Id}/action", QueuePayload(command));
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, uncertain.StatusCode, await uncertain.Content.ReadAsStringAsync());
        using var unknown = JsonDocument.Parse(await uncertain.Content.ReadAsStringAsync());
        Assert.AreEqual("request_outcome_unconfirmed", unknown.RootElement.GetProperty("code").GetString());
        Assert.AreEqual("retry_same_operation", unknown.RootElement.GetProperty("recovery").GetString());
        Assert.AreEqual(command.OperationId, unknown.RootElement.GetProperty("operationId").GetGuid());
        probe.Enabled = false;
        using var recovery = await client.PostAsJsonAsync($"/api/asap/staff/title-requests/{fixture.Source.Id}/action", QueuePayload(command));
        Assert.AreEqual(HttpStatusCode.OK, recovery.StatusCode, await recovery.Content.ReadAsStringAsync());
        using var recovered = JsonDocument.Parse(await recovery.Content.ReadAsStringAsync());
        Assert.IsTrue(recovered.RootElement.GetProperty("committed").GetBoolean());
        Assert.AreEqual(afterCommit, recovered.RootElement.GetProperty("replayed").GetBoolean());
        Assert.AreEqual("pending_hold", recovered.RootElement.GetProperty("queueCommittedStatus").GetString());
    }

    [TestMethod]
    public async Task PolarisQueueCombinedActionRetainsExistingHoldAdoptionAndDoesNotPlaceDuplicateHold()
    {
        await using var fixture = await SeedPolarisQueueAsync();
        fixture.Provider.AddPatron(new PatronSnapshot(88001, fixture.Source.Barcode, "patron@example.org", "Queue", "Patron",
            1, "Adult", 101, 2, "Test Library", 101), [new(101, "Main Library")], 2);
        fixture.Provider.SetHolds(fixture.Source.Barcode, 2, [new(8451, 9001, 3, "Active", 101, fixture.Source.Barcode)]);
        var queued = await QueueService().ActionAsync(fixture.Actor, fixture.Source.Id,
            QueueCommand(fixture.Source, "additionalCopy"), CancellationToken.None);
        Assert.AreEqual("updated", queued.Code);
        await using var context = await fixture.Contexts.CreateDbContextAsync();
        var source = await context.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == fixture.Source.Id);
        var placed = await factory!.Services.GetRequiredService<HoldPlacementService>().PlaceAsync(fixture.Actor, source.Id,
            new(StaffVersion.Encode(source.RowVersion)), CancellationToken.None);
        Assert.AreEqual("updated", placed.Code);
        Assert.AreEqual("hold_placed", placed.FinalStatus);
        var operation = await context.HoldPlacementOperations.AsNoTracking().SingleAsync(item => item.TitleRequestId == source.Id);
        Assert.AreEqual("existing_hold_adoption", operation.OutcomeEvidenceKind);
        Assert.AreEqual(8451, operation.PolarisHoldId);
        Assert.AreEqual(0, fixture.Provider.CreateCommands.Count);
        Assert.AreEqual(1, await context.AdditionalCopyRequests.CountAsync(item => item.SourceTitleRequestId == source.Id));
    }

    private TitleRequestMutationService QueueService() => factory!.Services.GetRequiredService<TitleRequestMutationService>();

    private TitleRequestMutationService QueueServiceWithContexts(
        IDbContextFactory<AsapDbContext> contexts, IStaffPolarisProvider provider)
    {
        var services = factory!.Services;
        return new TitleRequestMutationService(contexts,
            services.GetRequiredService<IEmailSender>(), services.GetRequiredService<RecipientDomainPolicy>(),
            dispatcher!, provider, services.GetRequiredService<IPatronProvider>(),
            services.GetRequiredService<PatronConfigurationService>(), services.GetRequiredService<IIdentifierLookupDispatcher>(),
            NullLogger<TitleRequestMutationService>.Instance, services.GetRequiredService<StaffEligibilityService>(), timeProvider!);
    }

    private static object QueuePayload(TitleRequestActionCommand command) => new
    {
        command.Version, command.OperationId, command.RequestType, command.Action, command.Bibid,
        command.StaffSelectedBibId, command.EmailPurchaseReminder
    };

    private static TitleRequestActionCommand QueueCommand(TitleRequest source, string action, bool? reminder = false) =>
        new TitleRequestActionInput
        {
            Version = StaffVersion.Encode(source.RowVersion), OperationId = Guid.NewGuid(), RequestType = "title_request", Action = action,
            Bibid = 9001, StaffSelectedBibId = 9001, EmailPurchaseReminder = reminder
        }.ToCommand();

    private async Task<HttpClient> PolarisQueueClientAsync(CurrentStaff actor)
    {
        var client = factory!.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        return client;
    }

    private async Task<PolarisQueueFixture> SeedPolarisQueueAsync(
        string status = "suggestion", bool autoHold = false, bool reminderDefault = false)
    {
        using var startup = factory!.CreateClient();
        using var session = await startup.GetAsync("/api/asap/staff/session");
        var admin = await ReadConfiguredSuperAdminAsync();
        var staff = await CreateCorrectiveStaffAsync(admin, "staff", 2);
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contexts.CreateDbContextAsync();
        var row = await context.StaffUsers.SingleAsync(item => item.Id == staff.Id);
        row.AdditionalCopyReminderDefault = reminderDefault;
        row.NotificationEmail = row.UserPrincipalName;
        var formatId = await context.MaterialFormats.Where(item => item.Code == "book" && item.OwnerOrganizationId == 1)
            .Select(item => item.Id).FirstAsync();
        var source = new TitleRequest
        {
            LibraryOrganizationId = 2, LibraryNameSnapshot = "Test Library", Barcode = Guid.NewGuid().ToString("N"),
            Title = "Queue fixture", Author = "Staff author", Identifier = "ORIGINAL", Publication = "Staff publication timing",
            Notes = "Staff draft history remains separate", AutoHold = autoHold, MaterialFormatId = formatId, Status = status,
            CloseReason = status == "closed" ? "manual" : null,
            IsbnCheckStatus = "pending", PreferredPickupBranchId = 101, PreferredPickupBranchName = "Main Library",
            CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime.AddMinutes(-5),
            UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime.AddMinutes(-5)
        };
        context.TitleRequests.Add(source);
        await context.SaveChangesAsync();
        var actor = await ReadCorrectiveStaffAsync(staff);
        var provider = factory.Services.GetRequiredService<DeterministicTestingPatronProvider>();
        return new PolarisQueueFixture(contexts, source, actor, provider);
    }

    private static async Task AssertQueueUnchangedAsync(PolarisQueueFixture fixture)
    {
        await using var verify = await fixture.Contexts.CreateDbContextAsync();
        var source = await verify.TitleRequests.SingleAsync(item => item.Id == fixture.Source.Id);
        Assert.AreEqual(fixture.Source.Status, source.Status);
        Assert.AreEqual(fixture.Source.AutoHold, source.AutoHold);
        CollectionAssert.AreEqual(fixture.Source.RowVersion, source.RowVersion);
        Assert.AreEqual(0, await verify.AdditionalCopyRequests.CountAsync(item => item.SourceTitleRequestId == source.Id));
        Assert.AreEqual(0, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == source.Id));
        Assert.AreEqual(0, fixture.Provider.CreateCommands.Count);
    }

    private sealed record PolarisQueueFixture(
        IDbContextFactory<AsapDbContext> Contexts, TitleRequest Source, CurrentStaff Actor,
        DeterministicTestingPatronProvider Provider) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await using var context = await Contexts.CreateDbContextAsync();
            await context.EmailOutbox.Where(item => item.RecipientStaffUserId == Actor.Id).ExecuteDeleteAsync();
            await context.AdditionalCopyRequests.Where(item => item.SourceTitleRequestId == Source.Id).ExecuteDeleteAsync();
            await context.HoldPlacementOperations.Where(item => item.TitleRequestId == Source.Id).ExecuteDeleteAsync();
            await context.TitleRequests.Where(item => item.Id == Source.Id).ExecuteDeleteAsync();
        }
    }

    private sealed class QueueReadProbe(IStaffPolarisProvider inner, Func<Task> onValidated) : IStaffPolarisProvider
    {
        public async Task<BibValidationResult> ValidateBibAsync(int bibId, int organizationId, CancellationToken token)
        {
            var result = await inner.ValidateBibAsync(bibId, organizationId, token);
            await onValidated();
            return result;
        }
        public Task<StaffBibHoldingsSummary> GetBibHoldingsAsync(int bibId, int organizationId, CancellationToken token) =>
            inner.GetBibHoldingsAsync(bibId, organizationId, token);
        public Task<IReadOnlyList<PolarisHoldSnapshot>> GetPatronHoldsAsync(string barcode, int organizationId, CancellationToken token) =>
            throw new AssertFailedException("Queueing must not place a hold or repeat patron enrichment.");
        public Task<HoldProviderResult> CreateHoldAsync(HoldCreateCommand command, CancellationToken token) =>
            throw new AssertFailedException("Queueing must use the durable hold processor.");
        public Task<HoldProviderResult> ReplyToHoldAsync(HoldReplyCommand command, CancellationToken token) =>
            throw new AssertFailedException("Queueing must use the durable hold processor.");
    }

    private sealed class QueueCommitProbeFactory(string connectionString, bool afterCommit) : IDbContextFactory<AsapDbContext>
    {
        public AsapDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AsapDbContext>()
            .UseSqlServer(connectionString).AddInterceptors(new QueueCommitProbe(afterCommit)).Options);
    }

    private sealed class QueueProbeContextFactory(string connectionString, IInterceptor probe) : IDbContextFactory<AsapDbContext>
    {
        public AsapDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AsapDbContext>()
            .UseSqlServer(connectionString).AddInterceptors(probe).Options);
    }

    private sealed class QueueWriteProbe(string table) : DbCommandInterceptor
    {
        public bool Executed { get; private set; }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            var isReceipt = command.Parameters.Cast<DbParameter>().Any(parameter => Equals(parameter.Value, "polaris_queue_completed"));
            var matches = table == "TitleRequestEvent" ? isReceipt :
                command.CommandText.Contains($"INSERT INTO [asap].[{table}]", StringComparison.Ordinal) ||
                command.CommandText.Contains($"UPDATE [asap].[{table}]", StringComparison.Ordinal);
            if (matches)
            {
                Executed = true;
                result.Dispose();
                throw new DbUpdateException("Injected failure after the SQL write executed.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class QueueCommitProbe(bool afterCommit) : DbTransactionInterceptor
    {
        public bool Enabled { get; set; } = true;
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Enabled && !afterCommit)
            {
                throw new OperationCanceledException("Simulated timeout before SQL commit.");
            }
            return ValueTask.FromResult(result);
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Enabled)
            {
                throw new OperationCanceledException("Simulated lost acknowledgement after SQL commit.");
            }
            return Task.CompletedTask;
        }
    }
}
