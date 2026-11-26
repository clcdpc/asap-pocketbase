using Asap.Web.Features.Analytics;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Asap.Web.Infrastructure.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow(true, false, "error_max_retries")]
    [DataRow(true, true, "not_found")]
    [DataRow(false, false, "error_max_retries")]
    [DataRow(false, false, "not_found")]
    public async Task ApprovedPurchaseDiscoversLaterCatalogRecordAndCompletesUnattended(bool autoHold, bool disableAfterPromotion, string initialStatus)
    {
        var actor = await GetOwnershipTestActorAsync();
        var scope = await CreateFulfillmentLibraryAsync();
        const string identifier = "9780306406157";
        const int bibId = 54301;
        var seeded = await SeedBibOwnershipRequestAsync(identifier, null, false, initialStatus, 5,
            "Earlier identifier check.", "earlier_failure", timeProvider!.GetUtcNow().UtcDateTime.AddDays(-1),
            identifierNotFoundTag: true, autoHold: autoHold, libraryOrganizationId: scope);
        var services = factory!.Services;
        var provider = services.GetRequiredService<DeterministicTestingPatronProvider>();
        var workflow = services.GetRequiredService<WorkflowProcessingService>();
        await using var db = await services.GetRequiredService<IDbContextFactory<AsapDbContext>>().CreateDbContextAsync();
        var row = await db.TitleRequests.SingleAsync(item => item.Id == seeded.Id);
        var systemSettings = await db.WorkflowSettings.SingleAsync(item => item.OrganizationId == 1);
        var originalExclusions = systemSettings.ExcludedPromotionToms;
        try
        {
            row.Notes = "Earlier catalog check recorded by staff.";
            db.WorkflowSettings.Add(new WorkflowSettings { OrganizationId = scope, AutoPromote = true });
            await db.SaveChangesAsync();
            var patron = new PatronSnapshot(7001, row.Barcode, null, "Test", "Patron", 1, "Adult",
                scope + 1, scope, "Library", scope + 1);
            provider.AddPatron(patron, [new(scope + 1, "Registered branch")], scope);
            var approval = await services.GetRequiredService<TitleRequestMutationService>().ActionAsync(actor, row.Id,
                new TitleRequestActionInput { Action = "purchase", Version = StaffVersion.Encode(row.RowVersion) }.ToCommand(), CancellationToken.None);
            Assert.AreEqual("updated", approval.Code);
            await db.Entry(row).ReloadAsync();
            Assert.AreEqual(RequestStatus.OutstandingPurchase, row.Status);
            Assert.AreEqual(initialStatus, row.IsbnCheckStatus);
            Assert.AreEqual(5, row.IsbnCheckRetryCount);
            Assert.AreEqual("earlier_failure", row.IsbnCheckLastErrorCode);
            var analytics = services.GetRequiredService<AnalyticsService>();
            async Task<long> IdentifierFailuresAsync() => (await analytics.GetAsync(actor, LibraryScope.ForLibrary(scope),
                "last30", null, null, CancellationToken.None)).Data!.Exceptions.IdentifierFailures;
            Assert.AreEqual(initialStatus == "error_max_retries" ? 1L : 0L, await IdentifierFailuresAsync());
            var phaseStart = row.UpdatedUtc;
            var notes = row.Notes;
            var emails = await db.EmailOutbox.CountAsync(item => item.OrganizationId == scope);
            var priorEvents = await db.TitleRequestEvents.Where(item => item.TitleRequestId == row.Id).Select(item => item.Id).ToArrayAsync();
            var events = priorEvents.Length;
            provider.SetPurchaseIdentifierResult(identifier, scope, new(IdentifierLookupOutcome.DefinitiveNotFound));
            for (var cycle = 0; initialStatus == "not_found" && cycle < 2; cycle++)
            {
                timeProvider!.SetUtcNow(timeProvider.GetUtcNow().AddMinutes(1));
                Assert.AreEqual("completed", (await workflow.ProcessWorkflowAsync(scope)).Code);
                await db.Entry(row).ReloadAsync();
                Assert.AreEqual(RequestStatus.OutstandingPurchase, row.Status);
                Assert.AreEqual(phaseStart, row.UpdatedUtc);
                Assert.AreEqual(timeProvider.GetUtcNow().UtcDateTime, row.LastPromoterCheckUtc);
                CollectionAssert.AreEquivalent(new[] { "polaris_bib_not_found" },
                    (await ReadBibOwnershipRequestAsync(row.Id)).IdentifierTags.ToArray());
                Assert.AreEqual("No eligible Polaris BIB was found.", row.LastPromoterResult);
                Assert.AreEqual(events, await db.TitleRequestEvents.CountAsync(item => item.TitleRequestId == row.Id));
                Assert.IsFalse(row.HasTrustedBib);
                Assert.AreEqual(notes, row.Notes);
            }
            timeProvider!.SetUtcNow(timeProvider.GetUtcNow().AddMinutes(1));
            provider.SetPurchaseIdentifierResult(identifier, scope, new(IdentifierLookupOutcome.Found, bibId,
                CatalogTitle: "Catalog title", CatalogAuthor: "Catalog author"));
            if (disableAfterPromotion)
            {
                // An open hold scan can already be past a newly promoted purchase.
                var cursor = await db.QueueProgress.SingleAsync(item => item.QueueName == QueueNames.HoldPlacement && item.ScopeOrganizationId == scope);
                cursor.CycleMaxId = cursor.LastItemId = row.Id;
                cursor.LastCreatedUtc = row.CreatedUtc;
                await db.SaveChangesAsync();
            }
            if (autoHold)
            {
                var settings = await db.PolarisSettings.SingleAsync(item => item.OrganizationId == 1);
                var guid = Guid.NewGuid();
                provider.ExpectCreate(new(7001, bibId, scope + 1, scope + 1, settings.WorkstationId!.Value,
                    settings.SystemPolarisUserId!.Value), new(HoldProviderOutcome.ReplyRequired, guid, null, "g", "q", 3, 5, "reply"));
                provider.ExpectReply(new(guid, "g", "q", scope + 1),
                    new(HoldProviderOutcome.FinalSuccess, guid, 54302, "g", "q", 2, 1, "confirmed"));
            }
            Assert.AreEqual("completed", (await workflow.ProcessWorkflowAsync(scope)).Code);
            if (disableAfterPromotion)
            {
                await db.Entry(row).ReloadAsync();
                Assert.AreEqual(RequestStatus.PendingHold, row.Status);
                Assert.IsTrue(TitleRequestWorkflowPolicy.Evaluate(row, false, false).CanPlaceHold);
                systemSettings.ExcludedPromotionToms = "bks";
                await db.SaveChangesAsync();
                await db.WorkflowSettings.Where(item => item.OrganizationId == scope)
                    .ExecuteUpdateAsync(set => set.SetProperty(item => item.AutoPromote, false));
            }
            Assert.AreEqual("completed", (await workflow.ProcessWorkflowAsync(scope)).Code);
            await db.Entry(row).ReloadAsync();
            Assert.AreEqual(autoHold ? RequestStatus.HoldPlaced : RequestStatus.Closed, row.Status);
            Assert.AreEqual(autoHold ? null : "purchased_no_hold", row.CloseReason);
            Assert.AreEqual(bibId, row.BibId);
            Assert.AreEqual(identifier, row.AutoVerifiedIdentifier);
            Assert.AreEqual(bibId, row.AutoVerifiedBibId);
            Assert.IsTrue(row.HasTrustedBib);
            Assert.IsFalse(row.BibIdStaffVerified);
            StringAssert.Contains(row.Notes!, "Earlier catalog check recorded by staff.");
            Assert.AreEqual(events, await db.TitleRequestEvents.CountAsync(item => priorEvents.Contains(item.Id)));
            Assert.AreEqual("found", row.IsbnCheckStatus);
            StringAssert.Contains(row.IsbnCheckResult!, bibId.ToString());
            Assert.AreEqual(row.LastPromoterCheckUtc, row.LastCheckedUtc);
            Assert.IsGreaterThan(phaseStart, row.LastCheckedUtc!.Value);
            Assert.AreEqual(0, row.IsbnCheckRetryCount);
            Assert.IsNull(row.IsbnCheckLastErrorCode);
            Assert.AreEqual(0L, await IdentifierFailuresAsync());
            CollectionAssert.AreEquivalent(new[] { "polaris_bib_found" }, (await ReadBibOwnershipRequestAsync(row.Id)).IdentifierTags.ToArray());
            StringAssert.Contains(row.LastPromoterResult!, "unique Polaris identifier");
            Assert.AreEqual("Catalog title (Staff entered title)", row.Title);
            Assert.AreEqual("Catalog author (Staff entered author)", row.Author);
            var transition = await db.TitleRequestEvents.SingleAsync(item => item.TitleRequestId == row.Id && item.EventType == "promoted");
            Assert.AreEqual(autoHold ? RequestStatus.PendingHold : RequestStatus.Closed, transition.Status);
            var holds = await db.HoldPlacementOperations.Where(item => item.TitleRequestId == row.Id).ToListAsync();
            Assert.AreEqual(autoHold ? 1 : 0, holds.Count);
            if (autoHold)
            {
                Assert.AreEqual(HoldOperationState.Succeeded, holds.Single().State);
                Assert.AreEqual(54302, holds.Single().PolarisHoldId);
            }
            provider.VerifyNoOutstandingWrites();
            Assert.AreEqual(autoHold ? 1 : 0, provider.CreateCommands.Count);
            Assert.AreEqual(emails, await db.EmailOutbox.CountAsync(item => item.OrganizationId == scope && item.MessageKind != "hold_placed"));
            Assert.AreEqual(autoHold ? 1 : 0, await db.EmailOutbox.CountAsync(item => item.OrganizationId == scope && item.MessageKind == "hold_placed"));
            Assert.AreEqual(0, await db.AdditionalCopyRequests.CountAsync(item => item.SourceTitleRequestId == row.Id));
        }
        finally
        {
            if (disableAfterPromotion)
            {
                systemSettings.ExcludedPromotionToms = originalExclusions;
                await db.SaveChangesAsync();
            }
            await db.HoldPlacementOperations.Where(item => item.TitleRequestId == row.Id).ExecuteDeleteAsync();
            await DeleteOwnedEmailFixtureAsync(db, db.EmailOutbox.Where(item => item.OrganizationId == scope));
            await DeleteBibOwnershipRequestsAsync([row.Id]);
            await db.WorkflowSettings.Where(item => item.OrganizationId == scope).ExecuteDeleteAsync();
            await DeleteFulfillmentLibraryAsync(scope);
        }
    }

    [TestMethod]
    [DataRow("identifier")]
    [DataRow("bib")]
    [DataRow("status")]
    [DataRow("notes")]
    [DataRow("autohold")]
    [DataRow("setting")]
    [DataRow("exclusions")]
    [DataRow("inactive")]
    [DataRow("cancel")]
    [DataRow("duplicate")]
    [DataRow("pickup")]
    [DataRow("hold")]
    [DataRow("fence")]
    [DataRow("revoked")]
    public async Task PurchaseDiscoveryRechecksAuthorityAndBarriersAfterPolaris(string change)
    {
        var actor = await GetOwnershipTestActorAsync();
        var scope = await CreateFulfillmentLibraryAsync();
        const string identifier = "001234567890";
        var seeded = await SeedBibOwnershipRequestAsync(identifier, null, false, "not_found",
            status: RequestStatus.OutstandingPurchase, libraryOrganizationId: scope);
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var db = await contexts.CreateDbContextAsync();
        var row = await db.TitleRequests.SingleAsync(item => item.Id == seeded.Id);
        var systemSettings = await db.WorkflowSettings.SingleAsync(item => item.OrganizationId == 1);
        var originalExclusions = systemSettings.ExcludedPromotionToms;
        var settings = new WorkflowSettings { OrganizationId = scope, AutoPromote = true };
        db.WorkflowSettings.Add(settings);
        await db.SaveChangesAsync();
        using var cancel = new CancellationTokenSource();
        var provider = factory.Services.GetRequiredService<DeterministicTestingPatronProvider>();
        provider.SetPurchaseIdentifierResult(identifier, scope, new(IdentifierLookupOutcome.Found, 54301));
        long? duplicateId = null;
        var probe = new QueueReadProbe(provider, async () =>
        {
            switch (change)
            {
                case "identifier": row.Identifier = "001234567891"; break;
                case "bib": row.BibId = 54303; break;
                case "status": row.Status = RequestStatus.Suggestion; break;
                case "notes": row.Notes = "Staff edit"; break;
                case "autohold": row.AutoHold = true; break;
                case "exclusions": systemSettings.ExcludedPromotionToms = "bks"; break;
                case "setting": settings.AutoPromote = false; break;
                case "revoked": (await db.StaffUsers.SingleAsync(item => item.Id == actor.Id)).IsActive = false; break;
                case "inactive": (await db.Organizations.SingleAsync(item => item.Id == scope)).IsActive = false; break;
                case "cancel": cancel.Cancel(); return;
                case "fence": await AdvanceQueueVersionAsync(QueueNames.PurchasePromotion, scope); break;
                case "duplicate":
                    var duplicate = await SeedBibOwnershipRequestAsync(identifier, 54301, false, "found", libraryOrganizationId: scope);
                    duplicateId = duplicate.Id;
                    (await db.TitleRequests.SingleAsync(item => item.Id == duplicate.Id)).Barcode = row.Barcode;
                    break;
                case "pickup":
                    await ExecuteNonQueryAsync("""
                        INSERT INTO [asap].[PickupPreferenceOperation]
                        ([Id],[Barcode],[PatronId],[LibraryOrganizationId],[TitleRequestId],[Origin],[ToPickupBranchId],
                         [ToPickupBranchName],[State],[DispatchStartedUtc],[ConfirmedByRead])
                        VALUES (NEWID(), @barcode, 7001, @scope, @id, N'request', 101, N'Branch', 1, SYSUTCDATETIME(), 0)
                        """, ("@barcode", row.Barcode), ("@scope", scope), ("@id", row.Id));
                    break;
                case "hold":
                    await ExecuteNonQueryAsync("""
                        INSERT INTO [asap].[HoldPlacementOperation]
                        ([TitleRequestId],[PatronBarcodeSnapshot],[BibIdSnapshot],[AttemptNumber],[State],[Phase],[ExecutionEpoch],[RequestStartedUtc])
                        VALUES (@id,@barcode,54301,1,N'operator_required',N'create_started',1,SYSUTCDATETIME())
                        """, ("@id", row.Id), ("@barcode", row.Barcode));
                    break;
            }
            await db.SaveChangesAsync();
        });
        await using var scoped = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<IStaffPolarisProvider>(probe)));
        try
        {
            var workflow = scoped.Services.GetRequiredService<WorkflowProcessingService>();
            var evidence = new StaffIdentityEvidence(actor.Id, actor.AuthenticationEmail, actor.EntraTenantId);
            var cycle = workflow.ProcessWorkflowAsync(scope, cancel.Token, evidence);
            if (change == "cancel")
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => cycle);
            }
            else
            {
                var result = await cycle;
                Assert.AreEqual(change switch { "fence" => "stale_progress_fence", "revoked" => "operational_failure", _ => "completed" }, result.Code);
            }
            Assert.AreEqual(1, provider.Calls.Count(call => call.Operation == TestingPolarisOperation.PurchaseIdentifierLookup));
            await db.Entry(row).ReloadAsync();
            Assert.AreEqual(change == "status" ? RequestStatus.Suggestion : RequestStatus.OutstandingPurchase, row.Status);
            Assert.AreEqual(change == "bib" ? 54303 : null, row.BibId);
            Assert.IsFalse(row.HasTrustedBib);
            Assert.IsNull(row.AutoVerifiedIdentifier);
            Assert.IsFalse(await db.TitleRequestEvents.AnyAsync(item => item.TitleRequestId == row.Id && item.EventType == "promoted"));
        }
        finally
        {
            if (change == "exclusions")
            {
                systemSettings.ExcludedPromotionToms = originalExclusions;
                await db.SaveChangesAsync();
            }
            if (change == "revoked")
            {
                await db.StaffUsers.Where(item => item.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(item => item.IsActive, true));
            }
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM [asap].[PickupPreferenceOperation] WHERE [TitleRequestId] = {row.Id}");
            await DeleteRequestAsync(seeded.Id);
            if (duplicateId.HasValue)
            {
                await DeleteRequestAsync(duplicateId.Value);
            }
            await db.WorkflowSettings.Where(item => item.OrganizationId == scope).ExecuteDeleteAsync();
            await DeleteFulfillmentLibraryAsync(scope);
        }
    }

    [TestMethod]
    public async Task PurchaseDiscoveryHonorsInheritanceAndRequiresConfirmationOfAssignedBib()
    {
        var actor = await GetOwnershipTestActorAsync();
        var scope = await CreateFulfillmentLibraryAsync();
        const string identifier = "9780306406157";
        var seeded = await SeedBibOwnershipRequestAsync(identifier, 543, false, "found",
            status: RequestStatus.OutstandingPurchase, libraryOrganizationId: scope);
        var services = factory!.Services;
        await using var db = await services.GetRequiredService<IDbContextFactory<AsapDbContext>>().CreateDbContextAsync();
        var system = await db.WorkflowSettings.SingleAsync(item => item.OrganizationId == 1);
        var before = system.AutoPromote;
        var library = new WorkflowSettings { OrganizationId = scope };
        var row = await db.TitleRequests.SingleAsync(item => item.Id == seeded.Id);
        db.WorkflowSettings.Add(library);
        var workflow = services.GetRequiredService<WorkflowProcessingService>();
        var provider = services.GetRequiredService<DeterministicTestingPatronProvider>();
        async Task VisitAsync()
        {
            await db.SaveChangesAsync();
            Assert.AreEqual("completed", (await workflow.ProcessWorkflowAsync(scope)).Code);
            await db.Entry(row).ReloadAsync();
        }
        try
        {
            system.AutoPromote = false;
            await VisitAsync();
            system.AutoPromote = true;
            library.AutoPromote = false;
            await VisitAsync();
            Assert.IsNull(row.LastPromoterCheckUtc);
            Assert.IsFalse(provider.Calls.Any(call => call.Operation == TestingPolarisOperation.PurchaseIdentifierLookup));

            library.AutoPromote = null;
            provider.SetPurchaseIdentifierResult(identifier, scope, new(IdentifierLookupOutcome.Found, MultipleMatches: true));
            await VisitAsync();
            CollectionAssert.Contains((await ReadBibOwnershipRequestAsync(row.Id)).IdentifierTags.ToArray(), "polaris_multiple_matches");
            Assert.AreEqual(RequestStatus.OutstandingPurchase, row.Status);
            Assert.IsFalse(row.HasTrustedBib);

            provider.SetFailure(new(TestingPolarisOperation.PurchaseIdentifierLookup, scope, identifier), new TimeoutException());
            await VisitAsync();
            Assert.AreEqual(0, (await ReadBibOwnershipRequestAsync(row.Id)).IdentifierTags.Count);
            StringAssert.Contains(row.LastPromoterResult!, "check again");
            system.AutoPromote = false;
            library.AutoPromote = true;
            provider.SetPurchaseIdentifierResult(identifier, scope, new(IdentifierLookupOutcome.Found, 544));
            await VisitAsync();
            Assert.AreEqual(543, row.BibId);
            Assert.IsFalse(row.HasTrustedBib);
            Assert.AreEqual(RequestStatus.OutstandingPurchase, row.Status);
            StringAssert.Contains(row.LastPromoterResult!, "544");

            provider.SetPurchaseIdentifierResult(identifier, scope, new(IdentifierLookupOutcome.Found, 543));
            var denied = await workflow.ProcessWorkflowAsync(scope, default,
                new StaffIdentityEvidence(actor.Id, "revoked@example.test", actor.EntraTenantId));
            Assert.AreEqual("operational_failure", denied.Code);
            await db.Entry(row).ReloadAsync();
            Assert.IsFalse(row.HasTrustedBib);
            Assert.AreEqual("staff_scope_forbidden", await db.QueueProgress.Where(item => item.QueueName == QueueNames.PurchasePromotion &&
                item.ScopeOrganizationId == scope).Select(item => item.LastOutcomeCode).SingleAsync());
            // The guard stopped the run after recording the denial. Finish its bounded scan, then start the next cycle.
            await VisitAsync();
            Assert.AreEqual(RequestStatus.OutstandingPurchase, row.Status);
            await VisitAsync();
            Assert.AreEqual(RequestStatus.Closed, row.Status);
            Assert.IsTrue(row.HasTrustedBib);
            Assert.IsFalse(row.BibIdStaffVerified);
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                // Dispatch reads the database predicate even for a writer outside EF.
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [asap].[TitleRequest] SET [Identifier] = [Identifier] + N' ' WHERE [Id] = {row.Id}");
                Assert.IsFalse(await db.TitleRequests.Where(item => item.Id == row.Id).Select(item => item.HasTrustedBib).SingleAsync());
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [asap].[TitleRequest] SET [Identifier] = {identifier}, [BibId] = 544 WHERE [Id] = {row.Id}");
                Assert.IsFalse(await db.TitleRequests.Where(item => item.Id == row.Id).Select(item => item.HasTrustedBib).SingleAsync());
                await transaction.RollbackAsync();
            }
            var reopened = await services.GetRequiredService<TitleRequestMutationService>().ActionAsync(actor, row.Id,
                new TitleRequestActionInput { Version = StaffVersion.Encode(row.RowVersion), Action = "reopen" }.ToCommand(), CancellationToken.None);
            Assert.AreEqual("updated", reopened.Code);
            await db.Entry(row).ReloadAsync();
            Assert.IsFalse(row.HasTrustedBib);
            Assert.IsNull(row.AutoVerifiedIdentifier);
        }
        finally
        {
            system.AutoPromote = before;
            db.WorkflowSettings.Remove(library);
            await db.SaveChangesAsync();
            await DeleteRequestAsync(seeded.Id);
            await DeleteFulfillmentLibraryAsync(scope);
        }
    }

    [TestMethod]
    [DataRow(false, false, "reopen")]
    [DataRow(false, false, "edit")]
    [DataRow(false, true, "edit")]
    [DataRow(true, false, "edit")]
    public async Task ClearingPromotionContextRetainsOnlyIndependentIdentifierEvidence(
        bool staffVerified, bool providerFailed, string action)
    {
        var actor = await GetOwnershipTestActorAsync();
        var scope = await CreateFulfillmentLibraryAsync();
        const string identifier = "9780306406157";
        const string earlierResult = "Only excluded e-materials matched the identifier.";
        var earlierCheck = timeProvider!.GetUtcNow().UtcDateTime.AddDays(-1);
        var seeded = await SeedBibOwnershipRequestAsync(identifier, 543, staffVerified, "not_found",
            checkResult: earlierResult, lastCheckedUtc: earlierCheck,
            status: RequestStatus.OutstandingPurchase, libraryOrganizationId: scope);
        var services = factory!.Services;
        var views = services.GetRequiredService<TitleRequestViewService>();
        var mutations = services.GetRequiredService<TitleRequestMutationService>();
        var provider = services.GetRequiredService<DeterministicTestingPatronProvider>();
        await using var db = await services.GetRequiredService<IDbContextFactory<AsapDbContext>>().CreateDbContextAsync();
        try
        {
            await ExecuteNonQueryAsync("""
                INSERT INTO [asap].[TitleRequestWorkflowTag] ([TitleRequestId], [WorkflowTagId])
                SELECT @id, Id FROM [asap].[WorkflowTag] WHERE Code IN (N'polaris_ematerials_only', N'duplicate_suggestion');
                """, ("@id", seeded.Id));
            db.WorkflowSettings.Add(new WorkflowSettings { OrganizationId = scope, AutoPromote = true });
            await db.SaveChangesAsync();
            provider.SetBib(543, scope, new(true, "Staff selection") { PrimaryMaterialTypeId = 36 });
            provider.SetPurchaseIdentifierResult(identifier, scope, new(providerFailed
                ? IdentifierLookupOutcome.OperationalFailure : IdentifierLookupOutcome.DefinitiveNotFound));
            Assert.AreEqual("completed", (await services.GetRequiredService<WorkflowProcessingService>().ProcessWorkflowAsync(scope)).Code);
            var row = await db.TitleRequests.SingleAsync(item => item.Id == seeded.Id);
            Assert.AreEqual(RequestStatus.OutstandingPurchase, row.Status);
            Assert.IsNotNull(row.LastPromoterCheckUtc);
            Assert.IsNotNull(row.LastPromoterResult);
            if (providerFailed)
            {
                Assert.IsNull(row.IsbnCheckStatus);
                Assert.IsNull(row.IsbnCheckResult);
                Assert.IsNull(row.LastCheckedUtc);
                StringAssert.Contains(row.LastPromoterResult, "check again");
            }
            else if (!staffVerified)
            {
                Assert.AreEqual("not_found", row.IsbnCheckStatus);
                Assert.AreEqual("No eligible Polaris BIB was found.", row.IsbnCheckResult);
                Assert.AreEqual(row.LastPromoterCheckUtc, row.LastCheckedUtc);
            }
            var details = await views.GetAsync(actor, seeded.Id.ToString(), CancellationToken.None);
            Assert.AreEqual(staffVerified ? "polaris_ematerials_only" : providerFailed ? null : "polaris_bib_not_found",
                details!.CatalogLookup?.Code);
            if (action == "reopen")
            {
                var close = new TitleRequestActionInput { Action = "closeDuplicate", Version = details.Version };
                Assert.AreEqual("updated", (await mutations.ActionAsync(actor, row.Id, close.ToCommand(), CancellationToken.None)).Code);
                await db.Entry(row).ReloadAsync();
            }
            var version = StaffVersion.Encode(row.RowVersion);
            var input = action == "edit"
                ? new TitleRequestActionInput { Action = action, Version = version, Bibid = 543, StaffSelectedBibId = 543 }
                : new TitleRequestActionInput { Action = action, Version = version };
            Assert.AreEqual("updated", (await mutations.ActionAsync(actor, row.Id, input.ToCommand(), CancellationToken.None)).Code);
            await db.Entry(row).ReloadAsync();
            Assert.IsNull(row.LastPromoterCheckUtc);
            Assert.IsNull(row.LastPromoterResult);
            Assert.AreEqual(staffVerified ? earlierCheck : null, row.LastCheckedUtc);
            Assert.AreEqual(staffVerified ? "not_found" : null, row.IsbnCheckStatus);
            Assert.AreEqual(staffVerified ? earlierResult : null, row.IsbnCheckResult);
            Assert.IsNull(row.IsbnCheckLastErrorCode);
            Assert.AreEqual(action == "edit", row.BibIdStaffVerified);
            var grid = (await views.ListAsync(actor, LibraryScope.ForLibrary(scope), CancellationToken.None))!.Items.Single();
            details = await views.GetAsync(actor, seeded.Id.ToString(), CancellationToken.None);
            string[] labels = staffVerified ? ["E-materials only", "Another patron requested this"] : ["Another patron requested this"];
            foreach (var dto in new[] { grid, details! })
            {
                Assert.AreEqual(row.IsbnCheckStatus, dto.IsbnCheckStatus);
                Assert.AreEqual(row.IsbnCheckResult, dto.IsbnCheckResult);
                Assert.AreEqual(staffVerified ? "polaris_ematerials_only" : null, dto.CatalogLookup?.Code);
                CollectionAssert.AreEquivalent(labels, dto.WorkflowTags.ToArray());
            }
            string[] persisted = staffVerified ? ["polaris_ematerials_only"] : [];
            CollectionAssert.AreEqual(persisted, (await ReadBibOwnershipRequestAsync(row.Id)).IdentifierTags.ToArray());
        }
        finally
        {
            await DeleteRequestAsync(seeded.Id);
            await db.WorkflowSettings.Where(item => item.OrganizationId == scope).ExecuteDeleteAsync();
            await DeleteFulfillmentLibraryAsync(scope);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task StaffEditsClearPurchaseVerification(bool changeIdentifier)
    {
        var actor = await GetOwnershipTestActorAsync();
        var scope = await CreateFulfillmentLibraryAsync();
        const string identifier = "001234567890";
        var seeded = await SeedBibOwnershipRequestAsync(identifier, null, false, "not_found", autoHold: true,
            status: RequestStatus.OutstandingPurchase, libraryOrganizationId: scope);
        var services = factory!.Services;
        var provider = services.GetRequiredService<DeterministicTestingPatronProvider>();
        await using var db = await services.GetRequiredService<IDbContextFactory<AsapDbContext>>().CreateDbContextAsync();
        try
        {
            db.WorkflowSettings.Add(new WorkflowSettings { OrganizationId = scope, AutoPromote = true });
            await db.SaveChangesAsync();
            provider.SetPurchaseIdentifierResult(identifier, scope, new(IdentifierLookupOutcome.Found, 543));
            provider.SetBib(544, scope, new(true, "Staff selection"));
            Assert.AreEqual("completed", (await services.GetRequiredService<WorkflowProcessingService>().ProcessWorkflowAsync(scope)).Code);
            var row = await db.TitleRequests.SingleAsync(item => item.Id == seeded.Id);
            Assert.AreEqual(RequestStatus.PendingHold, row.Status);
            Assert.IsTrue(row.HasTrustedBib);
            var version = StaffVersion.Encode(row.RowVersion);
            var edit = changeIdentifier ? new TitleRequestActionInput { Version = version, Action = "edit",
                Identifier = System.Text.Json.JsonSerializer.SerializeToElement("001234567891") }
                : new TitleRequestActionInput { Version = version, Action = "edit", Bibid = 544, StaffSelectedBibId = 544 };
            var result = await services.GetRequiredService<TitleRequestMutationService>().ActionAsync(actor, row.Id, edit.ToCommand(), CancellationToken.None);
            Assert.AreEqual("updated", result.Code);
            await db.Entry(row).ReloadAsync();
            Assert.IsNull(row.AutoVerifiedIdentifier);
            Assert.IsNull(row.AutoVerifiedBibId);
            Assert.AreEqual(!changeIdentifier, row.BibIdStaffVerified);
            Assert.AreEqual(!changeIdentifier, row.HasTrustedBib);
        }
        finally
        {
            await DeleteRequestAsync(seeded.Id);
            await db.WorkflowSettings.Where(item => item.OrganizationId == scope).ExecuteDeleteAsync();
            await DeleteFulfillmentLibraryAsync(scope);
        }
    }

    [TestMethod]
    [DataRow(1, null, true)]
    [DataRow(36, null, false)]
    [DataRow(41, null, false)]
    [DataRow(null, null, false)]
    [DataRow(1, "bks", false)]
    [DataRow(36, "bks", true)]
    [DataRow(41, "", true)]
    public async Task PurchaseDiscoveryValidatesOnlyStaffSelectedBib(int? material, string? exclusions, bool eligible)
    {
        await GetOwnershipTestActorAsync();
        var scope = await CreateFulfillmentLibraryAsync();
        var seeded = await SeedBibOwnershipRequestAsync("001234567890", 543, true, "not_found", checkResult: "Earlier identifier check.",
            status: RequestStatus.OutstandingPurchase, libraryOrganizationId: scope);
        var services = factory!.Services;
        await using var db = await services.GetRequiredService<IDbContextFactory<AsapDbContext>>().CreateDbContextAsync();
        var systemSettings = await db.WorkflowSettings.SingleAsync(item => item.OrganizationId == 1);
        var originalExclusions = systemSettings.ExcludedPromotionToms;
        try
        {
            systemSettings.ExcludedPromotionToms = exclusions;
            db.WorkflowSettings.Add(new WorkflowSettings { OrganizationId = scope, AutoPromote = true });
            await db.SaveChangesAsync();
            var provider = services.GetRequiredService<DeterministicTestingPatronProvider>();
            provider.SetBib(543, scope, new(true, "Catalog title") { PrimaryMaterialTypeId = material });
            Assert.AreEqual("completed", (await services.GetRequiredService<WorkflowProcessingService>().ProcessWorkflowAsync(scope)).Code);
            var row = await db.TitleRequests.SingleAsync(item => item.Id == seeded.Id);
            Assert.AreEqual(eligible ? RequestStatus.Closed : RequestStatus.OutstandingPurchase, row.Status);
            Assert.AreEqual("Staff entered title", row.Title);
            Assert.IsTrue(row.BibIdStaffVerified);
            Assert.AreEqual(543, row.BibId);
            Assert.AreEqual("not_found", row.IsbnCheckStatus);
            Assert.AreEqual("Earlier identifier check.", row.IsbnCheckResult);
            Assert.IsNull(row.AutoVerifiedIdentifier);
            Assert.IsFalse(provider.Calls.Any(call => call.Operation == TestingPolarisOperation.PurchaseIdentifierLookup));
        }
        finally
        {
            systemSettings.ExcludedPromotionToms = originalExclusions;
            await db.SaveChangesAsync();
            await DeleteRequestAsync(seeded.Id);
            await db.WorkflowSettings.Where(item => item.OrganizationId == scope).ExecuteDeleteAsync();
            await DeleteFulfillmentLibraryAsync(scope);
        }
    }
}
