using System.Diagnostics;
using System.Text.Json;

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
                VALUES (@library, @key, N'text', N'Saved collision field', 1, 9870);
                DECLARE @fieldId bigint = SCOPE_IDENTITY();
                INSERT INTO [asap].[MaterialFormatCustomFieldRule]
                    ([LibraryOrganizationId], [MaterialFormatId], [PatronCustomFieldId], [Mode], [LabelOverride])
                VALUES (@library, @formatId, @fieldId, N'required', N'Saved rule witness');
                """, ("@library", libraryId), ("@code", formatCode), ("@key", fieldKey));

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
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the format-rule draft browser regression.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.AreEqual(0, process.ExitCode, $"{await stdout}{Environment.NewLine}{await stderr}");
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
        }
        finally
        {
            await ExecuteNonQueryAsync("""
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
