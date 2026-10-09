using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Testing;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task SystemFormatRuleEditorPersistsItsActualPostThroughSqlAndReload()
    {
        factory!.UseKestrel(0);
        var actor = await ReadConfiguredSuperAdminAsync();
        using var client = factory.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

        var originalFormat = await ReadSystemFormatEvidenceAsync("book");
        var auditBefore = await ReadAuditHighWatermarkAsync();
        string unrelatedBefore;
        string? originalLoginPrompt;
        string? originalLoginNote;
        string? originalSuggestionFormNote;
        using (var initial = await ReadSettingsDocumentAsync(client, "system"))
        {
            var stored = initial.RootElement.GetProperty("stored");
            unrelatedBefore = SerializeSystemSettingsOutsideSubmittedPatronAndFormats(stored);
            var patron = stored.GetProperty("patron");
            originalLoginPrompt = patron.GetProperty("loginPrompt").GetString();
            originalLoginNote = patron.GetProperty("loginNote").GetString();
            originalSuggestionFormNote = patron.GetProperty("suggestionFormNote").GetString();
        }
        var postedPatronValues = new Dictionary<string, string?>(StringComparer.Ordinal);
        try
        {
            var repositoryRoot = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
            var artifactDirectory = Path.Combine(repositoryRoot, ".artifacts", "browser", $"settings-format-rule-{Guid.NewGuid():N}");
            Directory.CreateDirectory(artifactDirectory);
            var baseAddress = client.BaseAddress
                ?? throw new InvalidOperationException("The Kestrel test host did not expose a base address.");
            var startInfo = new ProcessStartInfo
            {
                FileName = "node",
                WorkingDirectory = repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(Path.Combine(repositoryRoot, "tests", "browser", "settings.cjs"));
            startInfo.ArgumentList.Add(baseAddress.GetLeftPart(UriPartial.Authority));
            startInfo.ArgumentList.Add(artifactDirectory);
            startInfo.ArgumentList.Add(actor.Id.ToString());
            startInfo.ArgumentList.Add(actor.EntraTenantId.ToString());
            startInfo.ArgumentList.Add(actor.AuthenticationEmail);
            startInfo.ArgumentList.Add("system-format-rule");

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the focused settings browser runner.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var reportPath = Path.Combine(artifactDirectory, "settings-browser-results.json");
            Assert.IsTrue(File.Exists(reportPath), $"The focused browser runner did not write its report. {stdout}{Environment.NewLine}{stderr}");

            using (var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath)))
            {
                var root = report.RootElement;
                Assert.AreEqual(0, root.GetProperty("externalRequests").GetInt32());
                Assert.AreEqual(0, root.GetProperty("pageErrors").GetArrayLength());
                Assert.AreEqual(1, root.GetProperty("systemFormatRulePostCount").GetInt32(),
                    "The focused browser journey must submit exactly one settings POST.");
                Assert.IsTrue(root.GetProperty("postedEditorPayload").GetBoolean());
                var post = root.GetProperty("systemFormatRulePost");
                Assert.AreNotEqual(JsonValueKind.Null, post.ValueKind,
                    $"The shipped editor did not reach its settings POST. {stdout}{Environment.NewLine}{stderr}");
                var responseStatus = (HttpStatusCode)post.GetProperty("status").GetInt32();
                var responseBody = post.GetProperty("responseBody").GetString();
                Assert.AreEqual(HttpStatusCode.OK, responseStatus,
                    $"The actual System editor request should save successfully. Body: {responseBody}{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");

                using var request = JsonDocument.Parse(post.GetProperty("requestBody").GetString()!);
                var payload = request.RootElement;
                Assert.AreEqual("system", payload.GetProperty("orgId").GetString());
                var patron = payload.GetProperty("patron");
                foreach (var property in patron.EnumerateObject())
                {
                    postedPatronValues.Add(property.Name, property.Value.GetString());
                }
                var bookRule = payload.GetProperty("formatRules").EnumerateArray()
                    .Single(item => item.GetProperty("code").GetString() == "book");
                Assert.IsFalse(bookRule.TryGetProperty("customFields", out _),
                    "The actual System editor payload must omit library-only customFields.");
                Assert.AreEqual("message", bookRule.GetProperty("messageBehavior").GetString());
                Assert.AreEqual("System format-rule integration witness", bookRule.GetProperty("message").GetString());
                Assert.IsTrue(root.GetProperty("systemFormatRuleRoundTrip").GetBoolean(),
                    "The browser must reload the saved values from the authoritative settings endpoint.");
                Assert.HasCount(2, root.GetProperty("states").EnumerateArray().ToArray());
            }

            Assert.AreEqual(0, process.ExitCode, $"{stdout}{Environment.NewLine}{stderr}");
            var savedFormat = await ReadSystemFormatEvidenceAsync("book");
            Assert.AreEqual("message", savedFormat.MessageBehavior);
            Assert.AreEqual("System format-rule integration witness", savedFormat.Message);
            Assert.IsFalse(originalFormat.RowVersion.SequenceEqual(savedFormat.RowVersion),
                "The actual editor POST must persist the System format rule in SQL.");
            using var saved = await ReadSettingsDocumentAsync(client, "system");
            var savedPatron = saved.RootElement.GetProperty("stored").GetProperty("patron");
            Assert.IsGreaterThan(0, postedPatronValues.Count,
                "The System editor must submit its patron text values along with the format rule.");
            foreach (var (key, value) in postedPatronValues)
            {
                Assert.AreEqual(value, savedPatron.GetProperty(key).GetString(),
                    $"Stored System patron value '{key}' must match the value submitted by the actual editor.");
            }
            Assert.AreEqual(unrelatedBefore,
                SerializeSystemSettingsOutsideSubmittedPatronAndFormats(saved.RootElement.GetProperty("stored")),
                "Saving one System format rule must preserve unrelated System settings.");
        }
        finally
        {
            await ExecuteNonQueryAsync("""
                UPDATE [asap].[MaterialFormat]
                SET [MessageBehavior] = @messageBehavior, [Message] = @message, [UpdatedUtc] = SYSUTCDATETIME()
                WHERE [OwnerOrganizationId] = 1 AND [Code] = N'book';
                UPDATE [asap].[PatronSettings]
                SET [LoginPrompt] = @loginPrompt, [LoginNote] = @loginNote,
                    [SuggestionFormNote] = @suggestionFormNote, [UpdatedUtc] = SYSUTCDATETIME()
                WHERE [OrganizationId] = 1;
                DELETE FROM [asap].[AdministrativeAudit] WHERE [Id] > @auditBefore;
                """, ("@messageBehavior", (object?)originalFormat.MessageBehavior ?? DBNull.Value),
                ("@message", (object?)originalFormat.Message ?? DBNull.Value),
                ("@loginPrompt", (object?)originalLoginPrompt ?? DBNull.Value),
                ("@loginNote", (object?)originalLoginNote ?? DBNull.Value),
                ("@suggestionFormNote", (object?)originalSuggestionFormNote ?? DBNull.Value),
                ("@auditBefore", auditBefore));
        }
    }

    [TestMethod]
    [DataRow("save-action")]
    [DataRow("reset-route")]
    public async Task ResetRejectsAnInheritedFormatThatWouldMakeARequiredSelectImpossible(string resetPath)
    {
        factory!.UseKestrel(0);
        var actor = await ReadConfiguredSuperAdminAsync();
        using var client = factory.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

        var suffix = Guid.NewGuid().ToString("N");
        var formatCode = $"reset_required_{suffix}";
        var fieldKey = $"reset_required_{suffix}";
        var barcode = $"settings-reset-{suffix}";
        var auditBefore = await ReadAuditHighWatermarkAsync();
        try
        {
            await ExecuteNonQueryAsync("""
                INSERT INTO [asap].[MaterialFormat]
                    ([OwnerOrganizationId], [Code], [Label], [SortOrder], [IsEnabled], [CreatedUtc], [UpdatedUtc])
                VALUES (1, @formatCode, N'Reset required-select witness', 9870, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                DECLARE @formatId bigint = SCOPE_IDENTITY();
                INSERT INTO [asap].[MaterialFormatOverride]
                    ([LibraryOrganizationId], [MaterialFormatId], [IsEnabled])
                VALUES (2, @formatId, 0);
                INSERT INTO [asap].[PatronCustomField]
                    ([LibraryOrganizationId], [FieldKey], [FieldType], [Label], [IsEnabled], [SortOrder])
                VALUES (2, @fieldKey, N'select', N'Required reset choice', 1, 9870);
                DECLARE @fieldId bigint = SCOPE_IDENTITY();
                INSERT INTO [asap].[PatronCustomFieldOption]
                    ([PatronCustomFieldId], [OptionKey], [Label], [IsEnabled], [SortOrder])
                VALUES (@fieldId, N'only_choice', N'Only choice', 1, 10);
                INSERT INTO [asap].[MaterialFormatCustomFieldRule]
                    ([LibraryOrganizationId], [MaterialFormatId], [PatronCustomFieldId], [Mode], [LabelOverride])
                VALUES (2, @formatId, @fieldId, N'required', N'Required reset choice');
                INSERT INTO [asap].[PatronSession]
                    ([TokenHash], [Barcode], [EffectiveOrganizationId], [CreatedUtc], [ExpiresUtc])
                VALUES (HASHBYTES('SHA2_256', CONVERT(varbinary(100), @barcode)), @barcode, 2,
                    SYSUTCDATETIME(), DATEADD(hour, 1, SYSUTCDATETIME()));
                """, ("@formatCode", formatCode), ("@fieldKey", fieldKey), ("@barcode", barcode));

            using var initial = await ReadSettingsDocumentAsync(client, "2");
            var customFields = JsonNode.Parse(initial.RootElement.GetProperty("stored")
                .GetProperty("customFields").GetRawText())!.AsArray();
            var field = customFields.Single(item => item?["key"]?.GetValue<string>() == fieldKey)!;
            field["options"]![0]!["enabled"] = false;
            var completeRules = BuildFormatRuleRows(initial.RootElement.GetProperty("effective").GetProperty("formats"));
            var disableLastOption = new JsonObject
            {
                ["orgId"] = "2",
                ["version"] = initial.RootElement.GetProperty("version").GetString(),
                ["customFields"] = customFields,
                ["formatRules"] = completeRules
            };
            using (var savedLibraryEdit = await PostSettingsAtPathAsync(
                       client, "/api/asap/staff/settings/library", disableLastOption.ToJsonString()))
            {
                Assert.AreEqual(HttpStatusCode.OK, savedLibraryEdit.StatusCode,
                    "A required select may have no enabled options while its inherited format is explicitly disabled.");
            }

            using (var afterLibraryEdit = await ReadSettingsDocumentAsync(client, "2"))
            {
                Assert.IsFalse(afterLibraryEdit.RootElement.GetProperty("version").GetString() ==
                    initial.RootElement.GetProperty("version").GetString(),
                    "The editor-shaped library alias request must persist the disabled last option before reset is tested.");
            }
            Assert.AreEqual(1, await ReadCountWithParameterAsync("""
                SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] overrideRow
                JOIN [asap].[MaterialFormat] format ON format.[Id] = overrideRow.[MaterialFormatId]
                WHERE overrideRow.[LibraryOrganizationId] = 2 AND format.[Code] = @formatCode
                  AND overrideRow.[IsEnabled] = 0;
                """, ("@formatCode", formatCode)));
            Assert.AreEqual(1, await ReadCountWithParameterAsync("""
                SELECT COUNT(*) FROM [asap].[PatronCustomFieldOption] optionRow
                JOIN [asap].[PatronCustomField] field ON field.[Id] = optionRow.[PatronCustomFieldId]
                WHERE field.[LibraryOrganizationId] = 2 AND field.[FieldKey] = @fieldKey
                  AND optionRow.[OptionKey] = N'only_choice' AND optionRow.[IsEnabled] = 0;
                """, ("@fieldKey", fieldKey)));
            Assert.AreEqual(1, await ReadCountWithParameterAsync("""
                SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] ruleRow
                JOIN [asap].[PatronCustomField] field ON field.[Id] = ruleRow.[PatronCustomFieldId]
                JOIN [asap].[MaterialFormat] format ON format.[Id] = ruleRow.[MaterialFormatId]
                WHERE ruleRow.[LibraryOrganizationId] = 2 AND field.[FieldKey] = @fieldKey
                  AND format.[Code] = @formatCode AND ruleRow.[Mode] = N'required';
                """, ("@fieldKey", fieldKey), ("@formatCode", formatCode)));

            var beforeReset = await ReadRequiredSelectResetSnapshotAsync(client, formatCode, fieldKey, barcode);
            using var beforeDocument = await ReadSettingsDocumentAsync(client, "2");
            var version = beforeDocument.RootElement.GetProperty("version").GetString()!;
            var resetBody = resetPath == "save-action"
                ? JsonSerializer.Serialize(new { orgId = "2", version, action = "reset" })
                : JsonSerializer.Serialize(new { version });
            var path = resetPath == "save-action"
                ? "/api/asap/staff/settings"
                : "/api/asap/staff/settings/reset?organizationId=2";
            using var reset = await PostSettingsAtPathAsync(client, path, resetBody);
            var resetResponseBody = await reset.Content.ReadAsStringAsync();
            var afterReset = await ReadRequiredSelectResetSnapshotAsync(client, formatCode, fieldKey, barcode);
            var failures = new List<string>();
            if (reset.StatusCode != HttpStatusCode.BadRequest)
            {
                failures.Add($"Expected a feasibility rejection (400), received {(int)reset.StatusCode}: {resetResponseBody}");
            }
            if (!string.Equals(beforeReset, afterReset, StringComparison.Ordinal))
            {
                failures.Add("Rejected reset changed the settings version, audit, override, custom field, option, rule, session, event, or outbox evidence.");
            }
            Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));

            using var safeSettings = await ReadSettingsDocumentAsync(client, "2");
            var safeFields = JsonNode.Parse(safeSettings.RootElement.GetProperty("stored")
                .GetProperty("customFields").GetRawText())!.AsArray();
            var safeField = safeFields.Single(item => item?["key"]?.GetValue<string>() == fieldKey)!;
            safeField["options"]![0]!["enabled"] = true;
            var safeResettableEdit = new JsonObject
            {
                ["orgId"] = "2",
                ["version"] = safeSettings.RootElement.GetProperty("version").GetString(),
                ["customFields"] = safeFields,
                ["formatRules"] = BuildFormatRuleRows(safeSettings.RootElement.GetProperty("stored").GetProperty("formatRules"))
            };
            using (var restoredOption = await PostSettingsAtPathAsync(
                       client, "/api/asap/staff/settings/library", safeResettableEdit.ToJsonString()))
            {
                Assert.AreEqual(HttpStatusCode.OK, restoredOption.StatusCode,
                    "Restoring one enabled choice must make this inherited reset safe.");
            }
            using var safeSettingsAfterEdit = await ReadSettingsDocumentAsync(client, "2");
            Assert.AreEqual(1, await ReadCountWithParameterAsync("""
                SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] ruleRow
                JOIN [asap].[PatronCustomField] field ON field.[Id] = ruleRow.[PatronCustomFieldId]
                JOIN [asap].[MaterialFormat] format ON format.[Id] = ruleRow.[MaterialFormatId]
                WHERE ruleRow.[LibraryOrganizationId] = 2 AND field.[FieldKey] = @fieldKey
                  AND format.[Code] = @formatCode AND ruleRow.[Mode] = N'required';
                """, ("@fieldKey", fieldKey), ("@formatCode", formatCode)),
                "The safe editor request must preserve the stored required mode before reset runs.");
            var safeVersion = safeSettingsAfterEdit.RootElement.GetProperty("version").GetString()!;
            var validResetBody = resetPath == "save-action"
                ? JsonSerializer.Serialize(new { version = safeVersion })
                : JsonSerializer.Serialize(new { orgId = "2", version = safeVersion, action = "reset" });
            var validResetPath = resetPath == "save-action"
                ? "/api/asap/staff/settings/reset?organizationId=2"
                : "/api/asap/staff/settings";
            using (var validReset = await PostSettingsAtPathAsync(client, validResetPath, validResetBody))
            {
                Assert.AreEqual(HttpStatusCode.OK, validReset.StatusCode,
                    "A feasible reset must remain available through both supported reset entry points.");
            }
            Assert.AreEqual(0, await ReadCountWithParameterAsync("""
                SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] overrideRow
                JOIN [asap].[MaterialFormat] format ON format.[Id] = overrideRow.[MaterialFormatId]
                WHERE overrideRow.[LibraryOrganizationId] = 2 AND format.[Code] = @formatCode;
                """, ("@formatCode", formatCode)));
            Assert.AreEqual(1, await ReadCountWithParameterAsync("""
                SELECT COUNT(*) FROM [asap].[PatronCustomField]
                WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = @fieldKey AND [FieldType] = N'select' AND [IsEnabled] = 1;
                """, ("@fieldKey", fieldKey)));
            Assert.AreEqual(1, await ReadCountWithParameterAsync("""
                SELECT COUNT(*) FROM [asap].[PatronCustomFieldOption] optionRow
                JOIN [asap].[PatronCustomField] field ON field.[Id] = optionRow.[PatronCustomFieldId]
                WHERE field.[LibraryOrganizationId] = 2 AND field.[FieldKey] = @fieldKey
                  AND optionRow.[OptionKey] = N'only_choice' AND optionRow.[IsEnabled] = 1;
                """, ("@fieldKey", fieldKey)));
            Assert.AreEqual(1, await ReadCountWithParameterAsync("""
                SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] ruleRow
                JOIN [asap].[PatronCustomField] field ON field.[Id] = ruleRow.[PatronCustomFieldId]
                JOIN [asap].[MaterialFormat] format ON format.[Id] = ruleRow.[MaterialFormatId]
                WHERE ruleRow.[LibraryOrganizationId] = 2 AND field.[FieldKey] = @fieldKey
                  AND format.[Code] = @formatCode AND ruleRow.[Mode] = N'required';
                """, ("@fieldKey", fieldKey), ("@formatCode", formatCode)));
        }
        finally
        {
            await ExecuteNonQueryAsync("""
                DELETE ruleRow
                FROM [asap].[MaterialFormatCustomFieldRule] ruleRow
                JOIN [asap].[PatronCustomField] field ON field.[Id] = ruleRow.[PatronCustomFieldId]
                WHERE field.[LibraryOrganizationId] = 2 AND field.[FieldKey] = @fieldKey;
                DELETE optionRow
                FROM [asap].[PatronCustomFieldOption] optionRow
                JOIN [asap].[PatronCustomField] field ON field.[Id] = optionRow.[PatronCustomFieldId]
                WHERE field.[LibraryOrganizationId] = 2 AND field.[FieldKey] = @fieldKey;
                DELETE FROM [asap].[PatronCustomField]
                WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = @fieldKey;
                DELETE overrideRow
                FROM [asap].[MaterialFormatOverride] overrideRow
                JOIN [asap].[MaterialFormat] format ON format.[Id] = overrideRow.[MaterialFormatId]
                WHERE overrideRow.[LibraryOrganizationId] = 2 AND format.[Code] = @formatCode;
                DELETE FROM [asap].[MaterialFormat]
                WHERE [OwnerOrganizationId] = 1 AND [Code] = @formatCode;
                DELETE FROM [asap].[PatronSession] WHERE [Barcode] = @barcode;
                DELETE FROM [asap].[AdministrativeAudit] WHERE [Id] > @auditBefore;
                """, ("@fieldKey", fieldKey), ("@formatCode", formatCode), ("@barcode", barcode),
                ("@auditBefore", auditBefore));
        }
    }

    [TestMethod]
    [DataRow("partial-snapshot")]
    [DataRow("missing-row")]
    public async Task LibraryRuleEditorBlocksAnIncompleteProjectionBeforePostingReplacement(string witness)
    {
        factory!.UseKestrel(0);
        var actor = await ReadConfiguredSuperAdminAsync();
        using var client = factory.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

        var libraryId = await ReserveSettingsCourseLibraryIdAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var formatCode = $"partial_rule_{suffix}";
        var fieldKey = $"partial_rule_{suffix}";
        var failures = new List<string>();
        try
        {
            await ExecuteNonQueryAsync("""
                INSERT INTO [asap].[Organization]
                    ([Id], [DisplayName], [Abbreviation], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
                VALUES (@organizationId, N'Settings course rule witness', N'SCR', 2, 1, 1);
                INSERT INTO [asap].[MaterialFormat]
                    ([OwnerOrganizationId], [Code], [Label], [SortOrder], [IsEnabled], [CreatedUtc], [UpdatedUtc])
                VALUES (1, @formatCode, N'Partial rule witness', 9870, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                DECLARE @formatId bigint = SCOPE_IDENTITY();
                DECLARE @bookId bigint = (SELECT [Id] FROM [asap].[MaterialFormat]
                    WHERE [OwnerOrganizationId] = 1 AND [Code] = N'book');
                INSERT INTO [asap].[PatronCustomField]
                    ([LibraryOrganizationId], [FieldKey], [FieldType], [Label], [IsEnabled], [SortOrder])
                VALUES (@organizationId, @fieldKey, N'text', N'Partial rule witness', 1, 9870);
                DECLARE @fieldId bigint = SCOPE_IDENTITY();
                INSERT INTO [asap].[MaterialFormatCustomFieldRule]
                    ([LibraryOrganizationId], [MaterialFormatId], [PatronCustomFieldId], [Mode], [LabelOverride])
                VALUES (@organizationId, @bookId, @fieldId, N'optional', N'Visible book rule'),
                       (@organizationId, @formatId, @fieldId, N'optional', N'Omitted format rule');
                """, ("@organizationId", libraryId), ("@formatCode", formatCode), ("@fieldKey", fieldKey));

            using (var initial = await ReadSettingsDocumentAsync(client, libraryId.ToString()))
            {
                var initialVersion = initial.RootElement.GetProperty("version").GetString();
                var beforeRules = await ReadRuleEvidenceAsync(fieldKey, libraryId);
                using var beforeRulesDocument = JsonDocument.Parse(beforeRules);
                Assert.AreEqual(2, beforeRulesDocument.RootElement.GetArrayLength(),
                    "The temporary library fixture must contain one book rule and one custom-format rule.");
                var auditBeforeBrowser = await ReadSettingsCourseAuditHighWatermarkAsync(libraryId);
                var outboxBeforeBrowser = await ReadCountAsync("SELECT COUNT(*) FROM [asap].[EmailOutbox];");
                var eventsBeforeBrowser = await ReadCountAsync("SELECT COUNT(*) FROM [asap].[TitleRequestEvent];");
                var sessionsBeforeBrowser = await ReadCountAsync("SELECT COUNT(*) FROM [asap].[PatronSession];");

                var repositoryRoot = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
                var artifactDirectory = Path.Combine(repositoryRoot, ".artifacts", "browser", $"settings-rule-{witness}-{Guid.NewGuid():N}");
                Directory.CreateDirectory(artifactDirectory);
                var baseAddress = client.BaseAddress
                    ?? throw new InvalidOperationException("The Kestrel test host did not expose a base address.");
                var startInfo = new ProcessStartInfo
                {
                    FileName = "node",
                    WorkingDirectory = repositoryRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                startInfo.ArgumentList.Add(Path.Combine(repositoryRoot, "tests", "browser", "settings.cjs"));
                startInfo.ArgumentList.Add(baseAddress.GetLeftPart(UriPartial.Authority));
                startInfo.ArgumentList.Add(artifactDirectory);
                startInfo.ArgumentList.Add(actor.Id.ToString());
                startInfo.ArgumentList.Add(actor.EntraTenantId.ToString());
                startInfo.ArgumentList.Add(actor.AuthenticationEmail);
                startInfo.ArgumentList.Add(witness == "partial-snapshot"
                    ? "library-rule-partial-snapshot"
                    : "library-rule-missing-row");
                startInfo.ArgumentList.Add(libraryId.ToString());
                startInfo.ArgumentList.Add(formatCode);

                using var process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Could not start the focused library settings browser runner.");
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                var stdout = await stdoutTask;
                var stderr = await stderrTask;
                var reportPath = Path.Combine(artifactDirectory, "settings-browser-results.json");
                if (!File.Exists(reportPath))
                {
                    failures.Add($"The focused browser runner did not write its report. {stdout}{Environment.NewLine}{stderr}");
                }
                else
                {
                    using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
                    var root = report.RootElement;
                    var witnessReport = root.GetProperty("libraryFormatRuleWitness");
                    if (root.GetProperty("externalRequests").GetInt32() != 0 ||
                        root.GetProperty("pageErrors").GetArrayLength() != 0)
                    {
                        failures.Add("The focused browser journey made an external request or raised a page error.");
                    }
                    if (witnessReport.GetProperty("postCount").GetInt32() != 0)
                    {
                        var responseStatus = witnessReport.GetProperty("responseStatus").GetRawText();
                        var responseBody = witnessReport.GetProperty("responseBody").GetString();
                        failures.Add($"The incomplete projection submitted a settings POST. Status: {responseStatus}; response: {responseBody}");
                    }
                    if (!witnessReport.GetProperty("blocked").GetBoolean())
                    {
                        failures.Add($"The editor did not show a validation error for the incomplete projection: " +
                            $"{witnessReport.GetProperty("blockedMessage").GetString()}");
                    }
                    if (witness == "partial-snapshot" &&
                        (!witnessReport.GetProperty("partialSnapshotProjectionApplied").GetBoolean() ||
                         !witnessReport.GetProperty("storedTargetRulePresent").GetBoolean()))
                    {
                        failures.Add("The browser did not receive a partial endpoint-shaped snapshot with the saved target rule still stored.");
                    }
                    if (witness == "missing-row" &&
                        (!witnessReport.GetProperty("targetRowInitiallyPresent").GetBoolean() ||
                         !witnessReport.GetProperty("targetRowRemoved").GetBoolean()))
                    {
                        failures.Add("The browser did not remove the complete rendered row backed by the saved target rule.");
                    }
                    if (process.ExitCode != 0)
                    {
                        failures.Add($"The focused browser runner exited {process.ExitCode}. {stdout}{Environment.NewLine}{stderr}");
                    }
                }

                using var after = await ReadSettingsDocumentAsync(client, libraryId.ToString());
                if (!string.Equals(initialVersion, after.RootElement.GetProperty("version").GetString(), StringComparison.Ordinal))
                {
                    failures.Add("The incomplete editor projection changed the library settings version.");
                }
                if (!string.Equals(beforeRules, await ReadRuleEvidenceAsync(fieldKey, libraryId), StringComparison.Ordinal))
                {
                    failures.Add("The incomplete editor projection changed or removed a persisted format rule.");
                }
                if (auditBeforeBrowser != await ReadSettingsCourseAuditHighWatermarkAsync(libraryId) ||
                    outboxBeforeBrowser != await ReadCountAsync("SELECT COUNT(*) FROM [asap].[EmailOutbox];") ||
                    eventsBeforeBrowser != await ReadCountAsync("SELECT COUNT(*) FROM [asap].[TitleRequestEvent];") ||
                    sessionsBeforeBrowser != await ReadCountAsync("SELECT COUNT(*) FROM [asap].[PatronSession];"))
                {
                    failures.Add("The incomplete editor projection changed audit, session, title-event, or email-outbox evidence.");
                }
            }

            // Restore the isolated witness rows before testing intentional whole-set replacements.
            await ExecuteNonQueryAsync("""
                DELETE FROM [asap].[MaterialFormatCustomFieldRule]
                WHERE [LibraryOrganizationId] = @organizationId AND [PatronCustomFieldId] =
                    (SELECT [Id] FROM [asap].[PatronCustomField]
                     WHERE [LibraryOrganizationId] = @organizationId AND [FieldKey] = @fieldKey);
                DELETE FROM [asap].[MaterialFormatOverride] WHERE [LibraryOrganizationId] = @organizationId;
                DELETE FROM [asap].[AdministrativeAudit] WHERE [OrganizationId] = @organizationId;
                DECLARE @fieldId bigint = (SELECT [Id] FROM [asap].[PatronCustomField]
                    WHERE [LibraryOrganizationId] = @organizationId AND [FieldKey] = @fieldKey);
                DECLARE @formatId bigint = (SELECT [Id] FROM [asap].[MaterialFormat]
                    WHERE [OwnerOrganizationId] = 1 AND [Code] = @formatCode);
                DECLARE @bookId bigint = (SELECT [Id] FROM [asap].[MaterialFormat]
                    WHERE [OwnerOrganizationId] = 1 AND [Code] = N'book');
                INSERT INTO [asap].[MaterialFormatCustomFieldRule]
                    ([LibraryOrganizationId], [MaterialFormatId], [PatronCustomFieldId], [Mode], [LabelOverride])
                VALUES (@organizationId, @bookId, @fieldId, N'optional', N'Visible book rule'),
                       (@organizationId, @formatId, @fieldId, N'optional', N'Omitted format rule');
                """, ("@organizationId", libraryId), ("@fieldKey", fieldKey),
                ("@formatCode", formatCode));

            string compatibilityVersion;
            using (var current = await ReadSettingsDocumentAsync(client, libraryId.ToString()))
            {
                compatibilityVersion = current.RootElement.GetProperty("version").GetString()!;
            }
            var intentionalPartial = new JsonObject
            {
                ["orgId"] = libraryId.ToString(),
                ["version"] = compatibilityVersion,
                ["formatRules"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["code"] = "book",
                        ["customFields"] = new JsonObject
                        {
                            [fieldKey] = new JsonObject
                            {
                                ["mode"] = "optional",
                                ["labelOverride"] = "Visible book rule"
                            }
                        }
                    }
                }
            };
            using (var acceptedPartial = await PostSettingsJsonAsync(client, intentionalPartial.ToJsonString()))
            {
                Assert.AreEqual(HttpStatusCode.OK, acceptedPartial.StatusCode,
                    "An intentional authoritative partial replacement remains a supported API contract.");
            }
            using (var partialResult = JsonDocument.Parse(await ReadRuleEvidenceAsync(fieldKey, libraryId)))
            {
                Assert.AreEqual(1, partialResult.RootElement.GetArrayLength());
                Assert.AreEqual("book", partialResult.RootElement[0].GetProperty("code").GetString());
            }

            using var afterPartial = await ReadSettingsDocumentAsync(client, libraryId.ToString());
            var clearPayload = new JsonObject
            {
                ["orgId"] = libraryId.ToString(),
                ["version"] = afterPartial.RootElement.GetProperty("version").GetString(),
                ["formatRules"] = new JsonArray()
            };
            using (var acceptedEmpty = await PostSettingsJsonAsync(client, clearPayload.ToJsonString()))
            {
                Assert.AreEqual(HttpStatusCode.OK, acceptedEmpty.StatusCode,
                    "An explicit empty replacement remains the intentional whole-set clear operation.");
            }
            Assert.AreEqual(0, await ReadCountWithParameterAsync("""
                SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] ruleRow
                JOIN [asap].[PatronCustomField] field ON field.[Id] = ruleRow.[PatronCustomFieldId]
                WHERE ruleRow.[LibraryOrganizationId] = @organizationId AND field.[FieldKey] = @fieldKey;
                """, ("@organizationId", libraryId), ("@fieldKey", fieldKey)));
            Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
        }
        finally
        {
            await ExecuteNonQueryAsync("""
                DELETE FROM [asap].[MaterialFormatCustomFieldRule] WHERE [LibraryOrganizationId] = @organizationId;
                DELETE FROM [asap].[MaterialFormatOverride] WHERE [LibraryOrganizationId] = @organizationId;
                DELETE FROM [asap].[PatronCustomFieldOption]
                WHERE [PatronCustomFieldId] IN
                    (SELECT [Id] FROM [asap].[PatronCustomField]
                     WHERE [LibraryOrganizationId] = @organizationId AND [FieldKey] = @fieldKey);
                DELETE FROM [asap].[PatronCustomField]
                WHERE [LibraryOrganizationId] = @organizationId AND [FieldKey] = @fieldKey;
                DELETE FROM [asap].[MaterialFormat]
                WHERE [OwnerOrganizationId] = 1 AND [Code] = @formatCode;
                DELETE FROM [asap].[AdministrativeAudit] WHERE [OrganizationId] = @organizationId;
                DELETE FROM [asap].[Organization] WHERE [Id] = @organizationId;
                """, ("@organizationId", libraryId), ("@fieldKey", fieldKey),
                ("@formatCode", formatCode));
        }
    }

    private static async Task<HttpResponseMessage> PostSettingsAtPathAsync(HttpClient client, string path, string json) =>
        await client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));

    private static JsonArray BuildFormatRuleRows(
        JsonElement formats,
        string? omitCode = null,
        string? clearCustomRulesForCode = null)
    {
        var rows = new JsonArray();
        foreach (var format in formats.EnumerateArray())
        {
            var code = format.GetProperty("code").GetString()!;
            if (string.Equals(code, omitCode, StringComparison.Ordinal))
            {
                continue;
            }
            var customFields = JsonNode.Parse(format.GetProperty("customFields").GetRawText())!.AsObject();
            if (string.Equals(code, clearCustomRulesForCode, StringComparison.Ordinal))
            {
                customFields = new JsonObject();
            }
            rows.Add(new JsonObject { ["code"] = code, ["customFields"] = customFields });
        }
        return rows;
    }

    private static string SerializeSystemSettingsOutsideSubmittedPatronAndFormats(JsonElement stored)
    {
        var snapshot = JsonNode.Parse(stored.GetRawText())!.AsObject();
        snapshot.Remove("formats");
        snapshot.Remove("configuredSystem");
        snapshot.Remove("formatRules");
        snapshot.Remove("patron");
        return snapshot.ToJsonString();
    }

    private static async Task<(string? MessageBehavior, string? Message, byte[] RowVersion)> ReadSystemFormatEvidenceAsync(string code)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand("""
            SELECT [MessageBehavior], [Message], [RowVersion]
            FROM [asap].[MaterialFormat]
            WHERE [OwnerOrganizationId] = 1 AND [Code] = @code;
            """, connection);
        command.Parameters.AddWithValue("@code", code);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException($"System material format '{code}' is missing.");
        }
        return (reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1), (byte[])reader[2]);
    }

    private static async Task<string> ReadRequiredSelectResetSnapshotAsync(
        HttpClient client,
        string formatCode,
        string fieldKey,
        string barcode)
    {
        using var settings = await ReadSettingsDocumentAsync(client, "2");
        var data = await ReadSqlJsonAsync("""
            SELECT
                JSON_QUERY((SELECT format.[Id] AS [id], format.[Code] AS [code], format.[IsEnabled] AS [systemEnabled],
                        overrideRow.[Id] AS [overrideId], overrideRow.[IsEnabled] AS [overrideEnabled],
                        overrideRow.[RowVersion] AS [overrideRowVersion]
                    FROM [asap].[MaterialFormat] format
                    LEFT JOIN [asap].[MaterialFormatOverride] overrideRow
                        ON overrideRow.[MaterialFormatId] = format.[Id] AND overrideRow.[LibraryOrganizationId] = 2
                    WHERE format.[OwnerOrganizationId] = 1 AND format.[Code] = @formatCode
                    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)) AS [format],
                JSON_QUERY((SELECT field.[Id] AS [id], field.[FieldKey] AS [fieldKey], field.[FieldType] AS [fieldType],
                        field.[Label] AS [label], field.[IsEnabled] AS [enabled], field.[SortOrder] AS [sortOrder],
                        field.[RowVersion] AS [rowVersion]
                    FROM [asap].[PatronCustomField] field
                    WHERE field.[LibraryOrganizationId] = 2 AND field.[FieldKey] = @fieldKey
                    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)) AS [field],
                JSON_QUERY((SELECT optionRow.[OptionKey] AS [key], optionRow.[Label] AS [label],
                        optionRow.[IsEnabled] AS [enabled], optionRow.[SortOrder] AS [sortOrder]
                    FROM [asap].[PatronCustomFieldOption] optionRow
                    JOIN [asap].[PatronCustomField] field ON field.[Id] = optionRow.[PatronCustomFieldId]
                    WHERE field.[LibraryOrganizationId] = 2 AND field.[FieldKey] = @fieldKey
                    ORDER BY optionRow.[SortOrder], optionRow.[Id]
                    FOR JSON PATH)) AS [options],
                JSON_QUERY((SELECT format.[Code] AS [code], field.[FieldKey] AS [fieldKey],
                        ruleRow.[Mode] AS [mode], ruleRow.[LabelOverride] AS [labelOverride],
                        ruleRow.[RowVersion] AS [rowVersion]
                    FROM [asap].[MaterialFormatCustomFieldRule] ruleRow
                    JOIN [asap].[MaterialFormat] format ON format.[Id] = ruleRow.[MaterialFormatId]
                    JOIN [asap].[PatronCustomField] field ON field.[Id] = ruleRow.[PatronCustomFieldId]
                    WHERE ruleRow.[LibraryOrganizationId] = 2 AND field.[FieldKey] = @fieldKey
                    FOR JSON PATH)) AS [rules],
                JSON_QUERY((SELECT session.[Barcode] AS [barcode], session.[EffectiveOrganizationId] AS [effectiveOrganizationId],
                        session.[CreatedUtc] AS [createdUtc], session.[ExpiresUtc] AS [expiresUtc], session.[RevokedUtc] AS [revokedUtc]
                    FROM [asap].[PatronSession] session WHERE session.[Barcode] = @barcode
                    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)) AS [session]
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
            """, ("@formatCode", formatCode), ("@fieldKey", fieldKey), ("@barcode", barcode));
        var snapshot = new JsonObject
        {
            ["version"] = settings.RootElement.GetProperty("version").GetString(),
            ["sql"] = JsonNode.Parse(data),
            ["auditHighWatermark"] = await ReadAuditHighWatermarkAsync(),
            ["patronSessionCount"] = await ReadCountAsync("SELECT COUNT(*) FROM [asap].[PatronSession];"),
            ["titleRequestEventCount"] = await ReadCountAsync("SELECT COUNT(*) FROM [asap].[TitleRequestEvent];"),
            ["emailOutboxCount"] = await ReadCountAsync("SELECT COUNT(*) FROM [asap].[EmailOutbox];")
        };
        return snapshot.ToJsonString();
    }

    private static async Task<int> ReserveSettingsCourseLibraryIdAsync()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var candidate = Random.Shared.Next(1_000_000, 2_000_000);
            if (await ReadCountWithParameterAsync(
                    "SELECT COUNT(*) FROM [asap].[Organization] WHERE [Id] = @organizationId;",
                    ("@organizationId", candidate)) == 0)
            {
                return candidate;
            }
        }
        throw new InvalidOperationException("Could not reserve an isolated settings-course library ID.");
    }

    private static async Task<long> ReadSettingsCourseAuditHighWatermarkAsync(int organizationId)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(
            "SELECT ISNULL(MAX([Id]), 0) FROM [asap].[AdministrativeAudit] WHERE [OrganizationId] = @organizationId;",
            connection);
        command.Parameters.AddWithValue("@organizationId", organizationId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ReadRuleEvidenceAsync(string fieldKey, int organizationId = 2) =>
        await ReadSqlJsonAsync("""
            SELECT format.[Code] AS [code], field.[FieldKey] AS [fieldKey], ruleRow.[Mode] AS [mode],
                ruleRow.[LabelOverride] AS [labelOverride], ruleRow.[RowVersion] AS [rowVersion]
            FROM [asap].[MaterialFormatCustomFieldRule] ruleRow
            JOIN [asap].[MaterialFormat] format ON format.[Id] = ruleRow.[MaterialFormatId]
            JOIN [asap].[PatronCustomField] field ON field.[Id] = ruleRow.[PatronCustomFieldId]
            WHERE ruleRow.[LibraryOrganizationId] = @organizationId AND field.[FieldKey] = @fieldKey
            ORDER BY format.[Code]
            FOR JSON PATH;
            """, ("@fieldKey", fieldKey), ("@organizationId", organizationId));

    private static async Task<string> ReadSqlJsonAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)
            ?? "null";
    }

    private static async Task<int> ReadCountWithParameterAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
