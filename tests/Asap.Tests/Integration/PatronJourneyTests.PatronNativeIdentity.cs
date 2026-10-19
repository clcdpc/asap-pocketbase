using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Asap.Web.Infrastructure.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task PinnedPolarisUsernameLoginPersistsNativeIdentityAndReturnedBarcode()
    {
        const string username = "credential-name-not-a-barcode";
        const string currentBarcode = "20000000009991";
        const int nativePatronId = 739991;
        var handler = new NativePatronIdentityHandler(
            nativePatronId,
            currentBarcode,
            organizations: "{\"PAPIErrorCode\":0,\"OrganizationsGetRows\":[" +
                "{\"OrganizationID\":1,\"OrganizationCodeID\":1,\"ParentOrganizationID\":null}," +
                "{\"OrganizationID\":2,\"OrganizationCodeID\":2,\"ParentOrganizationID\":1}," +
                "{\"OrganizationID\":101,\"OrganizationCodeID\":3,\"ParentOrganizationID\":2}]}" );
        var provider = await CreatePolarisProviderAsync(handler, "username-identity-" + Guid.NewGuid().ToString("N"));
        await using var scoped = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.AddSingleton<IPatronProvider>(provider);
        }));
        using var client = scoped.CreateClient();
        long? sessionId = null;
        try
        {
            var login = await client.PostAsJsonAsync("/api/asap/patron/login", new
            {
                username,
                pin = "1234",
                libraryOrgId = 2
            });

            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());
            Assert.AreEqual(1, handler.PatronAuthenticationCalls);
            Assert.AreEqual(1, handler.PatronDataCalls);

            using var loginBody = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            await using (var context = await scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                .CreateDbContextAsync())
            {
                var session = await context.PatronSessions.SingleAsync(item => item.NativePatronId == nativePatronId);
                sessionId = session.Id;
                Assert.AreEqual(currentBarcode, session.Barcode,
                    "The provider-verified current barcode is the stored alias; the credentialed username is not.");
                Assert.AreEqual(0, await context.PatronSessions.CountAsync(item => item.Barcode == username));
            }

            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", loginBody.RootElement.GetProperty("token").GetString());
            var restored = await client.GetAsync("/api/asap/patron/session");
            Assert.AreEqual(HttpStatusCode.OK, restored.StatusCode, await restored.Content.ReadAsStringAsync());
            Assert.AreEqual(2, handler.PatronDataCalls,
                "Restoration uses the persisted stable identity while refreshing its current provider snapshot.");
        }
        finally
        {
            if (sessionId.HasValue)
            {
                await DeleteTestPatronSessionAsync(sessionId.Value);
            }
        }
    }

    [TestMethod]
    public async Task PinnedPolarisAuthenticatedIdMismatchCannotCreateHttpSessionOrReachSuggestionIntent()
    {
        const string loginBarcode = "20000000009992";
        const string returnedBarcode = "20000000009993";
        var handler = new NativePatronIdentityHandler(
            authenticatedPatronId: 739992,
            currentBarcode: returnedBarcode,
            basicDataPatronId: 739993,
            organizations: "{\"PAPIErrorCode\":0,\"OrganizationsGetRows\":[" +
                "{\"OrganizationID\":1,\"OrganizationCodeID\":1,\"ParentOrganizationID\":null}," +
                "{\"OrganizationID\":2,\"OrganizationCodeID\":2,\"ParentOrganizationID\":1}]}" );
        var provider = await CreatePolarisProviderAsync(handler, "mismatched-identity-" + Guid.NewGuid().ToString("N"));
        await using var scoped = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.AddSingleton<IPatronProvider>(provider);
        }));
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        int requestCountBefore;
        int outboxCountBefore;
        await using (var before = await contexts.CreateDbContextAsync())
        {
            requestCountBefore = await before.TitleRequests.CountAsync();
            outboxCountBefore = await before.EmailOutbox.CountAsync();
        }

        using var client = scoped.CreateClient();
        var login = await client.PostAsJsonAsync("/api/asap/patron/login", new
        {
            barcode = loginBarcode,
            pin = "1234",
            libraryOrgId = 2
        });
        Assert.AreEqual(HttpStatusCode.InternalServerError, login.StatusCode);
        Assert.AreEqual(1, handler.PatronAuthenticationCalls);
        Assert.AreEqual(1, handler.PatronDataCalls);
        Assert.AreEqual(0, handler.OrganizationCalls,
            "The package's matching typed model cannot turn a conflicting raw patron ID into authority." );

        var suggestion = await client.PostAsJsonAsync("/api/asap/patron/suggestions", new
        {
            format = "book",
            title = "Identity mismatch must not create an intent",
            author = "Test Author",
            publication = "Coming soon",
            preferredPickupBranchId = 101
        });
        Assert.AreEqual(HttpStatusCode.Unauthorized, suggestion.StatusCode);

        await using var verify = await contexts.CreateDbContextAsync();
        Assert.AreEqual(0, await verify.PatronSessions.CountAsync(item =>
            item.Barcode == loginBarcode || item.Barcode == returnedBarcode));
        Assert.AreEqual(requestCountBefore, await verify.TitleRequests.CountAsync());
        Assert.AreEqual(outboxCountBefore, await verify.EmailOutbox.CountAsync());
    }

    [TestMethod]
    public async Task LegacySessionWithoutNativeIdentityCannotAuthorizeRestoreOrSuggestionSubmission()
    {
        const string barcode = "legacy-unbound-session-identity";
        var issued = await factory!.Services.GetRequiredService<PatronSessionService>()
            .IssueAsync(barcode, 739994, 2, 2, 2, CancellationToken.None);
        Assert.IsNotNull(issued);
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        int requestCountBefore;
        int outboxCountBefore;
        await using (var before = await contexts.CreateDbContextAsync())
        {
            requestCountBefore = await before.TitleRequests.CountAsync();
            outboxCountBefore = await before.EmailOutbox.CountAsync();
        }

        try
        {
            await ExecuteNonQueryAsync(
                "UPDATE [asap].[PatronSession] SET [NativePatronId] = NULL WHERE [Id] = @id;",
                ("@id", issued.Context.Id));
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", issued.Token);

            var restored = await client.GetAsync("/api/asap/patron/session");
            Assert.AreEqual(HttpStatusCode.Unauthorized, restored.StatusCode);
            var suggestion = await client.PostAsJsonAsync("/api/asap/patron/suggestions", new
            {
                format = "book",
                title = "An unbound legacy session cannot create a request",
                author = "Test Author",
                publication = "Coming soon",
                preferredPickupBranchId = 101
            });
            Assert.AreEqual(HttpStatusCode.Unauthorized, suggestion.StatusCode);

            await using var verify = await contexts.CreateDbContextAsync();
            Assert.AreEqual(1, await verify.PatronSessions.CountAsync(item =>
                item.Id == issued.Context.Id && item.NativePatronId == null),
                "The legacy identity is preserved as unknown while authorization fails closed.");
            Assert.AreEqual(requestCountBefore, await verify.TitleRequests.CountAsync());
            Assert.AreEqual(outboxCountBefore, await verify.EmailOutbox.CountAsync());
        }
        finally
        {
            await DeleteTestPatronSessionAsync(issued.Context.Id);
        }
    }

    [TestMethod]
    public async Task SameNativePatronCannotOpenDuplicateBibAcrossBarcodeRotation()
    {
        const int scope = 99089;
        const int actionNativeId = 7654321;
        const int workflowNativeId = 7654322;
        const int actionBibId = 991801;
        const int workflowBibId = 991802;
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await EnsureSlice5IsolatedLibraryAsync(contextFactory, scope);
        var testingProvider = factory.Services.GetRequiredService<DeterministicTestingPatronProvider>();
        testingProvider.SetBib(actionBibId, scope,
            new BibValidationResult(true, "Declared native identity action BIB"));
        testingProvider.SetBib(workflowBibId, scope,
            new BibValidationResult(true, "Declared native identity workflow BIB"));

        var actionTarget = await SeedBibOwnershipRequestAsync(
            "native-id-duplicate-action-target", null, false, status: "suggestion", autoHold: true,
            libraryOrganizationId: scope);
        var actionBlocker = await SeedBibOwnershipRequestAsync(
            "native-id-duplicate-action-blocker", actionBibId, true, status: "pending_hold", autoHold: true,
            libraryOrganizationId: scope);
        var workflowTarget = await SeedBibOwnershipRequestAsync(
            "native-id-duplicate-workflow-target", workflowBibId, true,
            status: "outstanding_purchase", autoHold: true, libraryOrganizationId: scope);
        var workflowBlocker = await SeedBibOwnershipRequestAsync(
            "native-id-duplicate-workflow-blocker", workflowBibId, true,
            status: "pending_hold", autoHold: true, libraryOrganizationId: scope);
        var requestIds = new[] { actionTarget.Id, actionBlocker.Id, workflowTarget.Id, workflowBlocker.Id };
        try
        {
            byte[] actionVersion;
            byte[] workflowVersion;
            await using (var setup = await contextFactory.CreateDbContextAsync())
            {
                var actionTargetRow = await setup.TitleRequests.SingleAsync(item => item.Id == actionTarget.Id);
                var actionBlockerRow = await setup.TitleRequests.SingleAsync(item => item.Id == actionBlocker.Id);
                var workflowTargetRow = await setup.TitleRequests.SingleAsync(item => item.Id == workflowTarget.Id);
                var workflowBlockerRow = await setup.TitleRequests.SingleAsync(item => item.Id == workflowBlocker.Id);
                actionTargetRow.PatronIdSnapshot = actionNativeId;
                actionBlockerRow.PatronIdSnapshot = actionNativeId;
                workflowTargetRow.PatronIdSnapshot = workflowNativeId;
                workflowBlockerRow.PatronIdSnapshot = workflowNativeId;
                setup.WorkflowSettings.Add(new WorkflowSettings
                {
                    OrganizationId = scope,
                    AutoPromote = true,
                    UpdatedUtc = timeProvider!.GetUtcNow().UtcDateTime
                });
                await setup.SaveChangesAsync();
                actionVersion = actionTargetRow.RowVersion.ToArray();
            }

            using var bib = JsonDocument.Parse($"\"{actionBibId}\"");
            var mutation = await factory.Services.GetRequiredService<TitleRequestMutationService>().ActionAsync(
                await GetOwnershipTestActorAsync(),
                actionTarget.Id,
                new TitleRequestActionInput
                {
                    Version = StaffVersion.Encode(actionVersion),
                    Action = "catalogFound",
                    Bibid = ReadNativeBib(bib.RootElement)
                }.ToCommand(),
                CancellationToken.None);
            Assert.AreEqual("duplicate_open_request", mutation.Code);
            Assert.AreEqual(actionBlocker.Id, mutation.Duplicate?.Id);

            int outboxBefore;
            await using (var before = await contextFactory.CreateDbContextAsync())
            {
                outboxBefore = await before.EmailOutbox.CountAsync(item => item.OrganizationId == scope);
            }
            await PrepareSingleItemCycleAsync(QueueNames.PurchasePromotion, scope, workflowTarget.Id);
            await using (var beforeWorkflow = await contextFactory.CreateDbContextAsync())
            {
                workflowVersion = await beforeWorkflow.TitleRequests.AsNoTracking()
                    .Where(item => item.Id == workflowTarget.Id)
                    .Select(item => item.RowVersion)
                    .SingleAsync();
            }
            var cycle = await factory.Services.GetRequiredService<WorkflowProcessingService>()
                .ProcessWorkflowAsync(scope, CancellationToken.None);
            Assert.AreEqual("completed", cycle.Code);

            await using var verify = await contextFactory.CreateDbContextAsync();
            var unchangedAction = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == actionTarget.Id);
            var unchangedWorkflow = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == workflowTarget.Id);
            Assert.AreEqual("suggestion", unchangedAction.Status);
            Assert.IsNull(unchangedAction.BibId);
            CollectionAssert.AreEqual(actionVersion, unchangedAction.RowVersion);
            Assert.AreEqual("outstanding_purchase", unchangedWorkflow.Status);
            CollectionAssert.AreEqual(workflowVersion, unchangedWorkflow.RowVersion);
            Assert.AreEqual(0, await verify.TitleRequestEvents.CountAsync(item => requestIds.Contains(item.TitleRequestId)));
            Assert.AreEqual(outboxBefore, await verify.EmailOutbox.CountAsync(item => item.OrganizationId == scope));
        }
        finally
        {
            await DeleteBibOwnershipRequestsAsync(requestIds);
            await using var cleanup = await contextFactory.CreateDbContextAsync();
            await cleanup.QueueProgress.Where(item => item.ScopeOrganizationId == scope).ExecuteDeleteAsync();
            await cleanup.WorkflowSettings.Where(item => item.OrganizationId == scope).ExecuteDeleteAsync();
            await cleanup.Organizations.Where(item => item.Id == scope).ExecuteDeleteAsync();
        }
    }

    [TestMethod]
    public async Task StaffPickupRejectsBarcodeReassignmentToDifferentNativePatron()
    {
        var provider = new PickupJournalProvider(35123);
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var request = await SeedPickupJournalRequestAsync(contexts, provider);
        var actor = await ReadConfiguredSuperAdminAsync();
        var beforeOutbox = await OutboxCountAsync(contexts, provider.OrganizationId);
        try
        {
            await using (var setup = await contexts.CreateDbContextAsync())
            {
                var row = await setup.TitleRequests.SingleAsync(item => item.Id == request.Id);
                row.PatronIdSnapshot = provider.PatronId;
                await setup.SaveChangesAsync();
                request = row;
            }
            var expectedVersion = request.RowVersion.ToArray();
            provider.PatronId++;
            var service = scoped.Services.GetRequiredService<StaffPickupService>();
            var options = await service.GetOptionsAsync(actor, request.Id, CancellationToken.None);
            Assert.AreEqual("pickup_changed_since_load", options.Code);
            var changed = await service.UpdateAsync(actor, request.Id,
                new(StaffVersion.Encode(expectedVersion), provider.SecondBranch, provider.FirstBranch, true),
                CancellationToken.None);
            Assert.AreEqual("pickup_changed_since_load", changed.Code);
            Assert.AreEqual(0, provider.Writes);
            Assert.AreEqual(0, await PickupJournalCountAsync(provider.Barcode, state: null));
            await AssertNoSuggestionSideEffectsAsync(contexts, provider, null, beforeOutbox,
                expectedRequestCount: 1, expectedEvents: 0);
            await using var verify = await contexts.CreateDbContextAsync();
            var unchanged = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id);
            Assert.AreEqual(request.PatronIdSnapshot, unchanged.PatronIdSnapshot);
            CollectionAssert.AreEqual(expectedVersion, unchanged.RowVersion);
            Assert.AreEqual(provider.FirstBranch, unchanged.PreferredPickupBranchId);
        }
        finally
        {
            await CleanupPickupJournalLibraryAsync(provider.OrganizationId);
        }
    }

    [TestMethod]
    public async Task StaffPickupRecoveryAcceptsVerifiedFormerBarcodeForSameNativePatron()
    {
        const int organizationId = 35124;
        const int nativePatronId = 7654331;
        const string originalBarcode = "200000000035124";
        const string rotatedBarcode = "200000000035125";
        const int firstBranchId = 3512401;
        const int secondBranchId = 3512402;
        var provider = new RotatingNativePickupProvider(organizationId, nativePatronId,
            originalBarcode, firstBranchId, secondBranchId);
        await using var scoped = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.AddSingleton<IPatronProvider>(provider);
        }));
        var contextFactory = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        long requestId = 0;
        try
        {
            await using (var setup = await contextFactory.CreateDbContextAsync())
            {
                setup.Organizations.Add(new Organization
                {
                    Id = organizationId,
                    DisplayName = "Native pickup recovery library",
                    OrganizationCodeId = OrganizationAuthority.LibraryOrganizationCodeId,
                    IsActive = true
                });
                setup.Organizations.Add(new Organization
                {
                    Id = firstBranchId,
                    DisplayName = "Native pickup recovery branch",
                    OrganizationCodeId = 3,
                    ParentOrganizationId = organizationId,
                    IsActive = false
                });
                var formatId = await setup.MaterialFormats.Where(item => item.OwnerOrganizationId == 1 && item.Code == "book")
                    .Select(item => item.Id).SingleAsync();
                var request = new TitleRequest
                {
                    LibraryOrganizationId = organizationId,
                    PatronOrganizationId = firstBranchId,
                    Barcode = originalBarcode,
                    PatronIdSnapshot = nativePatronId,
                    Title = "Pickup recovery keeps stable patron identity " + Guid.NewGuid().ToString("N"),
                    AutoHold = true,
                    MaterialFormatId = formatId,
                    Status = "pending_hold",
                    BibId = 991803,
                    BibIdStaffVerified = true,
                    PreferredPickupBranchId = firstBranchId,
                    PreferredPickupBranchName = "First",
                    CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime,
                    UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime
                };
                setup.TitleRequests.Add(request);
                await setup.SaveChangesAsync();
                requestId = request.Id;
            }

            var actor = await ReadConfiguredSuperAdminAsync();
            var pickup = scoped.Services.GetRequiredService<StaffPickupService>();
            byte[] expectedVersion;
            await using (var read = await contextFactory.CreateDbContextAsync())
            {
                expectedVersion = (await read.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == requestId))
                    .RowVersion.ToArray();
            }
            var input = new PickupPreferenceInput(StaffVersion.Encode(expectedVersion), secondBranchId,
                firstBranchId, true);
            var uncertain = await pickup.UpdateAsync(actor, requestId, input, CancellationToken.None);
            Assert.AreEqual("pickup_outcome_unconfirmed", uncertain.Code);
            Assert.IsNotNull(uncertain.OperationId);
            Assert.AreEqual(1, provider.Writes);

            provider.CurrentBarcode = rotatedBarcode;
            provider.FormerBarcode = originalBarcode;
            var recovered = await pickup.ReconcileAsync(actor, uncertain.OperationId.Value,
                new(input.Version, secondBranchId, true, true), CancellationToken.None);
            Assert.AreEqual("updated", recovered.Code);
            Assert.AreEqual(1, provider.Writes, "Recovery observes the live preference without repeating the provider write.");

            await using var verify = await contextFactory.CreateDbContextAsync();
            var row = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == requestId);
            Assert.AreEqual(originalBarcode, row.Barcode, "Recovery preserves the request's original barcode snapshot.");
            Assert.AreEqual(nativePatronId, row.PatronIdSnapshot);
            Assert.AreEqual(secondBranchId, row.PreferredPickupBranchId);
            await using (var connection = new SqlConnection(databaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new SqlCommand("""
                    SELECT [Barcode], [PatronId], [TitleRequestId], [CompletedUtc]
                    FROM [asap].[PickupPreferenceOperation] WHERE [Id] = @id;
                    """, connection);
                command.Parameters.AddWithValue("@id", uncertain.OperationId.Value);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.IsTrue(await reader.ReadAsync());
                Assert.AreEqual(originalBarcode, reader.GetString(0), "The operation retains its verified request alias.");
                Assert.AreEqual(nativePatronId, reader.GetInt32(1));
                Assert.AreEqual(requestId, reader.GetInt64(2));
                Assert.IsFalse(reader.IsDBNull(3));
            }
            Assert.AreEqual(1, await verify.TitleRequestEvents.CountAsync(item =>
                item.TitleRequestId == requestId && item.EventType == "pickup_preference_reconciled"));
        }
        finally
        {
            if (requestId > 0)
            {
                await ExecuteNonQueryAsync("DELETE FROM [asap].[PickupPreferenceOperation] WHERE [TitleRequestId] = @id; DELETE FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] = @id; DELETE FROM [asap].[TitleRequest] WHERE [Id] = @id;",
                    ("@id", requestId));
            }
            await ExecuteNonQueryAsync("DELETE FROM [asap].[Organization] WHERE [Id] IN (@branch, @organization);",
                ("@branch", firstBranchId), ("@organization", organizationId));
        }
    }

    [TestMethod]
    [DataRow("intent")]
    [DataRow("acceptance_without_pickup_write")]
    public async Task PublicCrossLibrarySuggestionRechecksCurrentRegistrationAtBothBoundaries(string boundary)
    {
        var organizationId = boundary == "intent" ? 35130 : 35131;
        var homeOrganizationId = organizationId + 1;
        var provider = new PickupJournalProvider(organizationId)
        {
            HomeLibraryOrganizationId = homeOrganizationId
        };
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await SeedPickupJournalRequestAsync(contexts, provider);
        await ExecuteNonQueryAsync("""
            INSERT INTO [asap].[Organization]
                ([Id], [DisplayName], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
            VALUES (@home, N'Cross-library patron home', 2, 1, 1);
            IF EXISTS (SELECT 1 FROM [asap].[WorkflowSettings] WHERE [OrganizationId] = @organization)
                UPDATE [asap].[WorkflowSettings] SET [AllowAnyRegisteredCardLogin] = 1
                WHERE [OrganizationId] = @organization;
            ELSE
                INSERT INTO [asap].[WorkflowSettings] ([OrganizationId], [AllowAnyRegisteredCardLogin], [UpdatedUtc])
                VALUES (@organization, 1, SYSUTCDATETIME());
            """, ("@home", homeOrganizationId), ("@organization", organizationId));
        var issued = await scoped.Services.GetRequiredService<PatronSessionService>().IssueAsync(
            provider.Barcode, provider.PatronId, homeOrganizationId, organizationId, organizationId, CancellationToken.None);
        Assert.IsNotNull(issued, "The original cross-library session must be issued while the current policy allows it.");

        var mutation = scoped.Services.GetRequiredService<PickupPreferenceMutationService>();
        var intentStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseIntent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (boundary == "intent")
        {
            mutation.BeforeIntentValidationForTesting = async token =>
            {
                intentStarted.TrySetResult();
                await releaseIntent.Task.WaitAsync(token);
            };
        }
        else
        {
            provider.AfterRefresh = async token =>
            {
                if (provider.Reads == 2)
                {
                    token.ThrowIfCancellationRequested();
                    await ExecuteNonQueryAsync(
                        "UPDATE [asap].[WorkflowSettings] SET [AllowAnyRegisteredCardLogin] = 0 WHERE [OrganizationId] = @organization;",
                        ("@organization", organizationId));
                }
            };
        }

        var outboxBefore = await OutboxCountAsync(contexts, organizationId);
        var title = "Cross-library policy boundary " + Guid.NewGuid().ToString("N");
        var selectedBranch = boundary == "intent" ? provider.SecondBranch : provider.FirstBranch;
        var submission = scoped.Services.GetRequiredService<PatronSuggestionService>().CreateAsync(
            issued.Context,
            Suggestion(title) with { PreferredPickupBranchId = selectedBranch },
            CancellationToken.None);
        try
        {
            if (boundary == "intent")
            {
                var reached = await Task.WhenAny(intentStarted.Task, submission);
                if (reached == submission)
                {
                    await submission;
                    Assert.Fail("The public cross-library request completed before the intent boundary.");
                }
                await intentStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await ExecuteNonQueryAsync(
                    "UPDATE [asap].[WorkflowSettings] SET [AllowAnyRegisteredCardLogin] = 0 WHERE [OrganizationId] = @organization;",
                    ("@organization", organizationId));
                releaseIntent.TrySetResult();
            }

            var failure = await Assert.ThrowsAsync<PatronFlowException>(() => submission);
            Assert.AreEqual(403, failure.StatusCode);
            using var body = JsonDocument.Parse(JsonSerializer.Serialize(failure.Response));
            Assert.AreEqual("patron_library_scope_changed", body.RootElement.GetProperty("code").GetString());
            Assert.AreEqual(0, provider.Writes);
            Assert.AreEqual(0, await PickupJournalCountAsync(provider.Barcode, state: null));
            await AssertNoSuggestionSideEffectsAsync(contexts, provider, title, outboxBefore, expectedRequestCount: 1);
            Assert.AreEqual(1, await CountCurrentSessionAsync(issued.Context.Id),
                "A policy revocation must reject the operation without deleting the existing session.");
        }
        finally
        {
            releaseIntent.TrySetResult();
            mutation.BeforeIntentValidationForTesting = null;
            provider.AfterRefresh = null;
            try
            {
                await submission;
            }
            catch (Exception)
            {
            }
            await DeleteTestPatronSessionAsync(issued.Context.Id);
            await CleanupPickupPolicyAsync(organizationId);
            await CleanupPickupJournalLibraryAsync(organizationId);
            await ExecuteNonQueryAsync("DELETE FROM [asap].[Organization] WHERE [Id] = @home;", ("@home", homeOrganizationId));
        }
    }

    private sealed class RotatingNativePickupProvider(
        int organizationId,
        int patronId,
        string originalBarcode,
        int firstBranchId,
        int secondBranchId) : IPatronProvider
    {
        public string CurrentBarcode { get; set; } = originalBarcode;
        public string? FormerBarcode { get; set; }
        public int? CurrentBranch { get; private set; } = firstBranchId;
        public int Writes { get; private set; }

        public Task<PatronSnapshot> AuthenticateAsync(string barcode, string pin, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Authentication is not part of this recovery scenario.");

        public Task<PatronSnapshot> RefreshAsync(string barcode, int requestedOrganizationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual(organizationId, requestedOrganizationId);
            Assert.IsTrue(string.Equals(barcode, CurrentBarcode, StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(barcode, FormerBarcode, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(new PatronSnapshot(patronId, CurrentBarcode, "rotated@example.org", "Native", "Patron",
                1, "Adult", organizationId, organizationId, "Native pickup recovery library", CurrentBranch,
                FormerBarcode, barcode));
        }

        public Task<IReadOnlyList<PickupBranch>> GetPickupBranchesAsync(
            PatronSnapshot patron, int requestedOrganizationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual(patronId, patron.PatronId);
            Assert.AreEqual(organizationId, requestedOrganizationId);
            return Task.FromResult<IReadOnlyList<PickupBranch>>(
                [new(firstBranchId, "First"), new(secondBranchId, "Second")]);
        }

        public Task UpdatePreferredPickupBranchAsync(
            string barcode, int pickupBranchId, int requestedOrganizationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual(CurrentBarcode, barcode);
            Assert.AreEqual(organizationId, requestedOrganizationId);
            Assert.AreEqual(secondBranchId, pickupBranchId);
            Writes++;
            CurrentBranch = pickupBranchId;
            throw new PolarisOperationalException("testing_uncertain_pickup", "The provider applied the write but the response was lost.");
        }

        public Task<IdentifierLookupResult> LookupIdentifierAsync(
            string identifier, int requestedOrganizationId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Identifier lookup is not part of this recovery scenario.");
    }

    private sealed class NativePatronIdentityHandler(
        int authenticatedPatronId,
        string currentBarcode,
        int? basicDataPatronId = null,
        string? organizations = null) : HttpMessageHandler
    {
        public int PatronAuthenticationCalls { get; private set; }
        public int PatronDataCalls { get; private set; }
        public int OrganizationCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            string content;
            if (path.Contains("/authenticator/staff", StringComparison.Ordinal))
            {
                content = "{\"PAPIErrorCode\":0,\"AccessToken\":\"native-id-token\"," +
                    "\"AccessSecret\":\"native-id-secret\",\"AuthExpDate\":\"2030-01-01T00:00:00Z\"}";
            }
            else if (path.Contains("/authenticator/patron", StringComparison.Ordinal))
            {
                PatronAuthenticationCalls++;
                content = "{\"PAPIErrorCode\":0," +
                    $"\"PatronID\":{authenticatedPatronId}," +
                    "\"AccessToken\":\"patron-token\",\"AccessSecret\":\"patron-secret\"}";
            }
            else if (path.EndsWith("/basicdata", StringComparison.Ordinal))
            {
                PatronDataCalls++;
                content = "{\"PAPIErrorCode\":0,\"PatronBasicData\":{" +
                    $"\"PatronID\":{basicDataPatronId ?? authenticatedPatronId}," +
                    $"\"Barcode\":\"{currentBarcode}\",\"PatronOrgID\":2," +
                    "\"PatronCodeID\":1,\"RequestPickupBranchID\":101}}";
            }
            else if (path.Contains("/pickupbranches", StringComparison.Ordinal))
            {
                content = "{\"PAPIErrorCode\":0,\"PickupBranchesRows\":[{\"ID\":101}]}";
            }
            else if (path.Contains("/organizations/", StringComparison.Ordinal))
            {
                OrganizationCalls++;
                content = organizations ?? "{\"PAPIErrorCode\":0,\"OrganizationsGetRows\":[]}";
            }
            else
            {
                throw new AssertFailedException("Unexpected pinned patron identity request: " + path);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content),
                RequestMessage = request
            });
        }
    }
}
