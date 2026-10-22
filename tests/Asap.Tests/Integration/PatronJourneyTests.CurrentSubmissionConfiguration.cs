using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow("required-option", false)]
    [DataRow("optional-option", false)]
    [DataRow("publication-disabled", false)]
    [DataRow("publication-removed", false)]
    [DataRow("autohold-opt-out", false)]
    [DataRow("field-type", false)]
    [DataRow("required-rule", false)]
    [DataRow("harmless-label", false)]
    [DataRow("harmless-scalar", false)]
    [DataRow("required-option", true)]
    [DataRow("optional-option", true)]
    [DataRow("publication-disabled", true)]
    [DataRow("publication-removed", true)]
    [DataRow("autohold-opt-out", true)]
    [DataRow("field-type", true)]
    [DataRow("required-rule", true)]
    [DataRow("harmless-label", true)]
    [DataRow("harmless-scalar", true)]
    [DataRow("post-pickup-option", true)]
    public Task PublicFinalAcceptanceUsesCompleteCurrentSettingsAfterReadiness(string change, bool pickupChanges) =>
        SuggestionSettingsRaceAsync(change, pickupChanges, staff: false);

    [TestMethod]
    [DataRow("required-option")]
    [DataRow("optional-option")]
    [DataRow("publication-disabled")]
    [DataRow("publication-removed")]
    [DataRow("field-type")]
    [DataRow("required-rule")]
    [DataRow("harmless-label")]
    [DataRow("harmless-scalar")]
    [DataRow("autohold-opt-out")]
    [DataRow("hidden-author")]
    [DataRow("hidden-identifier")]
    [DataRow("hidden-publication")]
    public Task StaffPickupIntentUsesCompleteCurrentSettingsWithoutBroadeningPublicPermissions(string change) =>
        SuggestionSettingsRaceAsync(change, pickupChanges: true, staff: true);

    [TestMethod]
    [DataRow("required-option")]
    [DataRow("optional-option")]
    [DataRow("publication-disabled")]
    [DataRow("publication-removed")]
    [DataRow("field-type")]
    [DataRow("required-rule")]
    [DataRow("hidden-author")]
    [DataRow("hidden-identifier")]
    [DataRow("hidden-publication")]
    [DataRow("harmless-label")]
    [DataRow("harmless-scalar")]
    [DataRow("autohold-opt-out")]
    public Task StaffFinalAcceptanceUsesCompleteCurrentSettingsWithoutPickupIntent(string change) =>
        SuggestionSettingsRaceAsync(change, pickupChanges: false, staff: true);

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public Task CurrentSelectKeysRemainUnambiguousWhenHarmlessOptionLabelMatchesKey(bool pickupChanges, bool staff) =>
        SuggestionSettingsRaceAsync("harmless-key-label", pickupChanges, staff);

    private async Task SuggestionSettingsRaceAsync(string change, bool pickupChanges, bool staff)
    {
        var provider = new PickupJournalProvider(36420);
        var sender = new SubmissionConfigurationReadinessBarrier();
        await using var scoped = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.AddSingleton<IPatronProvider>(provider);
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(sender);
        }));
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await SeedPickupJournalRequestAsync(contexts, provider);
        using var client = scoped.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        if (staff && pickupChanges)
        {
            scoped.Services.GetRequiredService<PickupPreferenceMutationService>().BeforeIntentValidationForTesting = async token =>
            {
                sender.Started.TrySetResult();
                await sender.Release.Task.WaitAsync(token);
            };
        }
        var optional = change is "optional-option" or "required-rule";
        const string key = "current_acceptance_choice";
        long sessionId = 0;
        Task<PatronSuggestionResult>? submission = null;
        try
        {
            using (var initial = await ReadSettingsDocumentAsync(client, provider.OrganizationId.ToString()))
            using (var saved = await SaveSettingsDocumentAsync(client, initial.RootElement,
                provider.OrganizationId.ToString(), new Dictionary<string, object?>
                {
                    ["workflow"] = new { allowPatronAutoholdOptOut = true, suggestionLimit = 50 },
                    ["publicationOptions"] = new[]
                    {
                        new { id = "soon", label = "Coming soon", enabled = true, sortOrder = 10 },
                        new { id = "later", label = "Later", enabled = true, sortOrder = 20 }
                    },
                    ["customFields"] = new[]
                    {
                        new
                        {
                            key, type = "select", label = "Original choice label", enabled = true, sortOrder = 10,
                            options = new[]
                            {
                                new { id = "a", label = "Original A", enabled = true, sortOrder = 10 },
                                new { id = "b", label = "B", enabled = true, sortOrder = 20 }
                            }
                        }
                    },
                    ["formatRules"] = new[]
                    {
                        new { code = "book", author = new { mode = "optional" },
                            identifier = new { mode = "optional" }, publication = new { mode = "optional" },
                            customFields = new Dictionary<string, object>
                        { [key] = new { mode = optional ? "optional" : "required", labelOverride = (string?)null } } }
                    }
                }))
            {
                Assert.AreEqual("saved", saved.RootElement.GetProperty("code").GetString());
            }
            var beforeOutbox = await OutboxCountAsync(contexts, provider.OrganizationId);
            var title = $"Current acceptance {change} {Guid.NewGuid():N}";
            var input = Suggestion(title) with
            {
                Isbn = change == "hidden-identifier" ? "9781400079988" : null,
                PreferredPickupBranchId = pickupChanges ? provider.SecondBranch : provider.FirstBranch,
                Autohold = false,
                CustomFields = change == "required-rule"
                    ? new Dictionary<string, string?>()
                    : new Dictionary<string, string?> { [key] = "a" }
            };
            var service = scoped.Services.GetRequiredService<PatronSuggestionService>();
            if (staff)
            {
                submission = service.CreateForStaffAsync(actor, provider.OrganizationId,
                    new StaffSuggestionInput(provider.OrganizationId, provider.Barcode, input.Format, input.Title,
                        input.Author, input.Isbn, input.Publication,
                        change == "hidden-publication" ? new DateOnly(2026, 12, 1) : null,
                        null, input.PreferredPickupBranchId,
                        provider.FirstBranch, input.Autohold, !pickupChanges, input.CustomFields,
                        CurrentPreferredPickupBranchObservedAtLoad: true), CancellationToken.None);
            }
            else
            {
                var session = await IssueTestPatronSessionAsync(provider.Barcode, provider.OrganizationId, provider.PatronId);
                sessionId = session.Id;
                submission = service.CreateAsync(session, input, CancellationToken.None);
            }
            await sender.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using (var current = await ReadSettingsDocumentAsync(client, provider.OrganizationId.ToString()))
            {
                var changes = new Dictionary<string, object?>();
                if (change is "required-option" or "optional-option" or "field-type" or "harmless-label" or "harmless-key-label")
                {
                    var fields = JsonNode.Parse(current.RootElement.GetProperty("stored").GetProperty("customFields").GetRawText())!.AsArray();
                    var field = fields.Single(item => item!["key"]!.GetValue<string>() == key)!;
                    if (change == "field-type")
                    {
                        field["type"] = "text";
                        field["options"] = new JsonArray();
                    }
                    else if (change == "harmless-label")
                    {
                        field["label"] = "Changed current label";
                        field["options"]![0]!["label"] = "Changed A label";
                    }
                    else if (change == "harmless-key-label")
                    {
                        field["options"]![1]!["label"] = "a";
                    }
                    else
                    {
                        field["options"]![0]!["enabled"] = false;
                    }
                    changes["customFields"] = fields;
                }
                else if (change.StartsWith("hidden-", StringComparison.Ordinal))
                {
                    changes["formatRules"] = new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["code"] = "book",
                            [change["hidden-".Length..]] = new { mode = "hidden" },
                            ["customFields"] = new Dictionary<string, object>
                            { [key] = new { mode = "required", labelOverride = (string?)null } }
                        }
                    };
                }
                else if (change.StartsWith("publication-", StringComparison.Ordinal))
                {
                    changes["publicationOptions"] = change == "publication-removed"
                        ? new[] { new { id = "later", label = "Later", enabled = true, sortOrder = 20 } }
                        : new[]
                        {
                            new { id = "soon", label = "Coming soon", enabled = false, sortOrder = 10 },
                            new { id = "later", label = "Later", enabled = true, sortOrder = 20 }
                        };
                }
                else if (change == "autohold-opt-out")
                {
                    changes["workflow"] = new { allowPatronAutoholdOptOut = false };
                }
                else if (change == "required-rule")
                {
                    changes["formatRules"] = new[]
                    {
                        new { code = "book", customFields = new Dictionary<string, object>
                        { [key] = new { mode = "required", labelOverride = (string?)null } } }
                    };
                }
                else
                {
                    changes["patron"] = new { loginNote = "Harmless text changed during readiness" };
                }
                using var saved = await SaveSettingsDocumentAsync(client, current.RootElement,
                    provider.OrganizationId.ToString(), changes);
                Assert.AreEqual("saved", saved.RootElement.GetProperty("code").GetString());
            }
            if (change == "post-pickup-option")
            {
                provider.AfterEffect = async token =>
                {
                    token.ThrowIfCancellationRequested();
                    using var current = await ReadSettingsDocumentAsync(client, provider.OrganizationId.ToString());
                    var fields = JsonNode.Parse(current.RootElement.GetProperty("stored").GetProperty("customFields").GetRawText())!.AsArray();
                    fields.Single(item => item!["key"]!.GetValue<string>() == key)!["options"]![0]!["enabled"] = false;
                    using var saved = await SaveSettingsDocumentAsync(client, current.RootElement,
                        provider.OrganizationId.ToString(), new Dictionary<string, object?> { ["customFields"] = fields });
                    Assert.AreEqual("saved", saved.RootElement.GetProperty("code").GetString());
                };
            }
            sender.Release.TrySetResult();
            var acceptedChange = change.StartsWith("harmless-", StringComparison.Ordinal) || staff && change == "autohold-opt-out";
            if (acceptedChange)
            {
                var accepted = await submission;
                await using var verify = await contexts.CreateDbContextAsync();
                var request = await verify.TitleRequests.SingleAsync(item => item.Id == accepted.Id);
                Assert.IsFalse(request.AutoHold);
                using var snapshot = JsonDocument.Parse(request.CustomFieldsJson!);
                Assert.AreEqual(staff && change == "harmless-label" ? "Changed current label" : "Original choice label",
                    snapshot.RootElement.GetProperty(key).GetProperty("label").GetString());
                Assert.AreEqual(staff && change == "harmless-label" ? "Changed A label" : "Original A",
                    snapshot.RootElement.GetProperty(key).GetProperty("displayValue").GetString(),
                    "Preserve the owning flow's submitted snapshot while enforcing current acceptance constraints.");
                Assert.AreEqual(2, await verify.TitleRequests.CountAsync(item => item.LibraryOrganizationId == provider.OrganizationId));
                Assert.AreEqual(1, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == accepted.Id));
            }
            else
            {
                var failure = await Assert.ThrowsAsync<PatronFlowException>(() => submission);
                Assert.AreEqual(409, failure.StatusCode);
                Assert.AreEqual(change == "post-pickup-option" ? 1 : 0, provider.Writes,
                    "Configuration invalid before intent must not dispatch an external pickup write.");
                if (change == "post-pickup-option")
                {
                    Assert.IsInstanceOfType<PatronSuggestionPickupChangedFailure>(failure.Response);
                    var partial = (PatronSuggestionPickupChangedFailure)failure.Response;
                    Assert.AreEqual("request_not_created_pickup_changed", partial.Code);
                    Assert.IsTrue(partial.PickupPreferenceChanged);
                    Assert.IsNotNull(partial.OperationId);
                    Assert.AreEqual(1, await ReadCountWithParameterAsync("""
                        SELECT COUNT(*) FROM [asap].[PickupPreferenceOperation]
                        WHERE [Barcode] = @barcode AND [State] = 2 AND [CompletedUtc] IS NULL
                          AND [TitleRequestId] IS NULL AND [ProviderConfirmedUtc] IS NOT NULL
                          AND [DispatchFinishedUtc] IS NOT NULL AND [ConfirmedByRead] = 0 AND [FailureCode] IS NULL;
                        """, ("@barcode", provider.Barcode)));
                    Assert.AreEqual(provider.SecondBranch, provider.Current);
                }
                else
                {
                    var rejection = JsonSerializer.SerializeToElement(failure.Response);
                    Assert.AreEqual("submission_configuration_changed",
                        rejection.GetProperty("code").GetString());
                    Assert.AreEqual(failure.Message, rejection.GetProperty("message").GetString(),
                        "The actual staff/public response body must retain the actionable refresh message.");
                }
                await AssertNoSuggestionSideEffectsAsync(contexts, provider, title, beforeOutbox, expectedRequestCount: 1);
            }
            var expectedWrites = pickupChanges && (acceptedChange || change == "post-pickup-option") ? 1 : 0;
            if (acceptedChange)
            {
                Assert.AreEqual(expectedWrites, provider.Writes);
            }
            Assert.AreEqual(expectedWrites, await PickupJournalCountAsync(provider.Barcode, state: null));
            Assert.AreEqual(0, sender.Sends);
        }
        finally
        {
            sender.Release.TrySetResult();
            if (submission is not null)
            {
                try
                {
                    await submission;
                }
                catch (Exception) when (submission.IsCompleted)
                {
                    // The test body owns the submission outcome; a failed task must not prevent fixture cleanup.
                }
            }
            if (sessionId != 0)
            {
                await DeleteTestPatronSessionAsync(sessionId);
            }
            await ExecuteNonQueryAsync("""
                DELETE FROM [asap].[EmailOutbox] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[AdministrativeAudit] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[MaterialFormatOverride] WHERE [LibraryOrganizationId] = @org;
                DELETE FROM [asap].[MaterialFormatCustomFieldRule] WHERE [LibraryOrganizationId] = @org;
                DELETE FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = @org;
                DELETE FROM [asap].[PublicationOptionSet] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[WorkflowSettings] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[PatronSettings] WHERE [OrganizationId] = @org;
                """, ("@org", provider.OrganizationId));
            await CleanupPickupJournalLibraryAsync(provider.OrganizationId);
        }
    }

    private sealed class SubmissionConfigurationReadinessBarrier : IEmailSender
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Sends { get; private set; }

        public async Task<EmailTransportReadiness> CheckReadinessAsync(int organizationId, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return EmailTransportReadiness.Configured;
        }

        public Task<EmailSendResult> SendAsync(EmailEnvelope envelope, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sends++;
            throw new InvalidOperationException("Submission acceptance must only enqueue email.");
        }
    }
}
