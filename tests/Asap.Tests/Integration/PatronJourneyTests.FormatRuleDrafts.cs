using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [TestCategory(TestCategories.Browser)]
    public async Task FormatRuleDraftIdentitiesSurviveCombinedFormatAndFieldCreationThroughSqlAndReload()
    {
        factory!.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        var actor = await ReadConfiguredSuperAdminAsync();
        using var client = factory.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        var libraryId = await ReserveSettingsCourseLibraryIdAsync();
        const string historicalSelections = """
            { "field_2" : { "label" : "Original select label", "type" : "select",
              "value" : "selected-choice", "displayValue" : "Original selected display" },
              "retired_history" : { "label" : "Retained history", "type" : "text", "value" : "Exact accepted history" } }
            """;
        async Task<(string Requests, string Events, string Owned)> ReadHistoricalRowsAsync()
        {
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                  (SELECT * FROM [asap].[TitleRequest] WHERE [LibraryOrganizationId] = @library
                   ORDER BY [Id] FOR JSON PATH, INCLUDE_NULL_VALUES),
                  (SELECT eventRow.* FROM [asap].[TitleRequestEvent] eventRow
                   JOIN [asap].[TitleRequest] requestRow ON requestRow.[Id] = eventRow.[TitleRequestId]
                   WHERE requestRow.[LibraryOrganizationId] = @library
                   ORDER BY eventRow.[Id] FOR JSON PATH, INCLUDE_NULL_VALUES),
                  (SELECT
                    JSON_QUERY((SELECT * FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = @library
                      ORDER BY [Id] FOR JSON PATH, INCLUDE_NULL_VALUES)) AS [formats],
                    JSON_QUERY((SELECT * FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = @library
                      ORDER BY [Id] FOR JSON PATH, INCLUDE_NULL_VALUES)) AS [fields],
                    JSON_QUERY((SELECT optionRow.* FROM [asap].[PatronCustomFieldOption] optionRow
                      JOIN [asap].[PatronCustomField] field ON field.[Id] = optionRow.[PatronCustomFieldId]
                      WHERE field.[LibraryOrganizationId] = @library
                      ORDER BY optionRow.[Id] FOR JSON PATH, INCLUDE_NULL_VALUES)) AS [options],
                    JSON_QUERY((SELECT * FROM [asap].[MaterialFormatCustomFieldRule] WHERE [LibraryOrganizationId] = @library
                      ORDER BY [Id] FOR JSON PATH, INCLUDE_NULL_VALUES)) AS [rules],
                    JSON_QUERY((SELECT * FROM [asap].[FormatAutoClaimRule] WHERE [LibraryOrganizationId] = @library
                      ORDER BY [Id] FOR JSON PATH, INCLUDE_NULL_VALUES)) AS [claims],
                    JSON_QUERY((SELECT * FROM [asap].[EmailTemplate] WHERE [OrganizationId] = @library AND [IsCustom] = 1
                      ORDER BY [Id] FOR JSON PATH, INCLUDE_NULL_VALUES)) AS [templates]
                   FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
                """;
            command.Parameters.AddWithValue("@library", libraryId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
        }
        try
        {
            await ExecuteNonQueryAsync("""
                INSERT INTO [asap].[Organization]
                    ([Id], [DisplayName], [Abbreviation], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
                VALUES (@library, N'Format rule draft library', N'FRD', 2, 1, 1);
                """, ("@library", libraryId));
            using var before = await ReadSettingsDocumentAsync(client, libraryId.ToString());
            var formatCount = before.RootElement.GetProperty("effective").GetProperty("formats").GetArrayLength();
            var formatCode = $"custom_{formatCount + 2}";
            const string fieldKey = "field_2";
            await ExecuteNonQueryAsync("""
                INSERT INTO [asap].[MaterialFormat]
                    ([OwnerOrganizationId], [Code], [Label], [SortOrder], [IsEnabled], [CreatedUtc], [UpdatedUtc])
                VALUES (@library, @code, N'Saved collision format', 9870, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                DECLARE @formatId bigint = SCOPE_IDENTITY();
                INSERT INTO [asap].[PatronCustomField]
                    ([LibraryOrganizationId], [FieldKey], [FieldType], [Label], [IsEnabled], [SortOrder])
                VALUES (@library, @key, N'select', N'Original select label', 1, 9870);
                DECLARE @fieldId bigint = SCOPE_IDENTITY();
                INSERT INTO [asap].[PatronCustomFieldOption]
                    ([PatronCustomFieldId], [OptionKey], [Label], [IsEnabled], [SortOrder])
                VALUES (@fieldId, N'selected-choice', N'Original selected display', 1, 10),
                       (@fieldId, N'other-choice', N'Other choice', 1, 20);
                INSERT INTO [asap].[MaterialFormatCustomFieldRule]
                    ([LibraryOrganizationId], [MaterialFormatId], [PatronCustomFieldId], [Mode], [LabelOverride])
                VALUES (@library, @formatId, @fieldId, N'required', N'Saved rule witness');
                INSERT INTO [asap].[FormatAutoClaimRule]
                    ([LibraryOrganizationId], [MaterialFormatId], [StaffUserId], [IsActive], [CreatedUtc])
                VALUES (@library, @formatId, @staff, 1, '2030-01-01T00:00:00');
                DECLARE @claimId bigint = SCOPE_IDENTITY();
                INSERT INTO [asap].[PublicationOptionSet] ([OrganizationId]) VALUES (@library);
                INSERT INTO [asap].[PublicationOption] ([OrganizationId], [OptionKey], [Label], [IsEnabled], [SortOrder])
                VALUES (@library, N'historical-publication', N'Historical publication label', 1, 10),
                       (@library, N'other-publication', N'Other publication', 1, 20);
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [Publication], [CustomFieldsJson], [AutoHold],
                     [MaterialFormatId], [Status], [Notes], [ClaimedByStaffUserId], [ClaimedByDisplayName],
                     [ClaimedAtUtc], [ClaimType], [ClaimRuleId], [CreatedUtc], [UpdatedUtc])
                VALUES (@library, N'SYNTHETIC-HISTORY', N'Settings history witness', N'Historical publication label',
                    @snapshot, 0, @formatId, N'suggestion', N'Accepted notes' + NCHAR(13) + NCHAR(10) + N'Unchanged',
                    @staff, N'Synthetic selector', '2030-01-01T00:00:00', N'automatic_format_rule', @claimId,
                    '2030-01-01T00:00:00', '2030-01-01T00:00:00');
                DECLARE @requestId bigint = SCOPE_IDENTITY();
                INSERT INTO [asap].[TitleRequestEvent]
                    ([TitleRequestId], [EventType], [Status], [ActorType], [ActorName], [Message], [MetadataJson], [CreatedUtc])
                VALUES (@requestId, N'request_submitted', N'suggestion', N'patron', N'Library Patron',
                    N'Accepted request history', @snapshot, '2030-01-01T00:00:00');
                """, ("@library", libraryId), ("@code", formatCode), ("@key", fieldKey),
                ("@staff", actor.Id), ("@snapshot", historicalSelections));
            var historicalBefore = await ReadHistoricalRowsAsync();
            using var historicalRequests = JsonDocument.Parse(historicalBefore.Requests);
            using var historicalEvents = JsonDocument.Parse(historicalBefore.Events);
            using var initialOwned = JsonDocument.Parse(historicalBefore.Owned);
            Assert.AreEqual(1, historicalRequests.RootElement.GetArrayLength(), "The history witness must contain a real stored request.");
            Assert.AreEqual(1, historicalEvents.RootElement.GetArrayLength(), "The history witness must contain accepted request history.");
            var historicalRequest = historicalRequests.RootElement[0];
            var historicalRequestId = historicalRequest.GetProperty("Id").GetInt64();
            var historicalFormatId = historicalRequest.GetProperty("MaterialFormatId").GetInt64();
            var historicalFieldId = initialOwned.RootElement.GetProperty("fields")[0].GetProperty("Id").GetInt64();
            Assert.AreEqual(historicalSelections, historicalRequest.GetProperty("CustomFieldsJson").GetString());
            Assert.AreEqual("Historical publication label", historicalRequest.GetProperty("Publication").GetString());
            var requestBefore = await ReadTitleRequestSnapshotAsync(historicalRequestId);

            var repositoryRoot = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
            var artifacts = Path.Combine(repositoryRoot, ".artifacts", "browser", $"format-rule-drafts-{Guid.NewGuid():N}");
            Directory.CreateDirectory(artifacts);
            var startInfo = new ProcessStartInfo
            {
                FileName = "node",
                WorkingDirectory = repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(Path.Combine(repositoryRoot, "tests", "browser", "settings.cjs"));
            startInfo.ArgumentList.Add(client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            startInfo.ArgumentList.Add(artifacts);
            startInfo.ArgumentList.Add(actor.Id.ToString());
            startInfo.ArgumentList.Add(actor.EntraTenantId.ToString());
            startInfo.ArgumentList.Add(actor.AuthenticationEmail);
            startInfo.ArgumentList.Add("library-format-rule-drafts");
            startInfo.ArgumentList.Add(libraryId.ToString());
            startInfo.ArgumentList.Add(formatCode);
            var (exitCode, stdout, stderr) = await OwnedTestProcess.RunAsync(startInfo, TestContext.CancellationToken);
            Assert.AreEqual(0, exitCode, $"{stdout}{Environment.NewLine}{stderr}");
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(artifacts, "settings-browser-results.json")));
            Assert.AreEqual(0, report.RootElement.GetProperty("externalRequests").GetInt32());
            Assert.AreEqual(0, report.RootElement.GetProperty("pageErrors").GetArrayLength());
            Assert.HasCount(2, report.RootElement.GetProperty("states").EnumerateArray().ToArray());
            var evidence = report.RootElement.GetProperty("formatRuleDrafts");
            Assert.IsTrue(evidence.GetProperty("roundTrip").GetBoolean());
            var recovery = report.RootElement.GetProperty("formatRuleDraftSaveRecovery");
            Assert.HasCount(2, recovery.GetProperty("rejected").EnumerateArray().ToArray());
            Assert.IsTrue(recovery.GetProperty("rejected").EnumerateArray()
                .All(item => item.GetProperty("status").GetInt32() == 400));
            Assert.HasCount(3, recovery.GetProperty("attemptedPayloads").EnumerateArray().ToArray());
            Assert.IsTrue(recovery.GetProperty("rejectedStateUnchanged").GetBoolean());
            Assert.IsTrue(recovery.GetProperty("retryPayloadStable").GetBoolean());
            Assert.AreEqual(200, recovery.GetProperty("committedResponse").GetProperty("status").GetInt32());
            Assert.IsTrue(recovery.GetProperty("committedIdentitiesVerified").GetBoolean());
            Assert.IsTrue(recovery.GetProperty("uncertainReplayBlocked").GetBoolean());
            Assert.IsTrue(recovery.GetProperty("guardedReloadReconciled").GetBoolean());
            Assert.AreEqual(1, await ReadCountWithParameterAsync("""
                SELECT COUNT(*) FROM [asap].[AdministrativeAudit]
                WHERE [OrganizationId] = @library AND [Action] = N'library_settings_updated';
                """, ("@library", libraryId)), "Only the committed attempt writes successful system history.");
            Assert.AreEqual(fieldKey, evidence.GetProperty("savedKey").GetString());
            var newCode = evidence.GetProperty("newCode").GetString()!;
            var newKey = evidence.GetProperty("newKey").GetString()!;
            var publicationKey = evidence.GetProperty("publicationKey").GetString()!;
            var templateKey = evidence.GetProperty("templateKey").GetString()!;
            Assert.AreEqual(1, await ReadCountWithParameterAsync("""
                SELECT COUNT(*) FROM [asap].[PublicationOption]
                WHERE [OrganizationId] = @library AND [OptionKey] = @key
                  AND [Label] = N'Draft publication timing' AND [IsEnabled] = 0;
                """, ("@library", libraryId), ("@key", publicationKey)));
            Assert.AreEqual(1, await ReadCountWithParameterAsync("""
                SELECT COUNT(*) FROM [asap].[EmailTemplate]
                WHERE [OrganizationId] = @library AND [TemplateKey] = @key AND [IsCustom] = 1
                  AND [SourceTemplateId] IS NULL AND [DisplayName] = N'Draft rejection label';
                """, ("@library", libraryId), ("@key", templateKey)));
            foreach (var option in evidence.GetProperty("optionIds").EnumerateArray())
            {
                Assert.AreEqual(1, await ReadCountWithParameterAsync("""
                    SELECT COUNT(*) FROM [asap].[PatronCustomFieldOption] optionRow
                    JOIN [asap].[PatronCustomField] field ON field.[Id] = optionRow.[PatronCustomFieldId]
                    WHERE field.[LibraryOrganizationId] = @library AND field.[FieldKey] = @key
                      AND optionRow.[OptionKey] = @option AND optionRow.[Label] = N'Same selection';
                    """, ("@library", libraryId), ("@key", newKey), ("@option", option.GetString()!)));
            }
            Assert.AreEqual(1, await ReadCountWithParameterAsync("""
                SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] ruleRow
                JOIN [asap].[MaterialFormat] format ON format.[Id] = ruleRow.[MaterialFormatId]
                JOIN [asap].[PatronCustomField] field ON field.[Id] = ruleRow.[PatronCustomFieldId]
                WHERE ruleRow.[LibraryOrganizationId] = @library AND format.[Code] = @code
                  AND field.[FieldKey] = @key AND ruleRow.[Mode] = N'required'
                  AND ruleRow.[LabelOverride] = @label;
                """, ("@library", libraryId), ("@code", formatCode), ("@key", fieldKey), ("@label", "Saved rule witness")),
                "The original required rule must remain on its original format after the actual combined POST.");
            Assert.AreEqual(1, await ReadCountWithParameterAsync("""
                SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] ruleRow
                JOIN [asap].[MaterialFormat] format ON format.[Id] = ruleRow.[MaterialFormatId]
                JOIN [asap].[PatronCustomField] field ON field.[Id] = ruleRow.[PatronCustomFieldId]
                WHERE ruleRow.[LibraryOrganizationId] = @library AND format.[Code] = @code
                  AND field.[FieldKey] = @key AND ruleRow.[Mode] = N'required'
                  AND ruleRow.[LabelOverride] = N'New rule witness';
                """, ("@library", libraryId), ("@code", newCode), ("@key", newKey)));
            Assert.AreEqual(2, await ReadCountWithParameterAsync(
                "SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] WHERE [LibraryOrganizationId] = @library;",
                ("@library", libraryId)), "The new format must not acquire the original format's saved rule.");
            var historicalAfterSave = await ReadHistoricalRowsAsync();
            await AssertTitleRequestUnchangedAsync(historicalRequestId, requestBefore);
            Assert.AreEqual(historicalBefore.Requests, historicalAfterSave.Requests,
                "Settings rename/state/order edits preserve the complete stored request, accepted publication, raw selections and rowversion byte-for-byte.");
            Assert.AreEqual(historicalBefore.Events, historicalAfterSave.Events,
                "Settings edits cannot rewrite or append accepted request history.");
            using var savedOwned = JsonDocument.Parse(historicalAfterSave.Owned);
            Assert.AreEqual(initialOwned.RootElement.GetProperty("claims").GetRawText(),
                savedOwned.RootElement.GetProperty("claims").GetRawText(), "The existing auto-claim identity and history remain unchanged.");

            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
            using var settingsBeforeReset = await ReadSettingsDocumentAsync(client, libraryId.ToString());
            var stored = settingsBeforeReset.RootElement.GetProperty("stored");
            var selectedDefinition = stored.GetProperty("customFields").EnumerateArray()
                .Single(item => item.GetProperty("key").GetString() == fieldKey);
            Assert.AreEqual(historicalFieldId.ToString(), selectedDefinition.GetProperty("id").GetString());
            Assert.AreEqual("Renamed historical field", selectedDefinition.GetProperty("label").GetString());
            Assert.IsFalse(selectedDefinition.GetProperty("enabled").GetBoolean());
            var selectedOption = selectedDefinition.GetProperty("options").EnumerateArray()
                .Single(item => item.GetProperty("id").GetString() == "selected-choice");
            Assert.AreEqual("Renamed historical selection", selectedOption.GetProperty("label").GetString());
            Assert.IsFalse(selectedOption.GetProperty("enabled").GetBoolean());
            Assert.AreEqual(historicalFormatId.ToString(), settingsBeforeReset.RootElement.GetProperty("effective")
                .GetProperty("formats").EnumerateArray().Single(item => item.GetProperty("code").GetString() == formatCode)
                .GetProperty("id").GetString());
            Assert.AreEqual(1, await ReadCountWithParameterAsync(
                "SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] WHERE [LibraryOrganizationId] = @library;",
                ("@library", libraryId)), "The browser must establish a real inherited format override before reset.");

            using var reset = await client.PostAsJsonAsync(
                $"/api/asap/staff/settings/reset?organizationId={libraryId}",
                new { version = settingsBeforeReset.RootElement.GetProperty("version").GetString() });
            Assert.AreEqual(HttpStatusCode.OK, reset.StatusCode, await reset.Content.ReadAsStringAsync());
            using var resetBody = JsonDocument.Parse(await reset.Content.ReadAsStringAsync());
            Assert.AreEqual("reset", resetBody.RootElement.GetProperty("code").GetString());
            using var settingsAfterReset = await ReadSettingsDocumentAsync(client, libraryId.ToString());
            Assert.IsFalse(settingsAfterReset.RootElement.GetProperty("stored").GetProperty("libraryOverride")
                .GetProperty("publicationOptions").GetProperty("exists").GetBoolean());
            Assert.AreEqual(0, await ReadCountWithParameterAsync(
                "SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] WHERE [LibraryOrganizationId] = @library;",
                ("@library", libraryId)));
            var historicalAfterReset = await ReadHistoricalRowsAsync();
            await AssertTitleRequestUnchangedAsync(historicalRequestId, requestBefore);
            Assert.AreEqual(historicalBefore.Requests, historicalAfterReset.Requests,
                "Resetting the publication list and inherited values preserves historical request selections and references exactly.");
            Assert.AreEqual(historicalBefore.Events, historicalAfterReset.Events);
            Assert.AreEqual(historicalAfterSave.Owned, historicalAfterReset.Owned,
                "Inherited reset preserves every owned format/field/option/rule/claim/template row, ID, rowversion and history.");
            static string Digest(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(value)));
            await File.WriteAllTextAsync(Path.Combine(artifacts, "historical-selections.json"), JsonSerializer.Serialize(new
            {
                historicalRequestId,
                historicalFormatId,
                historicalFieldId,
                fieldKey,
                optionKey = "selected-choice",
                publicationOptionKey = "historical-publication",
                acceptedPublication = "Historical publication label",
                storedSelectionJson = historicalSelections,
                requestBefore = Digest(historicalBefore.Requests),
                requestAfterSave = Digest(historicalAfterSave.Requests),
                requestAfterReset = Digest(historicalAfterReset.Requests),
                eventsBefore = Digest(historicalBefore.Events),
                eventsAfterSave = Digest(historicalAfterSave.Events),
                eventsAfterReset = Digest(historicalAfterReset.Events),
                ownedBeforeReset = Digest(historicalAfterSave.Owned),
                ownedAfterReset = Digest(historicalAfterReset.Owned)
            }));
        }
        finally
        {
            await ExecuteNonQueryAsync("""
                DELETE FROM [asap].[TitleRequest] WHERE [LibraryOrganizationId] = @library;
                DELETE FROM [asap].[FormatAutoClaimRule] WHERE [LibraryOrganizationId] = @library;
                DELETE FROM [asap].[MaterialFormatCustomFieldRule] WHERE [LibraryOrganizationId] = @library;
                DELETE FROM [asap].[MaterialFormatOverride] WHERE [LibraryOrganizationId] = @library;
                DELETE FROM [asap].[PatronCustomFieldOption] WHERE [PatronCustomFieldId] IN
                    (SELECT [Id] FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = @library);
                DELETE FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = @library;
                DELETE FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = @library;
                DELETE FROM [asap].[EmailTemplate] WHERE [OrganizationId] = @library;
                DELETE FROM [asap].[PublicationOption] WHERE [OrganizationId] = @library;
                DELETE FROM [asap].[PublicationOptionSet] WHERE [OrganizationId] = @library;
                DELETE FROM [asap].[AdministrativeAudit] WHERE [OrganizationId] = @library;
                DELETE FROM [asap].[Organization] WHERE [Id] = @library;
                """, ("@library", libraryId));
        }
    }
}
