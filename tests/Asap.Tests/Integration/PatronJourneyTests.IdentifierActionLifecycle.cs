using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Security;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task KestrelTitleActionKeepsCollidingAdditionalCopyUntouched()
    {
        var bibProvider = new ScriptedBibStaffProvider(new Dictionary<int, string?>
        {
            [9003] = "9785555555555"
        });
        using var actionFactory = factory!.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton<IStaffPolarisProvider>(bibProvider);
        }));
        actionFactory.UseKestrel(0);
        using var client = actionFactory.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        await using var scope = actionFactory.Services.CreateAsyncScope();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var seed = await contexts.CreateDbContextAsync();
        var formatId = await seed.MaterialFormats.Where(item => item.Code == "book").Select(item => item.Id).FirstAsync();
        var collisionId = Math.Max(await seed.TitleRequests.Select(item => (long?)item.Id).MaxAsync() ?? 0,
            await seed.AdditionalCopyRequests.Select(item => (long?)item.Id).MaxAsync() ?? 0) + 1000;
        var copyOnlyId = collisionId + 1;
        var barcode = $"2{Guid.NewGuid():N}"[..14];
        await seed.Database.ExecuteSqlInterpolatedAsync($"""
            SET IDENTITY_INSERT [asap].[TitleRequest] ON;
            INSERT INTO [asap].[TitleRequest]
                ([Id], [LibraryOrganizationId], [Barcode], [Title], [Identifier], [AutoHold], [MaterialFormatId],
                 [Status], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
            VALUES ({collisionId}, 2, {barcode}, N'Collision title action', N'9781111111111', 1, {formatId},
                    N'suggestion', N'pending', SYSUTCDATETIME(), SYSUTCDATETIME());
            SET IDENTITY_INSERT [asap].[TitleRequest] OFF;
            """);
        await seed.Database.ExecuteSqlInterpolatedAsync($"""
            SET IDENTITY_INSERT [asap].[AdditionalCopyRequest] ON;
            INSERT INTO [asap].[AdditionalCopyRequest]
                ([Id], [LibraryOrganizationId], [BibId], [Title], [Status], [CreatedUtc], [UpdatedUtc])
            VALUES ({collisionId}, 2, N'8999', N'Collision copy sentinel', N'open', SYSUTCDATETIME(), SYSUTCDATETIME()),
                   ({copyOnlyId}, 2, N'8998', N'Copy-only sentinel', N'open', SYSUTCDATETIME(), SYSUTCDATETIME());
            SET IDENTITY_INSERT [asap].[AdditionalCopyRequest] OFF;
            """);
        var source = await seed.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == collisionId);
        var collidingCopy = await seed.AdditionalCopyRequests.AsNoTracking()
            .SingleAsync(item => item.Id == collisionId);
        var copyOnly = await seed.AdditionalCopyRequests.AsNoTracking()
            .SingleAsync(item => item.Id == copyOnlyId);

        using var action = await client.PostAsJsonAsync($"/api/asap/staff/title-requests/{collisionId}/action",
            new
            {
                version = StaffVersion.Encode(source.RowVersion), action = "catalogFound", status = "pending_hold",
                bibid = "9003", identifier = "9789999999999"
            });
        Assert.AreEqual(HttpStatusCode.OK, action.StatusCode, await action.Content.ReadAsStringAsync());
        using var copyOnlyAction = await client.PostAsJsonAsync($"/api/asap/staff/title-requests/{copyOnlyId}/action",
            new
            {
                version = StaffVersion.Encode(copyOnly.RowVersion), action = "catalogFound", status = "pending_hold",
                bibid = "9003", identifier = "9789999999999"
            });
        Assert.AreEqual(HttpStatusCode.NotFound, copyOnlyAction.StatusCode,
            await copyOnlyAction.Content.ReadAsStringAsync());

        await using var verify = await contexts.CreateDbContextAsync();
        var savedTitle = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == collisionId);
        var savedCopy = await verify.AdditionalCopyRequests.AsNoTracking().SingleAsync(item => item.Id == collisionId);
        var savedCopyOnly = await verify.AdditionalCopyRequests.AsNoTracking().SingleAsync(item => item.Id == copyOnlyId);
        Assert.AreEqual("pending_hold", savedTitle.Status);
        Assert.AreEqual("9003", savedTitle.BibId);
        Assert.AreEqual("9785555555555", savedTitle.Identifier);
        Assert.AreEqual("found", savedTitle.IsbnCheckStatus);
        Assert.AreEqual(collidingCopy.Title, savedCopy.Title);
        Assert.AreEqual(collidingCopy.BibId, savedCopy.BibId);
        Assert.AreEqual(collidingCopy.Status, savedCopy.Status);
        Assert.IsTrue(collidingCopy.RowVersion.SequenceEqual(savedCopy.RowVersion));
        Assert.AreEqual(copyOnly.Title, savedCopyOnly.Title);
        Assert.IsTrue(copyOnly.RowVersion.SequenceEqual(savedCopyOnly.RowVersion));
    }

    [TestMethod]
    public async Task TitleActionsDoNotStrandIdentifierChecksOutsideSuggestions()
    {
        var bibProvider = new ScriptedBibStaffProvider(new Dictionary<int, string?>
        {
            [9002] = null,
            [9003] = "9785555555555"
        });
        using var actionFactory = factory!.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton<IStaffPolarisProvider>(bibProvider);
        }));
        await using var scope = actionFactory.Services.CreateAsyncScope();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var seed = await contexts.CreateDbContextAsync();
        var admin = await seed.StaffUsers.SingleAsync(item => item.NormalizedUserPrincipalName == "ADMIN@EXAMPLE.ORG");
        var formatId = await seed.MaterialFormats.Where(item => item.Code == "book").Select(item => item.Id).FirstAsync();
        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        var actor = new CurrentStaff(admin.Id, admin.NormalizedUserPrincipalName!, Guid.Parse(identity.TenantId!),
            admin.UserPrincipalName, admin.DisplayName, admin.NotificationEmail, "super_admin", 1, "System", true,
            false, null, false, false, false, admin.RowVersion);

        TitleRequest Source(string title, string status, string? identifier = "9781111111111") => new()
        {
            LibraryOrganizationId = 2,
            LibraryNameSnapshot = "Test Library",
            Barcode = $"200{Guid.NewGuid():N}"[..14],
            Title = title,
            MaterialFormatId = formatId,
            Status = status,
            Identifier = identifier,
            IsbnCheckStatus = "pending",
            IsbnCheckRetryCount = 2,
            IsbnCheckLastErrorCode = "temporary_failure",
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
            UpdatedUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        var catalogChanged = Source("Catalog changed", "suggestion");
        catalogChanged.IsbnCheckStatus = "not_found";
        var catalogPending = Source("Catalog pending", "suggestion");
        var catalogNoIdentifier = Source("Catalog has no identifier", "suggestion");
        var catalogEmpty = Source("Neither source nor catalog has identifier", "suggestion", null);
        var purchaseNoBib = Source("Purchase without BIB", "suggestion");
        var purchaseWithBib = Source("Purchase with BIB", "suggestion");
        var purchaseRetainedBib = Source("Purchase with existing BIB", "suggestion");
        purchaseRetainedBib.BibId = "9003";
        var alreadyOwn = Source("Already owned", "suggestion");
        var alreadyOwnRetainedBib = Source("Already owned with existing BIB", "suggestion");
        alreadyOwnRetainedBib.BibId = "9003";
        var catalogFromPurchase = Source("Catalog from outstanding purchase", "outstanding_purchase");
        catalogFromPurchase.BibId = "9003";
        var invalidBib = Source("Invalid catalog BIB", "suggestion");
        var staleVersion = Source("Stale catalog action", "suggestion");
        var editSuggestion = Source("Edit suggestion", "suggestion");
        var clearSuggestion = Source("Clear suggestion identifier", "suggestion");
        var editPurchase = Source("Edit purchase", "outstanding_purchase");
        var editHold = Source("Edit pending hold", "pending_hold");
        editHold.BibId = "9003";
        var rejected = Source("Reject pending check", "suggestion");
        var silentClosed = Source("Silent close pending check", "suggestion");
        var duplicateClosed = Source("Duplicate close pending check", "pending_hold");
        duplicateClosed.BibId = "9003";
        var placedClosed = Source("Close placed pending check", "hold_placed");
        placedClosed.BibId = "9003";
        var reopened = Source("Reopen interrupted check", "suggestion");
        var genuineNotFound = Source("Reopen completed not found", "suggestion");
        genuineNotFound.IsbnCheckStatus = "not_found";
        genuineNotFound.IsbnCheckResult = "Identifier not found in Polaris.";
        var genuineFound = Source("Reopen completed found", "suggestion");
        genuineFound.IsbnCheckStatus = "found";
        genuineFound.IsbnCheckResult = "Identifier found in Polaris.";
        genuineFound.BibId = "9003";
        var withoutIdentifier = Source("Reopen without identifier", "suggestion", null);
        withoutIdentifier.IsbnCheckStatus = "skipped_no_isbn";
        seed.TitleRequests.AddRange(catalogChanged, catalogPending, catalogNoIdentifier, catalogEmpty, purchaseNoBib,
            purchaseWithBib, purchaseRetainedBib, alreadyOwn, alreadyOwnRetainedBib, catalogFromPurchase,
            invalidBib, staleVersion,
            editSuggestion, clearSuggestion, editPurchase, editHold,
            rejected, silentClosed, duplicateClosed, placedClosed, reopened,
            genuineNotFound, genuineFound, withoutIdentifier);
        await seed.SaveChangesAsync();
        var notFoundTagId = await seed.WorkflowTags.Where(item => item.Code == "polaris_bib_not_found")
            .Select(item => item.Id).SingleAsync();
        var foundTagId = await seed.WorkflowTags.Where(item => item.Code == "polaris_bib_found")
            .Select(item => item.Id).SingleAsync();
        seed.TitleRequestWorkflowTags.AddRange(
            new TitleRequestWorkflowTag { TitleRequestId = catalogChanged.Id, WorkflowTagId = notFoundTagId },
            new TitleRequestWorkflowTag { TitleRequestId = catalogNoIdentifier.Id, WorkflowTagId = notFoundTagId },
            new TitleRequestWorkflowTag { TitleRequestId = purchaseNoBib.Id, WorkflowTagId = foundTagId });
        await seed.SaveChangesAsync();

        var mutations = scope.ServiceProvider.GetRequiredService<TitleRequestMutationService>();
        Assert.AreEqual("bib_not_found", (await mutations.ActionAsync(actor, invalidBib.Id,
            new TitleRequestActionInput
            {
                Version = StaffVersion.Encode(invalidBib.RowVersion),
                Action = "catalogFound",
                Status = "pending_hold",
                Bibid = JsonSerializer.SerializeToElement("9999")
            }, CancellationToken.None)).Code);
        await using (var staleWriter = await contexts.CreateDbContextAsync())
        {
            var changed = await staleWriter.TitleRequests.SingleAsync(item => item.Id == staleVersion.Id);
            changed.Title = "Changed after preflight version";
            await staleWriter.SaveChangesAsync();
        }
        Assert.AreEqual("stale_version", (await mutations.ActionAsync(actor, staleVersion.Id,
            new TitleRequestActionInput
            {
                Version = StaffVersion.Encode(staleVersion.RowVersion),
                Action = "catalogFound",
                Status = "pending_hold",
                Bibid = JsonSerializer.SerializeToElement("9003")
            }, CancellationToken.None)).Code);
        await using (var failureVerify = await contexts.CreateDbContextAsync())
        {
            foreach (var original in new[] { invalidBib, staleVersion })
            {
                var unchanged = await failureVerify.TitleRequests.AsNoTracking()
                    .SingleAsync(item => item.Id == original.Id);
                Assert.AreEqual("suggestion", unchanged.Status);
                Assert.AreEqual("pending", unchanged.IsbnCheckStatus);
                Assert.IsNull(unchanged.BibId);
            }
        }

        async Task AssertActionAsync(TitleRequest source, string action, string targetStatus, string? bib,
            string? suppliedIdentifier, string? expectedIdentifier, string expectedCheckStatus)
        {
            var result = await mutations.ActionAsync(actor, source.Id, new TitleRequestActionInput
            {
                Version = StaffVersion.Encode(source.RowVersion),
                Action = action,
                Status = targetStatus,
                Bibid = bib is null ? default : JsonSerializer.SerializeToElement(bib),
                Identifier = suppliedIdentifier is null ? default : JsonSerializer.SerializeToElement(suppliedIdentifier)
            }, CancellationToken.None);
            Assert.AreEqual("updated", result.Code, source.Title);
            await using var verify = await contexts.CreateDbContextAsync();
            var saved = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == source.Id);
            Assert.AreEqual(targetStatus, saved.Status, source.Title);
            Assert.AreEqual(expectedIdentifier, saved.Identifier, source.Title);
            Assert.AreEqual(expectedCheckStatus, saved.IsbnCheckStatus, source.Title);
            Assert.AreNotEqual("pending", saved.IsbnCheckStatus, source.Title);
            Assert.AreEqual(0, saved.IsbnCheckRetryCount, source.Title);
            Assert.IsNull(saved.IsbnCheckLastErrorCode, source.Title);
            var derivedTags = await (from link in verify.TitleRequestWorkflowTags
                join tag in verify.WorkflowTags on link.WorkflowTagId equals tag.Id
                where link.TitleRequestId == source.Id &&
                      (tag.Code == "polaris_bib_found" || tag.Code == "polaris_bib_not_found" ||
                       tag.Code == "polaris_multiple_matches")
                select tag.Code).ToListAsync();
            CollectionAssert.AreEquivalent(expectedCheckStatus == "found" ? ["polaris_bib_found"] : Array.Empty<string>(),
                derivedTags, source.Title);
            if (expectedCheckStatus == "found")
            {
                Assert.IsNotNull(saved.LastCheckedUtc, source.Title);
            }
        }

        await AssertActionAsync(catalogChanged, "catalogFound", "pending_hold", "9003", "9789999999999",
            "9785555555555", "found");
        await AssertActionAsync(catalogPending, "catalogFound", "pending_hold", "9003", null,
            "9785555555555", "found");
        await AssertActionAsync(catalogNoIdentifier, "catalogFound", "pending_hold", "9002", "9789999999999",
            "9781111111111", "not_found");
        await AssertActionAsync(catalogEmpty, "catalogFound", "pending_hold", "9002", null,
            null, "skipped_no_isbn");
        await AssertActionAsync(purchaseNoBib, "purchase", "outstanding_purchase", null, null,
            "9781111111111", "not_found");
        await AssertActionAsync(purchaseWithBib, "purchase", "pending_hold", "9003", null,
            "9785555555555", "found");
        await AssertActionAsync(purchaseRetainedBib, "purchase", "pending_hold", null, null,
            "9785555555555", "found");
        await AssertActionAsync(alreadyOwn, "alreadyOwn", "pending_hold", "9003", null,
            "9785555555555", "found");
        await AssertActionAsync(alreadyOwnRetainedBib, "alreadyOwn", "pending_hold", null, null,
            "9785555555555", "found");
        await AssertActionAsync(catalogFromPurchase, "catalogFound", "pending_hold", null, null,
            "9785555555555", "found");
        await AssertActionAsync(editPurchase, "edit", "outstanding_purchase", null, "9782222222222",
            "9782222222222", "not_found");
        var invalidHoldEdit = await mutations.ActionAsync(actor, editHold.Id, new TitleRequestActionInput
        {
            Version = StaffVersion.Encode(editHold.RowVersion),
            Action = "edit",
            Status = "pending_hold",
            Identifier = JsonSerializer.SerializeToElement("9782222222222")
        }, CancellationToken.None);
        Assert.AreEqual("bib_required", invalidHoldEdit.Code);
        await using (var rejectedHoldVerify = await contexts.CreateDbContextAsync())
        {
            var unchangedHold = await rejectedHoldVerify.TitleRequests.AsNoTracking()
                .SingleAsync(item => item.Id == editHold.Id);
            Assert.AreEqual("9003", unchangedHold.BibId);
            Assert.AreEqual("9781111111111", unchangedHold.Identifier);
            Assert.AreEqual("pending", unchangedHold.IsbnCheckStatus);
        }
        await AssertActionAsync(editHold, "edit", "pending_hold", "9003", "9782222222222",
            "9785555555555", "found");
        await AssertActionAsync(rejected, "reject", "closed", null, null,
            "9781111111111", "not_found");
        await AssertActionAsync(silentClosed, "silentClose", "closed", null, null,
            "9781111111111", "not_found");
        await AssertActionAsync(duplicateClosed, "closeDuplicate", "closed", null, null,
            "9781111111111", "not_found");
        await AssertActionAsync(placedClosed, "close", "closed", null, null,
            "9781111111111", "not_found");

        var suggestionResult = await mutations.ActionAsync(actor, editSuggestion.Id, new TitleRequestActionInput
        {
            Version = StaffVersion.Encode(editSuggestion.RowVersion),
            Action = "edit",
            Status = "suggestion",
            Identifier = JsonSerializer.SerializeToElement("9782222222222")
        }, CancellationToken.None);
        Assert.AreEqual("updated", suggestionResult.Code);
        await using var suggestionVerify = await contexts.CreateDbContextAsync();
        Assert.AreEqual("pending", (await suggestionVerify.TitleRequests.AsNoTracking()
            .SingleAsync(item => item.Id == editSuggestion.Id)).IsbnCheckStatus);

        var cleared = await mutations.ActionAsync(actor, clearSuggestion.Id, new TitleRequestActionInput
        {
            Version = StaffVersion.Encode(clearSuggestion.RowVersion),
            Action = "edit",
            Status = "suggestion",
            Identifier = JsonSerializer.SerializeToElement((string?)null)
        }, CancellationToken.None);
        Assert.AreEqual("updated", cleared.Code);
        await using var clearVerify = await contexts.CreateDbContextAsync();
        var clearedRow = await clearVerify.TitleRequests.AsNoTracking()
            .SingleAsync(item => item.Id == clearSuggestion.Id);
        Assert.IsNull(clearedRow.Identifier);
        Assert.AreEqual("skipped_no_isbn", clearedRow.IsbnCheckStatus);

        foreach (var (source, expectedClosedStatus, expectedReopenStatus, expectedResult) in new[]
        {
            (reopened, "not_found", "pending", (string?)null),
            (genuineNotFound, "not_found", "not_found", "Identifier not found in Polaris."),
            (genuineFound, "found", "found", "Identifier found in Polaris."),
            (withoutIdentifier, "skipped_no_isbn", "skipped_no_isbn", (string?)null)
        })
        {
            var closed = await mutations.ActionAsync(actor, source.Id, new TitleRequestActionInput
            {
                Version = StaffVersion.Encode(source.RowVersion),
                Action = "reject",
                Status = "closed"
            }, CancellationToken.None);
            Assert.AreEqual("updated", closed.Code);
            await using var closedContext = await contexts.CreateDbContextAsync();
            var closedRow = await closedContext.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == source.Id);
            Assert.AreEqual("closed", closedRow.Status);
            Assert.AreEqual(expectedClosedStatus, closedRow.IsbnCheckStatus);
            if (source.Id == reopened.Id)
            {
                Assert.AreEqual("Identifier processing was not completed before this request left suggestions.",
                    closedRow.IsbnCheckResult);
                Assert.AreEqual(0, closedRow.IsbnCheckRetryCount);
                Assert.IsNull(closedRow.IsbnCheckLastErrorCode);
                Assert.IsNull(closedRow.LastCheckedUtc);
            }

            var staleReopen = await mutations.ActionAsync(actor, source.Id, new TitleRequestActionInput
            {
                Version = StaffVersion.Encode(source.RowVersion),
                Action = "reopen",
                Status = "suggestion"
            }, CancellationToken.None);
            Assert.AreEqual("stale_version", staleReopen.Code);
            var reopenedResult = await mutations.ActionAsync(actor, source.Id, new TitleRequestActionInput
            {
                Version = StaffVersion.Encode(closedRow.RowVersion),
                Action = "reopen",
                Status = "suggestion"
            }, CancellationToken.None);
            Assert.AreEqual("updated", reopenedResult.Code);
            await using var reopenedContext = await contexts.CreateDbContextAsync();
            var reopenedRow = await reopenedContext.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == source.Id);
            Assert.AreEqual("suggestion", reopenedRow.Status);
            Assert.AreEqual(expectedReopenStatus, reopenedRow.IsbnCheckStatus);
            Assert.AreEqual(expectedResult, reopenedRow.IsbnCheckResult);
            if (source.Id == reopened.Id)
            {
                Assert.AreEqual(0, reopenedRow.IsbnCheckRetryCount);
                Assert.IsNull(reopenedRow.IsbnCheckLastErrorCode);
                Assert.IsNull(reopenedRow.LastCheckedUtc);
                Assert.IsTrue(await reopenedContext.TitleRequests.AsNoTracking().AnyAsync(item =>
                    item.Id == source.Id && item.Status == "suggestion" && item.IsbnCheckStatus == "pending"));
                Assert.IsFalse(await reopenedContext.TitleRequestWorkflowTags.AnyAsync(item =>
                    item.TitleRequestId == source.Id &&
                    (item.WorkflowTagId == foundTagId || item.WorkflowTagId == notFoundTagId)));
            }
        }
    }

    [TestMethod]
    public async Task CatalogFoundIdentifierReconciliationRollsBackWithAction()
    {
        var bibProvider = new ScriptedBibStaffProvider(new Dictionary<int, string?>
        {
            [9003] = "9785555555555"
        });
        using var actionFactory = factory!.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton<IStaffPolarisProvider>(bibProvider);
        }));
        await using var scope = actionFactory.Services.CreateAsyncScope();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var seed = await contexts.CreateDbContextAsync();
        var admin = await seed.StaffUsers.SingleAsync(item => item.NormalizedUserPrincipalName == "ADMIN@EXAMPLE.ORG");
        var formatId = await seed.MaterialFormats.Where(item => item.Code == "book").Select(item => item.Id).FirstAsync();
        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        var actor = new CurrentStaff(admin.Id, admin.NormalizedUserPrincipalName!, Guid.Parse(identity.TenantId!),
            admin.UserPrincipalName, admin.DisplayName, admin.NotificationEmail, "super_admin", 1, "System", true,
            false, null, false, false, false, admin.RowVersion);
        var source = new TitleRequest
        {
            LibraryOrganizationId = 2,
            Barcode = $"2{Guid.NewGuid():N}"[..14],
            Title = "Rollback title action",
            MaterialFormatId = formatId,
            Status = "suggestion",
            Identifier = "9781111111111",
            IsbnCheckStatus = "not_found",
            IsbnCheckResult = "Earlier check found nothing.",
            IsbnCheckRetryCount = 3,
            IsbnCheckLastErrorCode = "old_error",
            LastCheckedUtc = DateTime.UtcNow.AddDays(-1),
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
            UpdatedUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        seed.TitleRequests.Add(source);
        await seed.SaveChangesAsync();
        var oldTagId = await seed.WorkflowTags.Where(item => item.Code == "polaris_bib_not_found")
            .Select(item => item.Id).SingleAsync();
        seed.TitleRequestWorkflowTags.Add(new TitleRequestWorkflowTag
        {
            TitleRequestId = source.Id,
            WorkflowTagId = oldTagId
        });
        await seed.SaveChangesAsync();
        var mutations = scope.ServiceProvider.GetRequiredService<TitleRequestMutationService>();
        var input = new TitleRequestActionInput
        {
            Version = StaffVersion.Encode(source.RowVersion),
            Action = "catalogFound",
            Status = "pending_hold",
            Bibid = JsonSerializer.SerializeToElement("9003"),
            Identifier = JsonSerializer.SerializeToElement("9789999999999")
        };

        await seed.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER [asap].[TitleActionIdentifierRollbackTest]
            ON [asap].[TitleRequestEvent] AFTER INSERT AS
            BEGIN
                IF EXISTS (SELECT 1 FROM inserted AS event
                    INNER JOIN [asap].[TitleRequest] AS request ON request.[Id] = event.[TitleRequestId]
                    WHERE request.[Title] = N'Rollback title action')
                    THROW 51000, 'Injected title action event failure', 1;
            END;
            """);
        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(async () =>
                await mutations.ActionAsync(actor, source.Id, input, CancellationToken.None));
        }
        finally
        {
            await seed.Database.ExecuteSqlRawAsync("DROP TRIGGER [asap].[TitleActionIdentifierRollbackTest];");
        }

        await using var verify = await contexts.CreateDbContextAsync();
        var saved = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == source.Id);
        Assert.AreEqual("suggestion", saved.Status);
        Assert.AreEqual("9781111111111", saved.Identifier);
        Assert.IsNull(saved.BibId);
        Assert.AreEqual("not_found", saved.IsbnCheckStatus);
        Assert.AreEqual("Earlier check found nothing.", saved.IsbnCheckResult);
        Assert.AreEqual(3, saved.IsbnCheckRetryCount);
        Assert.AreEqual("old_error", saved.IsbnCheckLastErrorCode);
        Assert.AreEqual(source.LastCheckedUtc, saved.LastCheckedUtc);
        Assert.IsTrue(source.RowVersion.SequenceEqual(saved.RowVersion));
        Assert.AreEqual(1, await verify.TitleRequestWorkflowTags.CountAsync(item =>
            item.TitleRequestId == source.Id && item.WorkflowTagId == oldTagId));
        Assert.AreEqual(0, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == source.Id));
    }
}
