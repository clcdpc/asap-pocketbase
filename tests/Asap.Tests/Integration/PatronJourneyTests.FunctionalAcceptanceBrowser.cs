using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asap.Tests.Sql;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task ConfiguredPatronAcceptanceBrowserVerifiesRealFailuresCommitsAndRecovery()
    {
        const int library = 38077;
        factory!.UseKestrel(0);
        using var client = factory.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var provider = factory.Services.GetRequiredService<DeterministicTestingPatronProvider>();
        await ExecuteNonQueryAsync("""
            INSERT INTO [asap].[Organization]
                ([Id], [DisplayName], [Abbreviation], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
            VALUES (38077, N'Functional Acceptance Library', N'FAL', 2, 1, 1),
                   (38078, N'Acceptance Main', N'AM', 3, 38077, 0),
                   (38079, N'Acceptance North', N'AN', 3, 38077, 0);
            INSERT INTO [asap].[WorkflowSettings] ([OrganizationId], [SuggestionLimit], [UpdatedUtc])
            VALUES (38077, 10, SYSUTCDATETIME());
            """);
        await using (var seed = await contexts.CreateDbContextAsync())
        {
            seed.StaffUsers.Add(new StaffUser
            {
                EntraTenantId = actor.EntraTenantId,
                UserPrincipalName = "acceptance.staff@example.org",
                NormalizedUserPrincipalName = "ACCEPTANCE.STAFF@EXAMPLE.ORG",
                DisplayName = "Acceptance claimant",
                Role = "staff", OrganizationId = library, IsActive = true
            });
            await seed.SaveChangesAsync();
        }
        foreach (var patronNumber in new[] { 1, 2, 3 })
        {
            var barcode = $"2000000038077{patronNumber}";
            provider.AddPatron(new PatronSnapshot(380770 + patronNumber, barcode,
                    $"acceptance{patronNumber}@example.org", "Acceptance", $"Patron {patronNumber}",
                    1, "Adult", 38078, library, "Functional Acceptance Library", 38078),
                [new(38078, "Acceptance Main"), new(38079, "Acceptance North")], library);
            provider.AllowPickupUpdate(barcode, library, 38079);
        }
        try
        {
            await using var before = await contexts.CreateDbContextAsync();
            var claimant = await before.StaffUsers.AsNoTracking().SingleAsync(item =>
                item.OrganizationId == library);
            var root = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
            var artifacts = Path.Combine(root, ".artifacts", "browser", $"functional-acceptance-{Guid.NewGuid():N}");
            Directory.CreateDirectory(artifacts);
            var mutation = factory.Services.GetRequiredService<PickupPreferenceMutationService>();
            var intentStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseIntent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var intents = 0;
            mutation.BeforeIntentValidationForTesting = async token =>
            {
                if (Interlocked.Increment(ref intents) == 2)
                {
                    intentStarted.TrySetResult();
                    await releaseIntent.Task.WaitAsync(token);
                }
            };
            using var barrierLifetime = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var settingsChange = ChangeDuringIntentAsync();
            async Task ChangeDuringIntentAsync()
            {
                try
                {
                    await intentStarted.Task.WaitAsync(barrierLifetime.Token);
                    using var admin = factory.CreateClient();
                    AddTestingStaffHeaders(admin, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
                    admin.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(admin));
                    using var snapshot = await ReadSettingsDocumentAsync(admin, library.ToString(CultureInfo.InvariantCulture));
                    var fields = JsonNode.Parse(snapshot.RootElement.GetProperty("stored").GetProperty("customFields").GetRawText())!.AsArray();
                    var required = fields.Single(item => item!["key"]!.GetValue<string>() == "required_choice")!;
                    required["options"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == "alpha")!["enabled"] = false;
                    using var saved = await SaveSettingsDocumentAsync(admin, snapshot.RootElement,
                        library.ToString(CultureInfo.InvariantCulture), new Dictionary<string, object?> { ["customFields"] = fields });
                    Assert.AreEqual("saved", saved.RootElement.GetProperty("code").GetString());
                }
                finally
                {
                    releaseIntent.TrySetResult();
                }
            }
            int exitCode;
            string stdout;
            string stderr;
            try
            {
                (exitCode, stdout, stderr) = await RunBrowserScriptAsync(root, "functional-acceptance.cjs",
                    client.BaseAddress!.GetLeftPart(UriPartial.Authority), artifacts,
                    library.ToString(CultureInfo.InvariantCulture), actor.Id.ToString(CultureInfo.InvariantCulture),
                    actor.EntraTenantId.ToString(), actor.AuthenticationEmail,
                    claimant.Id.ToString(CultureInfo.InvariantCulture));
                if (exitCode == 0)
                {
                    await settingsChange;
                }
            }
            finally
            {
                barrierLifetime.Cancel();
                releaseIntent.TrySetResult();
                mutation.BeforeIntentValidationForTesting = null;
                try
                {
                    await settingsChange;
                }
                catch (OperationCanceledException) when (barrierLifetime.IsCancellationRequested)
                {
                    // Browser failure before reaching the barrier cancels only this test's waiter.
                }
            }
            Assert.AreEqual(0, exitCode, $"{artifacts}{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(artifacts, "browser-results.json")));
            Assert.AreEqual(0, report.RootElement.GetProperty("externalRequests").GetInt32());
            Assert.AreEqual(0, report.RootElement.GetProperty("pageErrors").GetArrayLength());
            foreach (var invariant in new[] { "settingsRejectionUnchanged", "settingsStaleWinnerPreserved",
                         "pendingSubmitSinglePost", "optionalChoicesCleared", "lostCommitLocked",
                         "committedOldIdentityIgnored", "currentConfigurationRejected", "weeklyLimitRejected",
                         "expiredSessionRejected", "preDispatchFailureNoReplay" })
            {
                Assert.IsTrue(report.RootElement.GetProperty(invariant).GetBoolean(), invariant);
            }

            await using var verify = await contexts.CreateDbContextAsync();
            var requests = await verify.TitleRequests.AsNoTracking().Where(item =>
                item.LibraryOrganizationId == library).OrderBy(item => item.Id).ToArrayAsync();
            CollectionAssert.AreEquivalent(new[] { "Acceptance Committed", "Acceptance Unknown",
                    "Acceptance Old Identity", "Acceptance Current Config", "Acceptance Recovered" },
                requests.Select(item => item.Title).ToArray(),
                "Failures, repeated clicks, refresh and lost acknowledgements must not create extra requests.");
            var committed = requests.Single(item => item.Title == "Acceptance Committed");
            Assert.AreEqual(380771, committed.PatronIdSnapshot);
            Assert.AreEqual("20000000380771", committed.Barcode);
            Assert.AreEqual(38079, committed.PreferredPickupBranchId);
            Assert.AreEqual("Acceptance North", committed.PreferredPickupBranchName);
            Assert.IsFalse(committed.AutoHold);
            Assert.IsNull(committed.Identifier);
            Assert.IsNull(committed.Publication, "An optional publication cleared through the UI must persist as null.");
            Assert.AreEqual(claimant.Id, committed.ClaimedByStaffUserId);
            Assert.AreEqual("automatic_format_rule", committed.ClaimType);
            using (var snapshot = JsonDocument.Parse(committed.CustomFieldsJson!))
            {
                Assert.IsFalse(snapshot.RootElement.TryGetProperty("optional_choice", out _));
                Assert.AreEqual("Book recommendation detail", snapshot.RootElement.GetProperty("recommendation_note")
                    .GetProperty("label").GetString());
                Assert.AreEqual("Acceptance detail", snapshot.RootElement.GetProperty("recommendation_note")
                    .GetProperty("value").GetString());
                var choice = snapshot.RootElement.GetProperty("required_choice");
                Assert.AreEqual("alpha", choice.GetProperty("value").GetString());
                Assert.AreEqual("Alpha display", choice.GetProperty("displayValue").GetString());
            }
            var current = requests.Single(item => item.Title == "Acceptance Current Config");
            using (var snapshot = JsonDocument.Parse(current.CustomFieldsJson!))
            {
                Assert.AreEqual("beta", snapshot.RootElement.GetProperty("required_choice").GetProperty("value").GetString());
            }
            Assert.AreEqual(1, provider.Calls.Count(item => item.Operation == TestingPolarisOperation.PickupUpdate),
                "Exactly one positive-control pickup write; ambiguous response/retries must not dispatch additional writes.");
            Assert.AreEqual(1, await ReadCountAsync($"SELECT COUNT(*) FROM [asap].[PickupPreferenceOperation] WHERE [LibraryOrganizationId] = {library};"));
            var requestIds = requests.Select(item => item.Id).ToArray();
            var events = await verify.TitleRequestEvents.AsNoTracking().Where(item =>
                requestIds.Contains(item.TitleRequestId)).ToArrayAsync();
            Assert.AreEqual(5, events.Count(item => item.EventType == "created"));
            Assert.AreEqual(1, await ReadCountAsync($"""
                SELECT COUNT(*) FROM [asap].[PickupPreferenceOperation]
                WHERE [LibraryOrganizationId] = {library} AND [TitleRequestId] = {committed.Id}
                  AND [State] = 3 AND [CompletedUtc] IS NOT NULL AND [ProviderConfirmedUtc] IS NOT NULL
                  AND [PatronId] = 380771 AND [ToPickupBranchId] = 38079;
                """));
            Assert.AreEqual(0, provider.CreateCommands.Count);
            Assert.AreEqual(0, provider.ReplyCommands.Count);
            var outboxes = await verify.EmailOutbox.AsNoTracking().Where(item => item.OrganizationId == library).ToArrayAsync();
            Assert.HasCount(5, outboxes);
            Assert.IsTrue(outboxes.All(item => item.Status == "pending"));
            Assert.AreEqual(5, dispatcher!.EnqueuedIds.Count);
            var sender = (RecordingEmailSender)factory.Services.GetRequiredService<IEmailSender>();
            var jobs = factory.Services.GetRequiredService<EmailOutboxJobs>();
            foreach (var outbox in outboxes)
            {
                await jobs.DeliverAsync(outbox.Id, CancellationToken.None);
                await jobs.DeliverAsync(outbox.Id, CancellationToken.None);
            }
            Assert.HasCount(5, sender.Envelopes, "Re-delivery of a definitive recorded send must not send twice.");
            foreach (var request in requests)
            {
                var delivered = sender.Envelopes.Single(item => item.Subject.Contains(request.Title, StringComparison.Ordinal));
                Assert.AreEqual(request.Email, delivered.ToAddress);
                StringAssert.Contains(delivered.BodyText!, request.Title);
            }
            Assert.AreEqual(5, await verify.EmailOutbox.AsNoTracking().CountAsync(item =>
                item.OrganizationId == library && item.Status == "sent" && item.ProviderMessageId == "test-message"));
        }
        finally
        {
            await ExecuteNonQueryAsync("""
                DELETE FROM [asap].[EmailDeliveryEvent] WHERE [EmailOutboxId] IN
                    (SELECT [Id] FROM [asap].[EmailOutbox] WHERE [OrganizationId] = 38077);
                DELETE FROM [asap].[EmailOutbox] WHERE [OrganizationId] = 38077;
                DELETE FROM [asap].[PickupPreferenceOperation] WHERE [LibraryOrganizationId] = 38077;
                DELETE FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] IN
                    (SELECT [Id] FROM [asap].[TitleRequest] WHERE [LibraryOrganizationId] = 38077);
                DELETE FROM [asap].[TitleRequest] WHERE [LibraryOrganizationId] = 38077;
                DELETE FROM [asap].[PatronSession] WHERE [EffectiveOrganizationId] = 38077;
                DELETE FROM [asap].[AdministrativeAudit] WHERE [OrganizationId] = 38077;
                DELETE FROM [asap].[FormatAutoClaimRule] WHERE [LibraryOrganizationId] = 38077;
                DELETE FROM [asap].[MaterialFormatCustomFieldRule] WHERE [LibraryOrganizationId] = 38077;
                DELETE FROM [asap].[MaterialFormatOverride] WHERE [LibraryOrganizationId] = 38077;
                DELETE FROM [asap].[PatronCustomFieldOption] WHERE [PatronCustomFieldId] IN
                    (SELECT [Id] FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = 38077);
                DELETE FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = 38077;
                DELETE FROM [asap].[PublicationOption] WHERE [OrganizationId] = 38077;
                DELETE FROM [asap].[PublicationOptionSet] WHERE [OrganizationId] = 38077;
                DELETE FROM [asap].[PatronSettings] WHERE [OrganizationId] = 38077;
                DELETE FROM [asap].[WorkflowSettings] WHERE [OrganizationId] = 38077;
                DELETE FROM [asap].[StaffUser] WHERE [OrganizationId] = 38077;
                DELETE FROM [asap].[Organization] WHERE [Id] IN (38078, 38079);
                DELETE FROM [asap].[Organization] WHERE [Id] = 38077;
                """);
        }
    }
}
