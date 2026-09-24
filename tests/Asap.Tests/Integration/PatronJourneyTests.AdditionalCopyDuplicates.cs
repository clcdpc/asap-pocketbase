using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Asap.Web.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task PurchasePromotionDoesNotCreateActiveSamePatronBibDuplicate()
    {
        const int libraryId = 99039;
        await using var scope = factory!.Services.CreateAsyncScope();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var seed = await contexts.CreateDbContextAsync();
        var formatId = await seed.MaterialFormats.Where(item => item.Code == "book")
            .Select(item => item.Id).FirstAsync();
        seed.Organizations.Add(new Organization
        {
            Id = libraryId, DisplayName = "Duplicate promotion library", Abbreviation = "DPL", IsActive = true
        });
        seed.WorkflowSettings.Add(new WorkflowSettings
        {
            OrganizationId = libraryId, AutoPromote = true,
            OutstandingTimeoutEnabled = false, PendingHoldTimeoutEnabled = false,
            HoldPickupTimeoutEnabled = false, AdditionalCopyTimeoutEnabled = false,
            UpdatedUtc = DateTime.UtcNow
        });
        var barcode = $"2{Guid.NewGuid():N}"[..14];
        var competitor = new TitleRequest
        {
            LibraryOrganizationId = libraryId, Barcode = barcode, Title = "Competing open BIB",
            MaterialFormatId = formatId, Status = "suggestion", BibId = "9001", AutoHold = true,
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1), UpdatedUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        var purchase = new TitleRequest
        {
            LibraryOrganizationId = libraryId, Barcode = barcode, Title = "Purchase awaiting promotion",
            MaterialFormatId = formatId, Status = "outstanding_purchase", BibId = "9001", AutoHold = true,
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1), UpdatedUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        seed.TitleRequests.AddRange(competitor, purchase);
        await seed.SaveChangesAsync();
        var initialVersion = purchase.RowVersion.ToArray();
        var processor = scope.ServiceProvider.GetRequiredService<WorkflowProcessingService>();
        var result = await processor.ProcessWorkflowAsync(libraryId, CancellationToken.None);
        Assert.AreEqual("completed", result.Code);
        Assert.AreEqual(0, result.Changed);
        await using var verify = await contexts.CreateDbContextAsync();
        var current = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == purchase.Id);
        Assert.AreEqual("outstanding_purchase", current.Status);
        Assert.IsTrue(current.RowVersion.SequenceEqual(initialVersion));
        Assert.AreEqual(0, await verify.TitleRequestEvents.CountAsync(item =>
            item.TitleRequestId == purchase.Id && item.EventType == "promoted"));
        var progress = await verify.QueueProgress.AsNoTracking()
            .SingleAsync(item => item.QueueName == QueueNames.PurchasePromotion &&
                                 item.ScopeOrganizationId == libraryId);
        Assert.AreEqual("cycle_complete", progress.LastOutcomeCode);
        Assert.AreEqual(purchase.Id, progress.LastOutcomeItemId);
    }

    [TestMethod]
    public async Task BibActionsRespectAutoHoldAndDateClearContract()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        await using var scope = factory!.Services.CreateAsyncScope();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var seed = await contexts.CreateDbContextAsync();
        var formatId = await seed.MaterialFormats.Where(item => item.Code == "book")
            .Select(item => item.Id).FirstAsync();
        var cases = new[]
        {
            (Action: "catalogFound", Source: "suggestion"),
            (Action: "catalogFound", Source: "outstanding_purchase"),
            (Action: "purchase", Source: "suggestion"),
            (Action: "alreadyOwn", Source: "suggestion")
        };
        var requests = new List<(TitleRequest Request, string Action, bool AutoHold)>();
        foreach (var entry in cases)
        {
            foreach (var autoHold in new[] { false, true })
            {
                var request = new TitleRequest
                {
                    LibraryOrganizationId = 2,
                    Barcode = $"2{Guid.NewGuid():N}"[..14],
                    Title = $"{entry.Action} {autoHold}",
                    MaterialFormatId = formatId,
                    Status = entry.Source,
                    AutoHold = autoHold,
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
                    UpdatedUtc = DateTime.UtcNow.AddMinutes(-1)
                };
                seed.TitleRequests.Add(request);
                requests.Add((request, entry.Action, autoHold));
            }
        }
        var copy = new TitleRequest
        {
            LibraryOrganizationId = 2,
            Barcode = $"2{Guid.NewGuid():N}"[..14],
            Title = "Additional-copy opt-out source",
            MaterialFormatId = formatId,
            Status = "suggestion",
            AutoHold = false,
            ExactPublicationDate = new DateOnly(2026, 10, 1),
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
            UpdatedUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        var editOutstanding = new TitleRequest
        {
            LibraryOrganizationId = 2,
            Barcode = $"2{Guid.NewGuid():N}"[..14],
            Title = "Edit outstanding opt-out",
            MaterialFormatId = formatId,
            Status = "outstanding_purchase",
            AutoHold = false,
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
            UpdatedUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        var editOutstandingRetained = new TitleRequest
        {
            LibraryOrganizationId = 2,
            Barcode = $"2{Guid.NewGuid():N}"[..14],
            Title = "Edit outstanding existing BIB opt-out",
            MaterialFormatId = formatId,
            Status = "outstanding_purchase",
            BibId = "9001",
            AutoHold = false,
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
            UpdatedUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        seed.TitleRequests.AddRange(copy, editOutstanding, editOutstandingRetained);
        await seed.SaveChangesAsync();

        var mutations = scope.ServiceProvider.GetRequiredService<TitleRequestMutationService>();
        foreach (var entry in requests)
        {
            var result = await mutations.ActionAsync(actor, entry.Request.Id, new TitleRequestActionInput
            {
                Version = StaffVersion.Encode(entry.Request.RowVersion),
                Action = entry.Action,
                Status = "pending_hold",
                Bibid = JsonSerializer.SerializeToElement("9001")
            }, CancellationToken.None);
            Assert.AreEqual("updated", result.Code, entry.Request.Title);
            await using var verify = await contexts.CreateDbContextAsync();
            var current = await verify.TitleRequests.AsNoTracking()
                .SingleAsync(item => item.Id == entry.Request.Id);
            Assert.AreEqual(entry.AutoHold ? "pending_hold" : "closed", current.Status, entry.Request.Title);
            Assert.AreEqual(entry.AutoHold ? null : "purchased_no_hold", current.CloseReason);
            Assert.AreEqual("9001", current.BibId);
            Assert.AreEqual(0, await verify.HoldPlacementOperations.CountAsync(item =>
                item.TitleRequestId == entry.Request.Id));
            if (!entry.AutoHold)
            {
                Assert.IsTrue(await verify.TitleRequestEvents.AnyAsync(item =>
                    item.TitleRequestId == entry.Request.Id && item.EventType == "autohold_opt_out"));
            }
        }

        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, editOutstanding.Id, new TitleRequestActionInput
        {
            Version = StaffVersion.Encode(editOutstanding.RowVersion), Action = "edit",
            Status = "outstanding_purchase", Bibid = JsonSerializer.SerializeToElement("9001")
        }, CancellationToken.None)).Code);
        await using (var verify = await contexts.CreateDbContextAsync())
        {
            var current = await verify.TitleRequests.AsNoTracking()
                .SingleAsync(item => item.Id == editOutstanding.Id);
            Assert.AreEqual("closed", current.Status);
            Assert.AreEqual("purchased_no_hold", current.CloseReason);
        }
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, editOutstandingRetained.Id,
            new TitleRequestActionInput
            {
                Version = StaffVersion.Encode(editOutstandingRetained.RowVersion), Action = "edit",
                Status = "outstanding_purchase", Bibid = JsonSerializer.SerializeToElement("9001")
            }, CancellationToken.None)).Code);
        await using (var verify = await contexts.CreateDbContextAsync())
        {
            var current = await verify.TitleRequests.AsNoTracking()
                .SingleAsync(item => item.Id == editOutstandingRetained.Id);
            Assert.AreEqual("closed", current.Status);
            Assert.AreEqual("purchased_no_hold", current.CloseReason);
            Assert.AreEqual("9001", current.BibId);
        }

        var pendingToOptOut = requests.First(entry => entry.AutoHold).Request;
        await using (var verify = await contexts.CreateDbContextAsync())
        {
            pendingToOptOut = await verify.TitleRequests.AsNoTracking()
                .SingleAsync(item => item.Id == pendingToOptOut.Id);
        }
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, pendingToOptOut.Id,
            new TitleRequestActionInput
            {
                Version = StaffVersion.Encode(pendingToOptOut.RowVersion), Action = "edit",
                Status = "pending_hold", Autohold = JsonSerializer.SerializeToElement(false)
            }, CancellationToken.None)).Code);
        await using (var verify = await contexts.CreateDbContextAsync())
        {
            var current = await verify.TitleRequests.AsNoTracking()
                .SingleAsync(item => item.Id == pendingToOptOut.Id);
            Assert.AreEqual("closed", current.Status);
            Assert.IsFalse(current.AutoHold);
            Assert.AreEqual("purchased_no_hold", current.CloseReason);
        }

        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, copy.Id, new TitleRequestActionInput
        {
            Version = StaffVersion.Encode(copy.RowVersion),
            Action = "additionalCopy",
            Status = "pending_hold",
            Bibid = JsonSerializer.SerializeToElement("9001"),
            ExactPublicationDate = JsonSerializer.SerializeToElement((string?)null)
        }, CancellationToken.None)).Code);
        await using (var verify = await contexts.CreateDbContextAsync())
        {
            var current = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == copy.Id);
            Assert.AreEqual("pending_hold", current.Status);
            Assert.IsTrue(current.AutoHold);
            Assert.AreEqual(new DateOnly(2026, 10, 1), current.ExactPublicationDate);
            Assert.AreEqual(1, await verify.AdditionalCopyRequests.CountAsync(item =>
                item.SourceTitleRequestId == copy.Id));
        }

        var dateRow = requests[0].Request;
        await using (var verify = await contexts.CreateDbContextAsync())
        {
            dateRow = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == dateRow.Id);
        }
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, dateRow.Id, new TitleRequestActionInput
        {
            Version = StaffVersion.Encode(dateRow.RowVersion), Action = "reopen", Status = "suggestion"
        }, CancellationToken.None)).Code);
        await using (var verify = await contexts.CreateDbContextAsync())
        {
            dateRow = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == dateRow.Id);
        }
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, dateRow.Id, new TitleRequestActionInput
        {
            Version = StaffVersion.Encode(dateRow.RowVersion), Action = "edit", Status = "suggestion",
            ExactPublicationDate = JsonSerializer.SerializeToElement("2026-10-01")
        }, CancellationToken.None)).Code);
        await using (var verify = await contexts.CreateDbContextAsync())
        {
            dateRow = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == dateRow.Id);
            Assert.AreEqual(new DateOnly(2026, 10, 1), dateRow.ExactPublicationDate);
        }
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, dateRow.Id, new TitleRequestActionInput
        {
            Version = StaffVersion.Encode(dateRow.RowVersion), Action = "edit", Status = "suggestion"
        }, CancellationToken.None)).Code);
        await using (var verify = await contexts.CreateDbContextAsync())
        {
            dateRow = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == dateRow.Id);
            Assert.AreEqual(new DateOnly(2026, 10, 1), dateRow.ExactPublicationDate);
        }
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, dateRow.Id, new TitleRequestActionInput
        {
            Version = StaffVersion.Encode(dateRow.RowVersion), Action = "edit", Status = "suggestion",
            ExactPublicationDate = JsonSerializer.SerializeToElement((string?)null)
        }, CancellationToken.None)).Code);
        await using var afterClear = await contexts.CreateDbContextAsync();
        Assert.IsNull((await afterClear.TitleRequests.AsNoTracking()
            .SingleAsync(item => item.Id == dateRow.Id)).ExactPublicationDate);

        var identifierEdit = new TitleRequest
        {
            LibraryOrganizationId = 2,
            Barcode = $"2{Guid.NewGuid():N}"[..14],
            Title = "Identifier edit with old BIB",
            MaterialFormatId = formatId,
            Status = "suggestion",
            AutoHold = true,
            Identifier = "9780000000001",
            BibId = "9002",
            IsbnCheckStatus = "found",
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
            UpdatedUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        afterClear.TitleRequests.Add(identifierEdit);
        await afterClear.SaveChangesAsync();
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, identifierEdit.Id, new TitleRequestActionInput
        {
            Version = StaffVersion.Encode(identifierEdit.RowVersion), Action = "edit", Status = "suggestion",
            Identifier = JsonSerializer.SerializeToElement("9780000000002"),
            Bibid = JsonSerializer.SerializeToElement("9002")
        }, CancellationToken.None)).Code);
        await using var afterIdentifierEdit = await contexts.CreateDbContextAsync();
        var editedIdentifier = await afterIdentifierEdit.TitleRequests.AsNoTracking()
            .SingleAsync(item => item.Id == identifierEdit.Id);
        Assert.AreEqual("9780000000002", editedIdentifier.Identifier);
        Assert.IsNull(editedIdentifier.BibId);
        Assert.AreEqual("pending", editedIdentifier.IsbnCheckStatus);
    }

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
    public async Task OrdinaryBibActionsRejectActiveDuplicatesWithoutPartialWrites()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        await using var scope = factory!.Services.CreateAsyncScope();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var seed = await contexts.CreateDbContextAsync();
        var formatId = await seed.MaterialFormats.Where(item => item.Code == "book")
            .Select(item => item.Id).FirstAsync();
        var barcode = $"2{Guid.NewGuid():N}"[..14];
        TitleRequest Source(string title, string status, string? bib = null, string? patron = null) => new()
        {
            LibraryOrganizationId = 2, Barcode = patron ?? barcode, Title = title,
            MaterialFormatId = formatId, Status = status, BibId = bib, AutoHold = true,
            CloseReason = status == "closed" ? "manual" : null,
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1), UpdatedUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        var competitor = Source("Competitor", "pending_hold", "9001");
        var catalog = Source("Catalog", "suggestion");
        var purchase = Source("Purchase", "suggestion");
        var alreadyOwn = Source("Already own", "suggestion");
        var changed = Source("Changed", "pending_hold", "9002");
        var retained = Source("Retained", "pending_hold", "9001");
        var closedPatron = $"2{Guid.NewGuid():N}"[..14];
        var closed = Source("Closed competitor", "closed", "9001", closedPatron);
        var allowed = Source("Allowed", "suggestion", patron: closedPatron);
        seed.TitleRequests.AddRange(competitor, catalog, purchase, alreadyOwn, changed, retained, closed, allowed);
        await seed.SaveChangesAsync();
        var mutations = scope.ServiceProvider.GetRequiredService<TitleRequestMutationService>();
        var initialEvents = await seed.TitleRequestEvents.CountAsync();
        var initialTags = await seed.TitleRequestWorkflowTags.CountAsync();
        var initialOperations = await seed.HoldPlacementOperations.CountAsync();
        var initialOutbox = await seed.EmailOutbox.CountAsync();
        foreach (var (request, action) in new[]
                 {
                     (catalog, "catalogFound"), (purchase, "purchase"),
                     (alreadyOwn, "alreadyOwn"), (changed, "edit")
                 })
        {
            var result = await mutations.ActionAsync(actor, request.Id, new TitleRequestActionInput
            {
                Version = StaffVersion.Encode(request.RowVersion), Action = action,
                Status = action == "edit" ? "pending_hold" : "pending_hold",
                Bibid = JsonSerializer.SerializeToElement("9001"),
                Notes = "Should roll back"
            }, CancellationToken.None);
            Assert.AreEqual("duplicate_open_request", result.Code, action);
            await using var verify = await contexts.CreateDbContextAsync();
            var current = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id);
            Assert.AreEqual(request.Status, current.Status);
            Assert.AreEqual(request.BibId, current.BibId);
            Assert.IsNull(current.Notes);
            Assert.IsTrue(current.RowVersion.SequenceEqual(request.RowVersion));
            Assert.AreEqual(initialEvents, await verify.TitleRequestEvents.CountAsync());
            Assert.AreEqual(initialTags, await verify.TitleRequestWorkflowTags.CountAsync());
            Assert.AreEqual(initialOperations, await verify.HoldPlacementOperations.CountAsync());
            Assert.AreEqual(initialOutbox, await verify.EmailOutbox.CountAsync());
        }
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, retained.Id, new TitleRequestActionInput
        {
            Version = StaffVersion.Encode(retained.RowVersion), Action = "edit", Status = "pending_hold",
            Bibid = JsonSerializer.SerializeToElement("9001")
        }, CancellationToken.None)).Code);
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, allowed.Id, new TitleRequestActionInput
        {
            Version = StaffVersion.Encode(allowed.RowVersion), Action = "catalogFound", Status = "pending_hold",
            Bibid = JsonSerializer.SerializeToElement("9001")
        }, CancellationToken.None)).Code);
    }

    [TestMethod]
    public async Task ConcurrentCatalogFoundActionsForSamePatronAndBibCannotBothCommit()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        await using var scope = factory!.Services.CreateAsyncScope();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var seed = await contexts.CreateDbContextAsync();
        var formatId = await seed.MaterialFormats.Where(item => item.Code == "book")
            .Select(item => item.Id).FirstAsync();
        var barcode = $"2{Guid.NewGuid():N}"[..14];
        TitleRequest Source(string title) => new()
        {
            LibraryOrganizationId = 2, Barcode = barcode, Title = title,
            MaterialFormatId = formatId, Status = "suggestion", AutoHold = true,
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1), UpdatedUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        var first = Source("First catalog action");
        var second = Source("Second catalog action");
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
        async Task<TitleRequestMutationResult> Run(TitleRequest request) =>
            await mutations.ActionAsync(actor, request.Id, new TitleRequestActionInput
            {
                Version = StaffVersion.Encode(request.RowVersion), Action = "catalogFound",
                Status = "pending_hold", Bibid = JsonSerializer.SerializeToElement("9001")
            }, CancellationToken.None);
        var results = await Task.WhenAll(Task.Run(() => Run(first)), Task.Run(() => Run(second)));
        CollectionAssert.AreEquivalent(new[] { "updated", "duplicate_open_request" },
            results.Select(item => item.Code).ToArray());
        await using var verify = await contexts.CreateDbContextAsync();
        var current = await verify.TitleRequests.AsNoTracking()
            .Where(item => item.Id == first.Id || item.Id == second.Id).ToListAsync();
        Assert.AreEqual(1, current.Count(item => item.Status == "pending_hold" && item.BibId == "9001"));
        Assert.AreEqual(1, current.Count(item => item.Status == "suggestion" && item.BibId == null));
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
