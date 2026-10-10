using System.Diagnostics;
using System.Text.Json;
using Asap.Web.Infrastructure.Testing;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task LibrarySettingsProductionGetEditorPostPreservesOverrideAndOwnedMetadata()
    {
        factory!.UseKestrel(0);
        var actor = await ReadConfiguredSuperAdminAsync();
        using var client = factory.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        var libraryId = await ReserveSettingsCourseLibraryIdAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var beforeOutbox = await ReadCountAsync("SELECT COUNT(*) FROM [asap].[EmailOutbox];");
        var beforeEvents = await ReadCountAsync("SELECT COUNT(*) FROM [asap].[TitleRequestEvent];");
        try
        {
            await ExecuteNonQueryAsync("""
                INSERT INTO [asap].[Organization]
                    ([Id], [DisplayName], [Abbreviation], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
                VALUES (@org, N'Override metadata witness', N'OMW', 2, 1, 1);
                INSERT INTO [asap].[ExternalSearchProvider]
                    ([OrganizationId], [ProviderKey], [Label], [UrlTemplate], [IsEnabled], [SortOrder])
                VALUES (1, N'audit_provider_a_' + @suffix, N'System provider A', N'https://catalog.example.org/a?q={query}', 1, 9811),
                       (1, N'audit_provider_b_' + @suffix, N'System provider B', N'https://catalog.example.org/b?q={query}', 1, 9813);
                INSERT INTO [asap].[MaterialFormat]
                    ([OwnerOrganizationId], [Code], [Label], [SortOrder], [IsEnabled], [CreatedUtc], [UpdatedUtc])
                VALUES (1, N'audit_format_a_' + @suffix, N'System format A', 9707, 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                       (1, N'audit_format_b_' + @suffix, N'System format B', 9719, 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                       (1, N'audit_inherited_' + @suffix, N'Inherited format', 9723, 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                       (@org, N'audit_custom_' + @suffix, N'Owned format', 9741, 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                       (@org, N'audit_sparse_' + @suffix, N'Sparse owned format', 9751, 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                       (@org, N'audit_retired_' + @suffix, N'Retired owned format', 9757, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
                """, ("@org", libraryId), ("@suffix", suffix));
            using (var initial = await ReadSettingsDocumentAsync(client, libraryId.ToString()))
            {
                var initialFormats = initial.RootElement.GetProperty("stored").GetProperty("formats");
                var formatA = initialFormats.EnumerateArray().Single(item => item.GetProperty("code").GetString() == $"audit_format_a_{suffix}");
                var formatB = initialFormats.EnumerateArray().Single(item => item.GetProperty("code").GetString() == $"audit_format_b_{suffix}");
                using var saved = await SaveSettingsDocumentAsync(client, initial.RootElement, libraryId.ToString(),
                    new Dictionary<string, object?>
                    {
                        ["providers"] = new object[]
                        {
                            new { key = $"audit_provider_a_{suffix}", label = "Library System provider A", overridden = true },
                            new { key = $"audit_provider_b_{suffix}", label = "Library System provider B", enabled = false,
                                urlTemplate = "https://catalog.example.org/library-b?q={query}", overridden = true }
                        },
                        ["formats"] = new object[]
                        {
                            new { id = formatA.GetProperty("id").GetString(), code = $"audit_format_a_{suffix}", ownerOrganizationId = 1,
                                version = formatA.GetProperty("version").GetString(), label = "Library System format A", sortOrder = 9708, overridden = true },
                            new { id = formatB.GetProperty("id").GetString(), code = $"audit_format_b_{suffix}", ownerOrganizationId = 1,
                                version = formatB.GetProperty("version").GetString(), label = "Library System format B", sortOrder = 9720,
                                isEnabled = false, messageBehavior = "message", message = "Library message B",
                                author = new { mode = "required", label = "Library creator B" }, overridden = true }
                        }
                    });
                Assert.AreEqual("saved", saved.RootElement.GetProperty("code").GetString());
            }
            using (var system = await ReadSettingsDocumentAsync(client, "system"))
            {
                var formatB = system.RootElement.GetProperty("stored").GetProperty("formats").EnumerateArray()
                    .Single(item => item.GetProperty("code").GetString() == $"audit_format_b_{suffix}");
                using var converged = await SaveSettingsDocumentAsync(client, system.RootElement, "system",
                    new Dictionary<string, object?>
                    {
                        ["providers"] = new[] { new { key = $"audit_provider_b_{suffix}", label = "Library System provider B",
                            enabled = false, urlTemplate = "https://catalog.example.org/library-b?q={query}" } },
                        ["formats"] = new[] { new { id = formatB.GetProperty("id").GetString(), code = $"audit_format_b_{suffix}",
                            ownerOrganizationId = 1, version = formatB.GetProperty("version").GetString(),
                            label = "Library System format B", sortOrder = 9720, isEnabled = false, messageBehavior = "message",
                            message = "Library message B", author = new { mode = "required", label = "Library creator B" } } }
                    });
                Assert.AreEqual("saved", converged.RootElement.GetProperty("code").GetString());
            }
            using var before = await ReadSettingsDocumentAsync(client, libraryId.ToString());
            var initialRetired = before.RootElement.GetProperty("stored").GetProperty("libraryOverride")
                .GetProperty("formats").EnumerateArray().Single(item =>
                    item.GetProperty("code").GetString() == $"audit_retired_{suffix}").GetRawText();
            var repositoryRoot = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
            var artifactDirectory = Path.Combine(repositoryRoot, ".artifacts", "browser", $"settings-metadata-{suffix}");
            Directory.CreateDirectory(artifactDirectory);
            var startInfo = new ProcessStartInfo
            {
                FileName = "node", WorkingDirectory = repositoryRoot,
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            };
            startInfo.ArgumentList.Add(Path.Combine(repositoryRoot, "tests", "browser", "settings.cjs"));
            startInfo.ArgumentList.Add(client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            startInfo.ArgumentList.Add(artifactDirectory);
            startInfo.ArgumentList.Add(actor.Id.ToString());
            startInfo.ArgumentList.Add(actor.EntraTenantId.ToString());
            startInfo.ArgumentList.Add(actor.AuthenticationEmail);
            startInfo.ArgumentList.Add("library-metadata");
            startInfo.ArgumentList.Add(libraryId.ToString());
            startInfo.ArgumentList.Add(suffix);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the production Settings metadata browser regression.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await stdoutTask + Environment.NewLine + await stderrTask;
            Assert.AreEqual(0, process.ExitCode, output);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(artifactDirectory, "settings-browser-results.json")));
            Assert.IsTrue(report.RootElement.GetProperty("libraryOverrideMetadataRoundTrip").GetBoolean());
            Assert.AreEqual(0, report.RootElement.GetProperty("externalRequests").GetInt32());
            Assert.AreEqual(0, report.RootElement.GetProperty("pageErrors").GetArrayLength());
            using var after = await ReadSettingsDocumentAsync(client, libraryId.ToString());
            var formats = after.RootElement.GetProperty("stored").GetProperty("libraryOverride").GetProperty("formats");
            Assert.AreEqual(initialRetired, formats.EnumerateArray().Single(item =>
                item.GetProperty("code").GetString() == $"audit_retired_{suffix}").GetRawText(),
                "Disabled owned formats, including exact identity and version, must survive ordinary sibling edits.");
            Assert.IsFalse(after.RootElement.GetProperty("effective").GetProperty("formats").EnumerateArray()
                .Any(item => item.GetProperty("code").GetString() == $"audit_custom_{suffix}"));
            Assert.AreEqual(beforeOutbox, await ReadCountAsync("SELECT COUNT(*) FROM [asap].[EmailOutbox];"));
            Assert.AreEqual(beforeEvents, await ReadCountAsync("SELECT COUNT(*) FROM [asap].[TitleRequestEvent];"));
            Assert.AreEqual(1, await ReadCountAsync($"SELECT COUNT(*) FROM [asap].[AdministrativeAudit] WHERE [OrganizationId] = {libraryId} AND [Action] = N'custom_format_deleted';"));
        }
        finally
        {
            await ExecuteNonQueryAsync("""
                DELETE FROM [asap].[AdministrativeAudit] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[MaterialFormatOverride] WHERE [LibraryOrganizationId] = @org;
                DELETE FROM [asap].[MaterialFormatCustomFieldRule] WHERE [LibraryOrganizationId] = @org;
                DELETE FROM [asap].[ExternalSearchProviderOverride] WHERE [LibraryOrganizationId] = @org;
                DELETE FROM [asap].[ExternalSearchProvider]
                WHERE [ProviderKey] IN (N'audit_provider_a_' + @suffix, N'audit_provider_b_' + @suffix);
                DELETE FROM [asap].[MaterialFormat] WHERE [Code] IN
                    (N'audit_format_a_' + @suffix, N'audit_format_b_' + @suffix, N'audit_inherited_' + @suffix,
                     N'audit_custom_' + @suffix, N'audit_sparse_' + @suffix, N'audit_retired_' + @suffix);
                DELETE FROM [asap].[PatronSettings] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[WorkflowSettings] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[Organization] WHERE [Id] = @org;
                """, ("@org", libraryId), ("@suffix", suffix));
        }
    }
}
