using System.Net;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task PolarisPatronIdRejectsAContradictoryBarcodeAndAcceptsAFormerBarcodeAlias()
    {
        const string requestedBarcode = "20000000001991";
        var mismatchHandler = new ProtectedSequenceResponseHandler(
            (HttpStatusCode.OK,
             "{\"PAPIErrorCode\":0,\"PatronBasicData\":{\"PatronID\":810021," +
             "\"Barcode\":\"20000000001992\",\"PatronOrgID\":2}}"));
        var mismatchProvider = await CreatePolarisProviderAsync(mismatchHandler,
            "patron-id-mismatch-" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(() => mismatchProvider.GetPatronIdAsync(
            requestedBarcode,
            2,
            CancellationToken.None));

        var aliasHandler = new ProtectedSequenceResponseHandler(
            (HttpStatusCode.OK,
             "{\"PAPIErrorCode\":0,\"PatronBasicData\":{\"PatronID\":810022," +
             "\"Barcode\":\"20000000001993\",\"FormerID\":\"20000000001991\",\"PatronOrgID\":2}}"));
        var aliasProvider = await CreatePolarisProviderAsync(aliasHandler,
            "patron-id-former-alias-" + Guid.NewGuid().ToString("N"));

        Assert.AreEqual<int?>(810022, await aliasProvider.GetPatronIdAsync(
            requestedBarcode,
            2,
            CancellationToken.None));
        Assert.AreEqual(1, aliasHandler.RequestUris.Count(uri =>
            uri.AbsolutePath.EndsWith("/basicdata", StringComparison.Ordinal)));
        Assert.IsFalse(aliasHandler.RequestUris.Any(uri =>
            uri.AbsolutePath.Contains("/organizations/", StringComparison.OrdinalIgnoreCase)));

        var projectedHandler = new ProtectedSequenceResponseHandler(
            (HttpStatusCode.OK,
             "{\"PAPIErrorCode\":0,\"PatronBasicData\":{\"PatronID\":810023}}"));
        var projectedProvider = await CreatePolarisProviderAsync(projectedHandler,
            "patron-id-projection-" + Guid.NewGuid().ToString("N"));
        Assert.AreEqual<int?>(810023, await projectedProvider.GetPatronIdAsync(
            requestedBarcode,
            2,
            CancellationToken.None),
            "The identity-only PAPI projection remains valid when it supplies no barcode fields.");
    }

    [TestMethod]
    public async Task PolarisPinnedHttpDeserializerAcceptsCaseOnlyAlphanumericCurrentAndFormerAliasesForLookupAndRefresh()
    {
        await AssertPinnedCaseOnlyBarcodeAliasAsync("mEmBeRx123", "MemberX123", "FormerY456");
        await AssertPinnedCaseOnlyBarcodeAliasAsync("fOrMeRy456", "MemberX123", "FormerY456");
    }

    private async Task AssertPinnedCaseOnlyBarcodeAliasAsync(
        string requestedBarcode,
        string currentBarcode,
        string formerBarcode)
    {
        const int expectedPatronId = 810024;
        var basicData = $"{{\"PAPIErrorCode\":0,\"PatronBasicData\":{{\"PatronID\":{expectedPatronId}," +
                        $"\"Barcode\":\"{currentBarcode}\",\"FormerID\":\"{formerBarcode}\",\"PatronOrgID\":2}}}}";
        var lookupHandler = new ProtectedSequenceResponseHandler((HttpStatusCode.OK, basicData));
        var lookupProvider = await CreatePolarisProviderAsync(lookupHandler,
            "case-only-lookup-" + Guid.NewGuid().ToString("N"));

        Assert.AreEqual<int?>(expectedPatronId, await lookupProvider.GetPatronIdAsync(
            requestedBarcode,
            2,
            CancellationToken.None));
        Assert.AreEqual(1, lookupHandler.RequestUris.Count(uri =>
            uri.AbsolutePath.EndsWith("/basicdata", StringComparison.Ordinal)));

        var refreshHandler = new ProtectedSequenceResponseHandler(
            (HttpStatusCode.OK, basicData),
            (HttpStatusCode.OK,
             "{\"PAPIErrorCode\":1,\"OrganizationsGetRows\":[{\"OrganizationID\":2," +
             "\"OrganizationCodeID\":2,\"Name\":\"Alias Test Library\"}]}"));
        var refreshProvider = await CreatePolarisProviderAsync(refreshHandler,
            "case-only-refresh-" + Guid.NewGuid().ToString("N"));

        var patron = await refreshProvider.RefreshAsync(requestedBarcode, 2, CancellationToken.None);

        Assert.AreEqual(expectedPatronId, patron.PatronId);
        Assert.AreEqual(currentBarcode, patron.Barcode);
        Assert.AreEqual(formerBarcode, patron.FormerBarcode);
        Assert.AreEqual(1, refreshHandler.RequestUris.Count(uri =>
            uri.AbsolutePath.EndsWith("/basicdata", StringComparison.Ordinal)));
        Assert.AreEqual(1, refreshHandler.RequestUris.Count(uri =>
            uri.AbsolutePath.Contains("/organizations/", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task ResearchConfigurationCorrelatesLivePinnedPatronWithSavedAndLegacyNativeIdentity()
    {
        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        var seeded = await SeedStaffBibLookupStateAsync(Guid.Parse(identity.TenantId!));
        var originalPattern = await ReadResearchPatronPatternAsync();
        var barcode = await ReadStringAsync(
            "SELECT [Barcode] FROM [asap].[TitleRequest] WHERE [Id]=@id;",
            "@id",
            seeded.RequestId);
        await SetRequestNativePatronIdAsync(seeded.RequestId, 810031);
        var handler = new ProtectedSequenceResponseHandler(
            (HttpStatusCode.OK,
             $"{{\"PAPIErrorCode\":0,\"PatronBasicData\":{{\"PatronID\":810032,\"Barcode\":\"{barcode}\",\"PatronOrgID\":2}}}}"),
            (HttpStatusCode.OK,
             $"{{\"PAPIErrorCode\":0,\"PatronBasicData\":{{\"PatronID\":810031,\"Barcode\":\"{barcode}\",\"PatronOrgID\":2}}}}"),
            (HttpStatusCode.OK,
             $"{{\"PAPIErrorCode\":0,\"PatronBasicData\":{{\"PatronID\":810032,\"Barcode\":\"{barcode}\",\"PatronOrgID\":2}}}}"));
        var provider = await CreatePolarisProviderAsync(handler,
            "research-native-correlation-" + Guid.NewGuid().ToString("N"));
        await using var researchFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.AddSingleton<IPatronProvider>(provider);
            }));
        using var client = researchFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Id", seeded.StaffId.ToString());
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Tenant-Id", identity.TenantId);
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Email", seeded.Email);

        try
        {
            using var session = await client.GetAsync("/api/asap/staff/session");
            Assert.AreEqual(HttpStatusCode.OK, session.StatusCode, await session.Content.ReadAsStringAsync());
            await SetResearchPatronPatternAsync("https://leap.example.test/patron/{{patron-id}}");

            using (var mismatch = await client.GetAsync(
                       $"/api/asap/staff/research-configuration?requestId={seeded.RequestId}"))
            {
                Assert.AreEqual(HttpStatusCode.OK, mismatch.StatusCode, await mismatch.Content.ReadAsStringAsync());
                using var body = JsonDocument.Parse(await mismatch.Content.ReadAsStringAsync());
                Assert.AreEqual(JsonValueKind.Null, body.RootElement.GetProperty("patronId").ValueKind,
                    "A barcode now resolving to another native patron must not redirect this request's LEAP link.");
            }

            using (var match = await client.GetAsync(
                       $"/api/asap/staff/research-configuration?requestId={seeded.RequestId}"))
            {
                Assert.AreEqual(HttpStatusCode.OK, match.StatusCode, await match.Content.ReadAsStringAsync());
                using var body = JsonDocument.Parse(await match.Content.ReadAsStringAsync());
                Assert.AreEqual(810031, body.RootElement.GetProperty("patronId").GetInt32());
            }

            await SetRequestNativePatronIdAsync(seeded.RequestId, null);
            using (var legacy = await client.GetAsync(
                       $"/api/asap/staff/research-configuration?requestId={seeded.RequestId}"))
            {
                Assert.AreEqual(HttpStatusCode.OK, legacy.StatusCode, await legacy.Content.ReadAsStringAsync());
                using var body = JsonDocument.Parse(await legacy.Content.ReadAsStringAsync());
                Assert.AreEqual(810032, body.RootElement.GetProperty("patronId").GetInt32(),
                    "Legacy NULL requests may use the provider-verified current/former barcode lookup.");
            }

            Assert.AreEqual(3, handler.RequestUris.Count(uri =>
                uri.AbsolutePath.EndsWith("/basicdata", StringComparison.Ordinal)));
            Assert.IsFalse(handler.RequestUris.Any(uri =>
                uri.AbsolutePath.Contains("/organizations/", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            await SetResearchPatronPatternAsync(originalPattern);
        }
    }

    [TestMethod]
    public async Task PinnedCheckoutCannotFulfillWhenLivePatronDiffersFromSavedNativeIdentity()
    {
        await AssertPinnedFulfillmentNativeIdentityAsync(
            requestPatronId: 810001,
            operationPatronId: 810001,
            initialLivePatronId: 810002,
            finalLivePatronId: 810002,
            formerBarcodeAlias: null,
            expectedToClose: false);
    }

    [TestMethod]
    [DataRow(810013, 810014, false, 1, 0)]
    [DataRow(810015, 810015, true, 2, 1)]
    public async Task PinnedCheckoutWithoutTrackedOperationUsesSavedNativeIdentity(
        int savedPatronId,
        int livePatronId,
        bool expectedToClose,
        int expectedBasicDataReads,
        int expectedCheckoutReads)
    {
        var scope = await CreateFulfillmentLibraryAsync();
        var barcode = CreateCorrelationBarcode();
        const int bibId = 99214;
        var handler = new FulfillmentNativeCorrelationPapiHandler(barcode, livePatronId, bibId);
        var provider = await CreatePolarisProviderAsync(handler,
            "fulfillment-native-no-operation-" + Guid.NewGuid().ToString("N"));
        await using var workflowFactory = CreatePinnedFulfillmentFactory(provider);
        var seeded = await SeedCompletedHoldIdentityAsync(
            "fulfillment-native-no-operation",
            barcode,
            bibId,
            holdRequestId: 99214,
            organizationId: scope);
        await SetRequestNativePatronIdAsync(seeded.RequestId, savedPatronId);
        await ExecuteNonQueryAsync(
            "DELETE FROM [asap].[HoldPlacementOperation] WHERE [Id]=@operationId;",
            ("@operationId", seeded.OperationId));
        await PrepareSingleItemCycleAsync(QueueNames.FulfillmentTracking, scope, seeded.RequestId);
        var requestVersionBefore = await ReadRequestVersionAsync(seeded.RequestId);
        var counts = await ReadFulfillmentNoMutationCountsAsync(seeded.RequestId);

        try
        {
            var result = await workflowFactory.Services.GetRequiredService<WorkflowProcessingService>()
                .ProcessWorkflowAsync(scope, CancellationToken.None);

            Assert.AreEqual(expectedToClose ? "completed" : "operational_failure", result.Code);
            Assert.AreEqual(expectedBasicDataReads, handler.BasicDataCalls);
            Assert.AreEqual(expectedCheckoutReads, handler.CheckoutCalls);
            await using var verify = await workflowFactory.Services
                .GetRequiredService<IDbContextFactory<AsapDbContext>>().CreateDbContextAsync();
            var request = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == seeded.RequestId);
            Assert.AreEqual(savedPatronId, request.PatronIdSnapshot);
            Assert.IsFalse(await verify.HoldPlacementOperations.AnyAsync(item => item.TitleRequestId == seeded.RequestId));
            if (expectedToClose)
            {
                Assert.AreEqual(RequestStatus.Closed, request.Status);
                Assert.AreEqual("hold_completed", request.CloseReason);
                Assert.AreEqual(counts.EventCount + 1, await verify.TitleRequestEvents.CountAsync(item =>
                    item.TitleRequestId == seeded.RequestId));
            }
            else
            {
                Assert.AreEqual(RequestStatus.HoldPlaced, request.Status);
                Assert.IsNull(request.CloseReason);
                CollectionAssert.AreEqual(requestVersionBefore, request.RowVersion);
                Assert.AreEqual(counts.EventCount, await verify.TitleRequestEvents.CountAsync(item =>
                    item.TitleRequestId == seeded.RequestId));
            }
            Assert.AreEqual(counts.OutboxCount, await verify.EmailOutbox.CountAsync());
        }
        finally
        {
            await DeleteRequestAsync(seeded.RequestId);
            await DeleteFulfillmentLibraryAsync(scope);
        }
    }

    [TestMethod]
    public async Task PinnedCheckoutCannotFulfillWhenOnlyTrackedOperationNativeIdentityDiffers()
    {
        await AssertPinnedFulfillmentNativeIdentityAsync(
            requestPatronId: null,
            operationPatronId: 810003,
            initialLivePatronId: 810004,
            finalLivePatronId: 810004,
            formerBarcodeAlias: null,
            expectedToClose: false);
    }

    [TestMethod]
    public async Task PinnedCheckoutDoesNotFulfillWhenPersistedNativeSnapshotsContradict()
    {
        await AssertPinnedFulfillmentNativeIdentityAsync(
            requestPatronId: 810005,
            operationPatronId: 810006,
            initialLivePatronId: 810005,
            finalLivePatronId: 810005,
            formerBarcodeAlias: null,
            expectedToClose: false);
    }

    [TestMethod]
    public async Task PinnedCheckoutClosesForVerifiedCurrentPatronThroughFormerBarcodeAlias()
    {
        await AssertPinnedFulfillmentNativeIdentityAsync(
            requestPatronId: 810007,
            operationPatronId: 810007,
            initialLivePatronId: 810007,
            finalLivePatronId: 810007,
            formerBarcodeAlias: true,
            expectedToClose: true);
    }

    [TestMethod]
    public async Task PinnedCheckoutPreservesVerifiedLegacyNullIdentityWithoutGuessingSnapshot()
    {
        await AssertPinnedFulfillmentNativeIdentityAsync(
            requestPatronId: null,
            operationPatronId: null,
            initialLivePatronId: 810008,
            finalLivePatronId: 810008,
            formerBarcodeAlias: null,
            expectedToClose: true);
    }

    [TestMethod]
    public async Task PinnedCheckoutRechecksNativeIdentityAfterEvidenceRead()
    {
        var scope = await CreateFulfillmentLibraryAsync();
        var barcode = CreateCorrelationBarcode();
        const int bibId = 99211;
        var handler = new FulfillmentNativeCorrelationPapiHandler(barcode, 810009, bibId)
        {
            BlockCheckoutRead = true
        };
        var provider = await CreatePolarisProviderAsync(handler,
            "fulfillment-native-race-" + Guid.NewGuid().ToString("N"));
        await using var workflowFactory = CreatePinnedFulfillmentFactory(provider);
        var seeded = await SeedCompletedHoldIdentityAsync(
            "fulfillment-native-reassignment-during-checkout",
            barcode,
            bibId,
            holdRequestId: 99211,
            organizationId: scope);
        await SetFulfillmentNativeSnapshotsAsync(seeded.RequestId, seeded.OperationId, 810009, 810009);
        await PrepareSingleItemCycleAsync(QueueNames.FulfillmentTracking, scope, seeded.RequestId);
        var versions = await ReadFulfillmentVersionsAsync(seeded.RequestId, seeded.OperationId);
        var counts = await ReadFulfillmentNoMutationCountsAsync(seeded.RequestId);

        try
        {
            var execution = workflowFactory.Services.GetRequiredService<WorkflowProcessingService>()
                .ProcessWorkflowAsync(scope, CancellationToken.None);
            await handler.CheckoutReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            handler.LivePatronId = 810010;
            handler.CompleteCheckoutRead();

            var result = await execution.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.AreEqual("operational_failure", result.Code);
            Assert.AreEqual(2, handler.BasicDataCalls,
                "The owner must be refreshed again after the checkout evidence read.");
            await using var verify = await workflowFactory.Services
                .GetRequiredService<IDbContextFactory<AsapDbContext>>().CreateDbContextAsync();
            await AssertNoFulfillmentMutationAsync(verify,
                seeded.RequestId,
                seeded.OperationId,
                status: RequestStatus.HoldPlaced,
                closeReason: null,
                requestVersion: versions.RequestVersion,
                operationVersion: versions.OperationVersion,
                eventCount: counts.EventCount,
                outboxCount: counts.OutboxCount);
        }
        finally
        {
            handler.CompleteCheckoutRead();
            await DeleteRequestAsync(seeded.RequestId);
            await DeleteFulfillmentLibraryAsync(scope);
        }
    }

    [TestMethod]
    public async Task PinnedHoldEvidenceRechecksNativeIdentityAfterRead()
    {
        var scope = await CreateFulfillmentLibraryAsync();
        var barcode = CreateCorrelationBarcode();
        const int bibId = 99212;
        const int holdId = 99213;
        var handler = new FulfillmentNativeCorrelationPapiHandler(barcode, 810011, bibId)
        {
            CheckoutContent = "{\"PAPIErrorCode\":0,\"PatronItemsOutGetRows\":[]}",
            HoldContent = "{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{" +
                "\"HoldRequestID\":99213,\"BibID\":99212,\"StatusID\":8,\"StatusDescription\":\"Expired\"}]}",
            BlockHoldRead = true
        };
        var provider = await CreatePolarisProviderAsync(handler,
            "fulfillment-native-hold-race-" + Guid.NewGuid().ToString("N"));
        await using var workflowFactory = CreatePinnedFulfillmentFactory(provider);
        var seeded = await SeedCompletedHoldIdentityAsync(
            "fulfillment-native-reassignment-during-hold-read",
            barcode,
            bibId,
            holdRequestId: holdId,
            organizationId: scope);
        await SetFulfillmentNativeSnapshotsAsync(seeded.RequestId, seeded.OperationId, 810011, 810011);
        await PrepareSingleItemCycleAsync(QueueNames.FulfillmentTracking, scope, seeded.RequestId);
        var versions = await ReadFulfillmentVersionsAsync(seeded.RequestId, seeded.OperationId);
        var counts = await ReadFulfillmentNoMutationCountsAsync(seeded.RequestId);

        try
        {
            var execution = workflowFactory.Services.GetRequiredService<WorkflowProcessingService>()
                .ProcessWorkflowAsync(scope, CancellationToken.None);
            await handler.HoldReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            handler.LivePatronId = 810012;
            handler.CompleteHoldRead();

            var result = await execution.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.AreEqual("operational_failure", result.Code);
            Assert.AreEqual(2, handler.BasicDataCalls,
                "The owner must be refreshed again after the terminal hold evidence read.");
            await using var verify = await workflowFactory.Services
                .GetRequiredService<IDbContextFactory<AsapDbContext>>().CreateDbContextAsync();
            await AssertNoFulfillmentMutationAsync(
                verify,
                seeded.RequestId,
                seeded.OperationId,
                RequestStatus.HoldPlaced,
                closeReason: null,
                requestVersion: versions.RequestVersion,
                operationVersion: versions.OperationVersion,
                eventCount: counts.EventCount,
                outboxCount: counts.OutboxCount);
        }
        finally
        {
            handler.CompleteHoldRead();
            await DeleteRequestAsync(seeded.RequestId);
            await DeleteFulfillmentLibraryAsync(scope);
        }
    }

    [TestMethod]
    public Task PinnedCheckoutFinalSqlLockRejectsConcurrentRequestNativeIdentityChange() =>
        AssertFinalSqlLockRejectsIdentityMutationAsync(changeRequestSnapshot: true);

    [TestMethod]
    public Task PinnedCheckoutFinalSqlLockRejectsChangedLatestSucceededOperationIdentity() =>
        AssertFinalSqlLockRejectsIdentityMutationAsync(changeRequestSnapshot: false);

    private async Task AssertFinalSqlLockRejectsIdentityMutationAsync(bool changeRequestSnapshot)
    {
        var scope = await CreateFulfillmentLibraryAsync();
        const string barcode = "native-final-lock-race-1001";
        const int bibId = 99215;
        const int savedPatronId = 810016;
        const int changedPatronId = 810017;
        var seeded = await SeedCompletedHoldIdentityAsync(
            "fulfillment-final-lock-" + (changeRequestSnapshot ? "request" : "operation"),
            barcode,
            bibId,
            holdRequestId: 99215,
            organizationId: scope);
        await SetFulfillmentNativeSnapshotsAsync(seeded.RequestId, seeded.OperationId, savedPatronId, savedPatronId);
        await PrepareSingleItemCycleAsync(QueueNames.FulfillmentTracking, scope, seeded.RequestId);
        var versionsBefore = await ReadFulfillmentVersionsAsync(seeded.RequestId, seeded.OperationId);
        var countsBefore = await ReadFulfillmentNoMutationCountsAsync(seeded.RequestId);
        byte[]? requestVersionAfterConcurrentChange = null;
        byte[]? operationVersionAfterConcurrentChange = null;
        var handler = new FulfillmentNativeCorrelationPapiHandler(barcode, savedPatronId, bibId)
        {
            BeforeSecondBasicDataResponse = async cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (changeRequestSnapshot)
                {
                    await ExecuteNonQueryAsync(
                        "UPDATE [asap].[TitleRequest] SET [PatronIdSnapshot]=@patronId WHERE [Id]=@requestId;",
                        ("@requestId", seeded.RequestId),
                        ("@patronId", changedPatronId));
                    requestVersionAfterConcurrentChange = await ReadRequestVersionAsync(seeded.RequestId);
                }
                else
                {
                    await ExecuteNonQueryAsync(
                        "UPDATE [asap].[HoldPlacementOperation] SET [PatronIdSnapshot]=@patronId WHERE [Id]=@operationId;",
                        ("@operationId", seeded.OperationId),
                        ("@patronId", changedPatronId));
                    var versions = await ReadFulfillmentVersionsAsync(seeded.RequestId, seeded.OperationId);
                    operationVersionAfterConcurrentChange = versions.OperationVersion;
                }
            }
        };
        var provider = await CreatePolarisProviderAsync(handler,
            "fulfillment-final-sql-lock-" + Guid.NewGuid().ToString("N"));
        await using var workflowFactory = CreatePinnedFulfillmentFactory(provider);

        try
        {
            var result = await workflowFactory.Services.GetRequiredService<WorkflowProcessingService>()
                .ProcessWorkflowAsync(scope, CancellationToken.None);

            Assert.AreEqual("completed", result.Code);
            Assert.AreEqual(2, handler.BasicDataCalls);
            Assert.AreEqual(1, handler.CheckoutCalls);
            Assert.AreEqual(0, handler.PutCalls);
            await using var verify = await workflowFactory.Services
                .GetRequiredService<IDbContextFactory<AsapDbContext>>().CreateDbContextAsync();
            var request = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == seeded.RequestId);
            var operation = await verify.HoldPlacementOperations.AsNoTracking()
                .SingleAsync(item => item.Id == seeded.OperationId);
            Assert.AreEqual(RequestStatus.HoldPlaced, request.Status);
            Assert.IsNull(request.CloseReason);
            Assert.AreEqual(HoldOperationState.Succeeded, operation.State);
            Assert.AreEqual(countsBefore.EventCount, await verify.TitleRequestEvents.CountAsync(item =>
                item.TitleRequestId == seeded.RequestId));
            Assert.AreEqual(countsBefore.OutboxCount, await verify.EmailOutbox.CountAsync());

            var countsAfter = await ReadFulfillmentNoMutationCountsAsync(seeded.RequestId);
            Assert.AreEqual(countsBefore.HoldIntentCount, countsAfter.HoldIntentCount);
            Assert.AreEqual(countsBefore.PickupIntentCount, countsAfter.PickupIntentCount);
            if (changeRequestSnapshot)
            {
                Assert.AreEqual(changedPatronId, request.PatronIdSnapshot);
                Assert.IsNotNull(requestVersionAfterConcurrentChange);
                CollectionAssert.AreEqual(requestVersionAfterConcurrentChange, request.RowVersion);
                Assert.AreEqual(savedPatronId, operation.PatronIdSnapshot);
                CollectionAssert.AreEqual(versionsBefore.OperationVersion, operation.RowVersion);
            }
            else
            {
                Assert.AreEqual(savedPatronId, request.PatronIdSnapshot);
                CollectionAssert.AreEqual(versionsBefore.RequestVersion, request.RowVersion);
                Assert.AreEqual(changedPatronId, operation.PatronIdSnapshot);
                Assert.IsNotNull(operationVersionAfterConcurrentChange);
                CollectionAssert.AreEqual(operationVersionAfterConcurrentChange, operation.RowVersion);
            }
        }
        finally
        {
            await DeleteRequestAsync(seeded.RequestId);
            await DeleteFulfillmentLibraryAsync(scope);
        }
    }

    [TestMethod]
    public async Task ManualFulfillmentNativeMismatchAfterRevocationRecordsOnlyWorkerProgress()
    {
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var created = await CreateCorrectiveStaffAsync(superAdmin, StaffRole.SuperAdmin, LibraryScope.SystemOrganizationId);
        var actor = await ReadCorrectiveStaffAsync(created);
        var scope = await CreateFulfillmentLibraryAsync();
        const string barcode = "native-manual-race-1002";
        const int bibId = 99216;
        const int savedPatronId = 810018;
        var handler = new FulfillmentNativeCorrelationPapiHandler(barcode, savedPatronId, bibId)
        {
            BlockCheckoutRead = true
        };
        var provider = await CreatePolarisProviderAsync(handler,
            "manual-fulfillment-native-race-" + Guid.NewGuid().ToString("N"));
        await using var workflowFactory = CreatePinnedFulfillmentFactory(provider);
        var contexts = workflowFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var seeded = await SeedCompletedHoldIdentityAsync(
            "manual-fulfillment-native-race",
            barcode,
            bibId,
            holdRequestId: 99216,
            organizationId: scope);
        await SetFulfillmentNativeSnapshotsAsync(seeded.RequestId, seeded.OperationId, savedPatronId, savedPatronId);

        try
        {
            await PrepareSingleItemCycleAsync(QueueNames.FulfillmentTracking, scope, seeded.RequestId);
            var versionsBefore = await ReadFulfillmentVersionsAsync(seeded.RequestId, seeded.OperationId);
            var countsBefore = await ReadFulfillmentNoMutationCountsAsync(seeded.RequestId);
            var execution = workflowFactory.Services.GetRequiredService<BackgroundWorkflowJobs>()
                .ProcessManualWorkflowAsync(
                    new StaffJobEvidence(actor.Id, actor.AuthenticationEmail, actor.EntraTenantId),
                    scope,
                    CancellationToken.None);
            await handler.CheckoutReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));

            await ExecuteNonQueryAsync("UPDATE [asap].[StaffUser] SET [IsActive]=0 WHERE [Id]=@id;", ("@id", actor.Id));
            handler.LivePatronId = savedPatronId + 1;
            handler.CompleteCheckoutRead();
            var result = await execution.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.AreEqual("operational_failure", result.Code);
            Assert.AreEqual(2, handler.BasicDataCalls);
            Assert.AreEqual(1, handler.CheckoutCalls);
            Assert.AreEqual(0, handler.HoldCalls);
            Assert.AreEqual(0, handler.PutCalls);
            await using var verify = await contexts.CreateDbContextAsync();
            var request = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == seeded.RequestId);
            var operation = await verify.HoldPlacementOperations.AsNoTracking()
                .SingleAsync(item => item.Id == seeded.OperationId);
            var progress = await verify.QueueProgress.AsNoTracking().SingleAsync(item =>
                item.QueueName == QueueNames.FulfillmentTracking && item.ScopeOrganizationId == scope);
            Assert.AreEqual(RequestStatus.HoldPlaced, request.Status);
            Assert.IsNull(request.CloseReason);
            Assert.AreEqual(savedPatronId, request.PatronIdSnapshot);
            CollectionAssert.AreEqual(versionsBefore.RequestVersion, request.RowVersion);
            Assert.AreEqual(HoldOperationState.Succeeded, operation.State);
            Assert.AreEqual(savedPatronId, operation.PatronIdSnapshot);
            Assert.IsNull(operation.LastErrorCode);
            CollectionAssert.AreEqual(versionsBefore.OperationVersion, operation.RowVersion);
            Assert.AreEqual("native_patron_identity_mismatch", progress.LastOutcomeCode);
            Assert.AreEqual(seeded.RequestId, progress.LastOutcomeItemId);
            Assert.AreEqual(countsBefore.EventCount, await verify.TitleRequestEvents.CountAsync(item =>
                item.TitleRequestId == seeded.RequestId));
            Assert.AreEqual(countsBefore.OutboxCount, await verify.EmailOutbox.CountAsync());
            var countsAfter = await ReadFulfillmentNoMutationCountsAsync(seeded.RequestId);
            Assert.AreEqual(countsBefore.HoldIntentCount, countsAfter.HoldIntentCount);
            Assert.AreEqual(countsBefore.PickupIntentCount, countsAfter.PickupIntentCount);
        }
        finally
        {
            handler.CompleteCheckoutRead();
            await DeleteRequestAsync(seeded.RequestId);
            await using var cleanup = await contexts.CreateDbContextAsync();
            await cleanup.StaffUsers.Where(item => item.Id == actor.Id).ExecuteDeleteAsync();
            await DeleteFulfillmentLibraryAsync(scope);
        }
    }

    private async Task AssertPinnedFulfillmentNativeIdentityAsync(
        int? requestPatronId,
        int? operationPatronId,
        int initialLivePatronId,
        int finalLivePatronId,
        bool? formerBarcodeAlias,
        bool expectedToClose)
    {
        var scope = await CreateFulfillmentLibraryAsync();
        var barcode = CreateCorrelationBarcode();
        var currentBarcode = formerBarcodeAlias is true ? CreateCorrelationBarcode() : barcode;
        const int bibId = 99201;
        var handler = new FulfillmentNativeCorrelationPapiHandler(
            barcode,
            initialLivePatronId,
            bibId,
            currentBarcode,
            formerBarcodeAlias is true ? barcode : null)
        {
            LivePatronIdAfterFirstBasicData = finalLivePatronId
        };
        var provider = await CreatePolarisProviderAsync(handler,
            "fulfillment-native-" + Guid.NewGuid().ToString("N"));
        await using var workflowFactory = CreatePinnedFulfillmentFactory(provider);
        var seeded = await SeedCompletedHoldIdentityAsync(
            "fulfillment-native-correlation",
            barcode,
            bibId,
            holdRequestId: 99201,
            organizationId: scope);
        await SetFulfillmentNativeSnapshotsAsync(
            seeded.RequestId,
            seeded.OperationId,
            requestPatronId,
            operationPatronId);
        await PrepareSingleItemCycleAsync(QueueNames.FulfillmentTracking, scope, seeded.RequestId);

        try
        {
            int eventCountBefore;
            int outboxCountBefore;
            byte[] requestVersionBefore;
            byte[] operationVersionBefore;
            await using (var before = await workflowFactory.Services
                             .GetRequiredService<IDbContextFactory<AsapDbContext>>().CreateDbContextAsync())
            {
                eventCountBefore = await before.TitleRequestEvents.CountAsync(item =>
                    item.TitleRequestId == seeded.RequestId);
                outboxCountBefore = await before.EmailOutbox.CountAsync();
                requestVersionBefore = await before.TitleRequests.AsNoTracking()
                    .Where(item => item.Id == seeded.RequestId).Select(item => item.RowVersion).SingleAsync();
                operationVersionBefore = await before.HoldPlacementOperations.AsNoTracking()
                    .Where(item => item.Id == seeded.OperationId).Select(item => item.RowVersion).SingleAsync();
            }

            var result = await workflowFactory.Services.GetRequiredService<WorkflowProcessingService>()
                .ProcessWorkflowAsync(scope, CancellationToken.None);

            Assert.AreEqual(expectedToClose ? "completed" : "operational_failure", result.Code);
            var persistedIdentitiesContradict = requestPatronId.HasValue && operationPatronId.HasValue &&
                requestPatronId != operationPatronId;
            var knownIdentityMismatch = requestPatronId.HasValue && requestPatronId != initialLivePatronId ||
                operationPatronId.HasValue && operationPatronId != initialLivePatronId;
            var expectedBasicDataReads = persistedIdentitiesContradict ? 0 : knownIdentityMismatch ? 1 : 2;
            var expectedCheckoutReads = persistedIdentitiesContradict || knownIdentityMismatch ? 0 : 1;
            Assert.AreEqual(expectedCheckoutReads, handler.CheckoutCalls,
                "The real pinned PAPI checkout serializer must supply evidence only after the saved owner is verified.");
            Assert.AreEqual(expectedBasicDataReads, handler.BasicDataCalls,
                "Fulfillment must perform pinned basic-data identity reads before accepting checkout evidence.");
            await using var verify = await workflowFactory.Services
                .GetRequiredService<IDbContextFactory<AsapDbContext>>().CreateDbContextAsync();
            var request = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == seeded.RequestId);
            var operation = await verify.HoldPlacementOperations.AsNoTracking()
                .SingleAsync(item => item.Id == seeded.OperationId);
            if (expectedToClose)
            {
                Assert.AreEqual(RequestStatus.Closed, request.Status);
                Assert.AreEqual("hold_completed", request.CloseReason);
                Assert.AreEqual(requestPatronId, request.PatronIdSnapshot,
                    "A verified legacy lookup must not invent a persisted patron identity.");
                Assert.AreEqual(operationPatronId, operation.PatronIdSnapshot);
                Assert.AreEqual(eventCountBefore + 1, await verify.TitleRequestEvents.CountAsync(item =>
                    item.TitleRequestId == seeded.RequestId));
            }
            else
            {
                Assert.AreEqual(RequestStatus.HoldPlaced, request.Status);
                Assert.IsNull(request.CloseReason);
                CollectionAssert.AreEqual(requestVersionBefore, request.RowVersion,
                    "A mismatched native identity must not change the request rowversion.");
                CollectionAssert.AreEqual(operationVersionBefore, operation.RowVersion,
                    "A mismatched native identity must not write a diagnostic onto the hold journal.");
                Assert.AreEqual(eventCountBefore, await verify.TitleRequestEvents.CountAsync(item =>
                    item.TitleRequestId == seeded.RequestId));
                Assert.AreEqual(outboxCountBefore, await verify.EmailOutbox.CountAsync());
            }
            Assert.AreEqual(outboxCountBefore, await verify.EmailOutbox.CountAsync());
        }
        finally
        {
            await DeleteRequestAsync(seeded.RequestId);
            await DeleteFulfillmentLibraryAsync(scope);
        }
    }

    private WebApplicationFactory<Program> CreatePinnedFulfillmentFactory(PolarisPatronProvider provider) =>
        factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton<IPatronProvider>(provider);
            services.AddSingleton<IStaffPolarisProvider>(provider);
        }));

    private static Task SetFulfillmentNativeSnapshotsAsync(
        long requestId,
        long operationId,
        int? requestPatronId,
        int? operationPatronId) => ExecuteNonQueryAsync(
        """
        UPDATE [asap].[TitleRequest]
        SET [PatronIdSnapshot] = @requestPatronId
        WHERE [Id] = @requestId;
        UPDATE [asap].[HoldPlacementOperation]
        SET [PatronIdSnapshot] = @operationPatronId
        WHERE [Id] = @operationId;
        """,
        ("@requestId", requestId),
        ("@operationId", operationId),
        ("@requestPatronId", (object?)requestPatronId ?? DBNull.Value),
        ("@operationPatronId", (object?)operationPatronId ?? DBNull.Value));

    private static Task SetRequestNativePatronIdAsync(long requestId, int? patronId) => ExecuteNonQueryAsync(
        "UPDATE [asap].[TitleRequest] SET [PatronIdSnapshot]=@patronId WHERE [Id]=@requestId;",
        ("@requestId", requestId),
        ("@patronId", (object?)patronId ?? DBNull.Value));

    private async Task<(byte[] RequestVersion, byte[] OperationVersion)> ReadFulfillmentVersionsAsync(
        long requestId,
        long operationId)
    {
        await using var context = await factory!.Services
            .GetRequiredService<IDbContextFactory<AsapDbContext>>().CreateDbContextAsync();
        var requestVersion = await context.TitleRequests.AsNoTracking()
            .Where(item => item.Id == requestId).Select(item => item.RowVersion).SingleAsync();
        var operationVersion = await context.HoldPlacementOperations.AsNoTracking()
            .Where(item => item.Id == operationId).Select(item => item.RowVersion).SingleAsync();
        return (requestVersion, operationVersion);
    }

    private async Task<byte[]> ReadRequestVersionAsync(long requestId)
    {
        await using var context = await factory!.Services
            .GetRequiredService<IDbContextFactory<AsapDbContext>>().CreateDbContextAsync();
        return await context.TitleRequests.AsNoTracking()
            .Where(item => item.Id == requestId).Select(item => item.RowVersion).SingleAsync();
    }

    private async Task<(int EventCount, int OutboxCount, int HoldIntentCount, int PickupIntentCount)> ReadFulfillmentNoMutationCountsAsync(long requestId)
    {
        await using var context = await factory!.Services
            .GetRequiredService<IDbContextFactory<AsapDbContext>>().CreateDbContextAsync();
        var eventCount = await context.TitleRequestEvents.CountAsync(item => item.TitleRequestId == requestId);
        var outboxCount = await context.EmailOutbox.CountAsync();
        var holdIntentCount = await context.HoldPlacementOperations.CountAsync(item => item.TitleRequestId == requestId);
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM [asap].[PickupPreferenceOperation] WHERE [TitleRequestId] = @requestId;";
        command.Parameters.AddWithValue("@requestId", requestId);
        var pickupIntentCount = Convert.ToInt32(await command.ExecuteScalarAsync());
        return (eventCount, outboxCount, holdIntentCount, pickupIntentCount);
    }

    private static string CreateCorrelationBarcode() =>
        "20" + Random.Shared.Next(10_000_000, 99_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static async Task AssertNoFulfillmentMutationAsync(
        AsapDbContext context,
        long requestId,
        long operationId,
        string status,
        string? closeReason,
        byte[] requestVersion,
        byte[] operationVersion,
        int eventCount,
        int outboxCount)
    {
        var request = await context.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == requestId);
        var operation = await context.HoldPlacementOperations.AsNoTracking()
            .SingleAsync(item => item.Id == operationId);
        Assert.AreEqual(status, request.Status);
        Assert.AreEqual(closeReason, request.CloseReason);
        CollectionAssert.AreEqual(requestVersion, request.RowVersion);
        CollectionAssert.AreEqual(operationVersion, operation.RowVersion);
        Assert.AreEqual(eventCount, await context.TitleRequestEvents.CountAsync(item =>
            item.TitleRequestId == requestId));
        Assert.AreEqual(outboxCount, await context.EmailOutbox.CountAsync());
    }

    private sealed class FulfillmentNativeCorrelationPapiHandler(
        string requestedBarcode,
        int livePatronId,
        int bibId,
        string? currentBarcode = null,
        string? formerBarcode = null) : HttpMessageHandler
    {
        private readonly string barcode = currentBarcode ?? requestedBarcode;
        private readonly TaskCompletionSource checkoutReadRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource holdReadRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int LivePatronId { get; set; } = livePatronId;
        public int? LivePatronIdAfterFirstBasicData { get; set; }
        public int BasicDataCalls { get; private set; }
        public int CheckoutCalls { get; private set; }
        public int HoldCalls { get; private set; }
        public int PutCalls { get; private set; }
        public bool BlockCheckoutRead { get; init; }
        public bool BlockHoldRead { get; init; }
        public Func<CancellationToken, Task>? BeforeSecondBasicDataResponse { get; init; }
        public string CheckoutContent { get; init; } =
            $"{{\"PAPIErrorCode\":0,\"PatronItemsOutGetRows\":[{{\"BibID\":{bibId}}}]}}";
        public string HoldContent { get; init; } = "{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[]}";
        public TaskCompletionSource CheckoutReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HoldReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void CompleteCheckoutRead() => checkoutReadRelease.TrySetResult();
        public void CompleteHoldRead() => holdReadRelease.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Method == HttpMethod.Put)
            {
                PutCalls++;
            }
            var path = request.RequestUri!.AbsolutePath;
            string content;
            if (path.Contains("/authenticator/staff", StringComparison.Ordinal))
            {
                content = "{\"PAPIErrorCode\":0,\"AccessToken\":\"correlation-token\",\"AccessSecret\":\"correlation-secret\",\"AuthExpDate\":\"2030-01-01T00:00:00Z\"}";
            }
            else if (path.EndsWith("/basicdata", StringComparison.Ordinal))
            {
                BasicDataCalls++;
                if (BasicDataCalls == 2 && BeforeSecondBasicDataResponse is { } beforeSecondBasicDataResponse)
                {
                    await beforeSecondBasicDataResponse(cancellationToken);
                }
                var patronId = LivePatronIdAfterFirstBasicData is { } changedId && BasicDataCalls > 1
                    ? changedId
                    : LivePatronId;
                content = "{\"PAPIErrorCode\":0,\"PatronBasicData\":{" +
                          $"\"PatronID\":{patronId},\"Barcode\":\"{barcode}\",\"PatronOrgID\":3494,\"PatronCodeID\":1" +
                          (formerBarcode is null ? string.Empty : $",\"FormerID\":\"{formerBarcode}\"") + "}}";
            }
            else if (path.Contains("/organizations/", StringComparison.Ordinal))
            {
                content = "{\"PAPIErrorCode\":0,\"OrganizationsGetRows\":[" +
                          "{\"OrganizationID\":1,\"OrganizationCodeID\":1,\"ParentOrganizationID\":null}," +
                          "{\"OrganizationID\":3494,\"OrganizationCodeID\":2,\"ParentOrganizationID\":1,\"Name\":\"Fulfillment Library\"}]}";
            }
            else if (path.Contains("/itemsout/", StringComparison.Ordinal))
            {
                CheckoutCalls++;
                if (BlockCheckoutRead)
                {
                    CheckoutReadStarted.TrySetResult();
                    await checkoutReadRelease.Task.WaitAsync(cancellationToken);
                }
                content = CheckoutContent;
            }
            else if (path.Contains("/holdrequests/", StringComparison.Ordinal))
            {
                HoldCalls++;
                if (BlockHoldRead)
                {
                    HoldReadStarted.TrySetResult();
                    await holdReadRelease.Task.WaitAsync(cancellationToken);
                }
                content = HoldContent;
            }
            else
            {
                throw new AssertFailedException("Unexpected pinned Polaris native-correlation request: " + path);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content),
                RequestMessage = request
            };
        }
    }
}
