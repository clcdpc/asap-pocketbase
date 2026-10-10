using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task PatronSubmitBrowserJourneyRunsWithIdentitySwitchAndDeferredResponses()
    {
        factory!.UseKestrel(0);
        using var client = factory.CreateClient();
        var repositoryRoot = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
        var artifactDirectory = Path.Combine(repositoryRoot, ".artifacts", "browser", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactDirectory);
        var baseOrigin = client.BaseAddress?.GetLeftPart(UriPartial.Authority)
            ?? throw new InvalidOperationException("The Kestrel test host did not expose a base address.");
        const int scopedLibraryId = 91406;
        const string scopedBarcode = "20000000091406";
        const string scopedLibraryName = "Patron Browser Empty Format Library";
        var scopedPageTitle = $"Scoped browser configuration {Guid.NewGuid():N}";
        var cleanupTemplateKeyA = $"patron_browser_a_{Guid.NewGuid():N}";
        var cleanupTemplateKeyB = $"patron_browser_b_{Guid.NewGuid():N}";
        var ownsScopedLibrary = false;

        try
        {
            await using (var connection = new SqlConnection(databaseConnectionString))
            {
                await connection.OpenAsync();
                await using var existing = connection.CreateCommand();
                existing.CommandText = "SELECT COUNT(*) FROM [asap].[Organization] WHERE [Id] = @id;";
                existing.Parameters.AddWithValue("@id", scopedLibraryId);
                Assert.AreEqual(0, Convert.ToInt32(await existing.ExecuteScalarAsync()),
                    $"Test-owned library {scopedLibraryId} must not already exist.");
            }

            ownsScopedLibrary = true;
            await UpsertTestOrganizationAsync(scopedLibraryId, scopedLibraryName, "PBX");
            var provider = factory.Services.GetRequiredService<DeterministicTestingPatronProvider>();
            provider.AddPatron(
                new PatronSnapshot(9140601, scopedBarcode, "scoped-browser@example.org", "Scoped", "Patron",
                    1, "Adult", scopedLibraryId, scopedLibraryId, scopedLibraryName, 101),
                [new PickupBranch(101, "Main Library")],
                scopedLibraryId);

            using (var settingsClient = factory.CreateClient())
            {
                var actor = await ReadConfiguredSuperAdminAsync();
                AddTestingStaffHeaders(settingsClient, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
                settingsClient.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(settingsClient));

                using var initialSettings = await ReadSettingsDocumentAsync(settingsClient, scopedLibraryId.ToString());
                using (var savedSettings = await SaveSettingsDocumentAsync(
                           settingsClient,
                           initialSettings.RootElement,
                           scopedLibraryId.ToString(),
                           new Dictionary<string, object?>
                           {
                               ["patron"] = new
                               {
                                   pageTitle = scopedPageTitle,
                                   availableFormats = Array.Empty<string>()
                               }
                           }))
                {
                    Assert.AreEqual("saved", savedSettings.RootElement.GetProperty("code").GetString());
                }

                using var reloadedSettings = await ReadSettingsDocumentAsync(settingsClient, scopedLibraryId.ToString());
                var effectiveFormats = reloadedSettings.RootElement.GetProperty("effective").GetProperty("formats")
                    .EnumerateArray().ToArray();
                Assert.IsTrue(effectiveFormats.Length > 0, "The empty-format control must start from real inherited formats.");
                Assert.IsTrue(effectiveFormats.All(item => !item.GetProperty("isEnabled").GetBoolean()),
                    "An authoritative empty availableFormats Settings save must disable every effective format.");
                Assert.AreEqual(scopedPageTitle,
                    reloadedSettings.RootElement.GetProperty("effective").GetProperty("pageTitle").GetString());

                using var publicConfiguration = await settingsClient.GetAsync(
                    $"/api/asap/config?libraryOrgId={scopedLibraryId}");
                Assert.AreEqual(HttpStatusCode.OK, publicConfiguration.StatusCode,
                    await publicConfiguration.Content.ReadAsStringAsync());
                using var publicConfigurationJson = JsonDocument.Parse(await publicConfiguration.Content.ReadAsStringAsync());
                Assert.AreEqual(0, publicConfigurationJson.RootElement.GetProperty("availableFormats").GetArrayLength());
                Assert.AreEqual(scopedPageTitle, publicConfigurationJson.RootElement.GetProperty("pageTitle").GetString());
            }

            await using (var connection = new SqlConnection(databaseConnectionString))
            {
                await connection.OpenAsync();
                await using var verifySettings = connection.CreateCommand();
                verifySettings.CommandText =
                    "SELECT " +
                    "(SELECT COUNT_BIG(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1), " +
                    "(SELECT COUNT_BIG(*) FROM [asap].[MaterialFormat] AS format " +
                    "LEFT JOIN [asap].[MaterialFormatOverride] AS override " +
                    "ON override.[LibraryOrganizationId] = @organizationId AND override.[MaterialFormatId] = format.[Id] " +
                    "WHERE format.[OwnerOrganizationId] = 1 AND COALESCE(override.[IsEnabled], format.[IsEnabled]) = 1), " +
                    "(SELECT [PageTitle] FROM [asap].[PatronSettings] WHERE [OrganizationId] = @organizationId);";
                verifySettings.Parameters.AddWithValue("@organizationId", scopedLibraryId);
                await using var settingsReader = await verifySettings.ExecuteReaderAsync();
                Assert.IsTrue(await settingsReader.ReadAsync());
                Assert.IsTrue(settingsReader.GetInt64(0) > 0,
                    "The empty-format control must exercise inherited system formats.");
                Assert.AreEqual(0L, settingsReader.GetInt64(1),
                    "COALESCE of sparse library overrides and system format defaults must yield no enabled formats.");
                Assert.IsFalse(settingsReader.IsDBNull(2));
                Assert.AreEqual(scopedPageTitle, settingsReader.GetString(2),
                    "The saved Settings marker must be present in the owning PatronSettings row.");
            }

            await File.WriteAllTextAsync(
                Path.Combine(artifactDirectory, "patron-session-scope.json"),
                JsonSerializer.Serialize(new
                {
                    libraryOrgId = scopedLibraryId,
                    libraryName = scopedLibraryName,
                    barcode = scopedBarcode,
                    pageTitle = scopedPageTitle
                }));

            var sideEffectsBefore = await ReadSideEffectsAsync(databaseConnectionString);
            var providerWritesBefore = SnapshotProviderWrites(provider);
            var emailSender = factory.Services.GetRequiredService<IEmailSender>() as RecordingEmailSender
                ?? throw new InvalidOperationException("The patron journey must keep its recording email sender.");
            var emailSendsBefore = emailSender.Envelopes.Count;

            var (exitCode, stdout, stderr) = await RunBrowserScriptAsync(
                repositoryRoot,
                "patron-submit-session.cjs",
                baseOrigin,
                artifactDirectory);
            Assert.AreEqual(0, exitCode, $"{stdout}{Environment.NewLine}{stderr}");

            var sideEffectsAfter = await ReadSideEffectsAsync(databaseConnectionString);
            Assert.AreEqual(sideEffectsBefore, sideEffectsAfter,
                "Browser-side session/configuration guards must not persist patron request, event, email, or pickup-journal state.");
            CollectionAssert.AreEqual(providerWritesBefore, SnapshotProviderWrites(provider),
                "The added guards must not invoke provider external writes.");
            Assert.AreEqual(emailSendsBefore, emailSender.Envelopes.Count,
                "The added guards must not send patron email.");

            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(artifactDirectory, "browser-results.json")));
            var root = report.RootElement;
            Assert.HasCount(5, root.GetProperty("races").EnumerateArray().ToArray());
            Assert.HasCount(1, root.GetProperty("unknownOutcomes").EnumerateArray().ToArray());
            Assert.HasCount(2, root.GetProperty("configurationGuards").EnumerateArray().ToArray());
            Assert.HasCount(8, root.GetProperty("states").EnumerateArray().ToArray());
            var missingConfiguration = root.GetProperty("configurationGuards").EnumerateArray()
                .Single(item => item.GetProperty("scenario").GetString() == "real-session-missing-ui-text");
            Assert.IsTrue(missingConfiguration.GetProperty("serverIssuedTokenPresent").GetBoolean());
            Assert.IsFalse(missingConfiguration.GetProperty("clientTokenStored").GetBoolean());
            Assert.IsFalse(missingConfiguration.GetProperty("bIdentityEstablished").GetBoolean());
            Assert.IsTrue(missingConfiguration.GetProperty("aUiRetained").GetBoolean());
            var emptyFormats = root.GetProperty("configurationGuards").EnumerateArray()
                .Single(item => item.GetProperty("scenario").GetString() == "settings-empty-formats");
            Assert.AreEqual(scopedLibraryId, emptyFormats.GetProperty("effectiveLibraryOrgId").GetInt32());
            Assert.AreEqual(0, emptyFormats.GetProperty("availableFormats").GetArrayLength());
            Assert.AreEqual(0, emptyFormats.GetProperty("optionCount").GetInt32());
            Assert.AreEqual(0, emptyFormats.GetProperty("suggestionPostCount").GetInt32());
        }
        finally
        {
            if (ownsScopedLibrary)
            {
                await using (var connection = new SqlConnection(databaseConnectionString))
                {
                    await connection.OpenAsync();
                    await using var deleteSessions = connection.CreateCommand();
                    deleteSessions.CommandText = "DELETE FROM [asap].[PatronSession] WHERE [Barcode] = @barcode;";
                    deleteSessions.Parameters.AddWithValue("@barcode", scopedBarcode);
                    await deleteSessions.ExecuteNonQueryAsync();
                }

                await CleanupScalarSettingsTestDataAsync(
                    scopedLibraryId,
                    cleanupTemplateKeyA,
                    cleanupTemplateKeyB);
            }
        }

        static async Task<(long Requests, long Events, long Outbox, long PickupOperations)> ReadSideEffectsAsync(
            string connectionString)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT (SELECT COUNT_BIG(*) FROM [asap].[TitleRequest]), " +
                "(SELECT COUNT_BIG(*) FROM [asap].[TitleRequestEvent]), " +
                "(SELECT COUNT_BIG(*) FROM [asap].[EmailOutbox]), " +
                "(SELECT COUNT_BIG(*) FROM [asap].[PickupPreferenceOperation]);";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
        }

        static TestingPolarisCall[] SnapshotProviderWrites(DeterministicTestingPatronProvider provider) => provider.Calls
            .Where(call => call.Operation is TestingPolarisOperation.PickupUpdate or
                TestingPolarisOperation.HoldCreate or TestingPolarisOperation.HoldReply)
            .ToArray();
    }

    [TestMethod]
    public async Task StaffRequestBrowserJourneyRunsAgainstRealSqlAndHttp()
    {
        factory!.UseKestrel(0);
        var actor = await ReadConfiguredSuperAdminAsync();
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var fieldKey = $"browser_clear_{Guid.NewGuid():N}";
        var retiredSelectKey = $"browser_retired_{Guid.NewGuid():N}";
        await using var seed = await contexts.CreateDbContextAsync();
        var formatId = await seed.MaterialFormats.AsNoTracking()
            .Where(item => item.OwnerOrganizationId == 1 && item.Code == "book")
            .Select(item => item.Id).SingleAsync();
        var field = new PatronCustomField
        {
            LibraryOrganizationId = 2,
            FieldKey = fieldKey,
            FieldType = "text",
            Label = "Browser clear note",
            IsEnabled = true,
            SortOrder = 997
        };
        var retiredSelect = new PatronCustomField
        {
            LibraryOrganizationId = 2,
            FieldKey = retiredSelectKey,
            FieldType = "select",
            Label = "Browser retired choice",
            IsEnabled = true,
            SortOrder = 998
        };
        seed.PatronCustomFields.AddRange(field, retiredSelect);
        await seed.SaveChangesAsync();
        seed.PatronCustomFieldOptions.AddRange(
            new PatronCustomFieldOption
            {
                PatronCustomFieldId = retiredSelect.Id,
                OptionKey = "current-choice",
                Label = "Current active choice",
                IsEnabled = true,
                SortOrder = 1
            },
            new PatronCustomFieldOption
            {
                PatronCustomFieldId = retiredSelect.Id,
                OptionKey = "retired-choice",
                Label = "Current configured retired label",
                IsEnabled = false,
                SortOrder = 2
            });
        var clearRule = new MaterialFormatCustomFieldRule
        {
            LibraryOrganizationId = 2,
            MaterialFormatId = formatId,
            PatronCustomFieldId = field.Id,
            Mode = "optional"
        };
        var retiredRule = new MaterialFormatCustomFieldRule
        {
            LibraryOrganizationId = 2,
            MaterialFormatId = formatId,
            PatronCustomFieldId = retiredSelect.Id,
            Mode = "optional"
        };
        var request = new TitleRequest
        {
            LibraryOrganizationId = 2,
            Barcode = $"2{Guid.NewGuid():N}"[..14],
            Title = "Browser title before clear",
            MaterialFormatId = formatId,
            Status = "suggestion",
            AutoHold = true,
            CustomFieldsJson = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [fieldKey] = new { label = field.Label, type = field.FieldType, value = "Remove me" },
                [retiredSelectKey] = new
                {
                    label = "Original select label",
                    type = "select",
                    value = "retired-choice",
                    displayValue = "Original retired display"
                },
                ["retired_history"] = new { label = "Retired history", type = "text", value = "Keep retired snapshot" }
            }),
            CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime,
            UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime
        };
        var requestWithoutRetiredValue = new TitleRequest
        {
            LibraryOrganizationId = 2,
            Barcode = $"2{Guid.NewGuid():N}"[..14],
            Title = "Request without retired choice",
            MaterialFormatId = formatId,
            Status = "suggestion",
            AutoHold = true,
            CreatedUtc = timeProvider.GetUtcNow().UtcDateTime,
            UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime
        };
        var corruptRequest = new TitleRequest
        {
            LibraryOrganizationId = 2,
            Barcode = $"2{Guid.NewGuid():N}"[..14],
            Title = "Corrupt custom-field history",
            MaterialFormatId = formatId,
            Status = "suggestion",
            AutoHold = true,
            CustomFieldsJson = "{\"broken_history\":\"unsupported scalar\"}",
            CreatedUtc = timeProvider.GetUtcNow().UtcDateTime,
            UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime
        };
        seed.MaterialFormatCustomFieldRules.AddRange(clearRule, retiredRule);
        seed.TitleRequests.AddRange(request, requestWithoutRetiredValue, corruptRequest);
        await seed.SaveChangesAsync();
        var originalVersion = request.RowVersion.ToArray();

        try
        {
            using (var apiClient = factory.CreateClient())
            {
                AddTestingStaffHeaders(apiClient, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
                apiClient.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(apiClient));
                var beforeRejected = await ReadTitleRequestSnapshotAsync(requestWithoutRetiredValue.Id);
                using var rejected = await apiClient.PostAsJsonAsync(
                    $"/api/asap/staff/title-requests/{requestWithoutRetiredValue.Id}/action",
                    new
                    {
                        version = beforeRejected.Version,
                        action = "edit",
                        title = "Must not select a retired option",
                        customFields = new Dictionary<string, object>
                        {
                            [retiredSelectKey] = new
                            {
                                label = "Browser retired choice",
                                type = "select",
                                value = "retired-choice",
                                displayValue = "Current configured retired label"
                            }
                        }
                    });
                Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode, await rejected.Content.ReadAsStringAsync());
                using var rejectedBody = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
                Assert.AreEqual("invalid_custom_fields", rejectedBody.RootElement.GetProperty("code").GetString());
                await AssertTitleRequestUnchangedAsync(requestWithoutRetiredValue.Id, beforeRejected);
            }

            using var client = factory.CreateClient();
            var repositoryRoot = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
            var artifactDirectory = Path.Combine(repositoryRoot, ".artifacts", "browser", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(artifactDirectory);
            var baseOrigin = client.BaseAddress?.GetLeftPart(UriPartial.Authority)
                ?? throw new InvalidOperationException("The Kestrel test host did not expose a base address.");

            var (exitCode, stdout, stderr) = await RunBrowserScriptAsync(
                repositoryRoot,
                "staff-request-contracts.cjs",
                baseOrigin,
                artifactDirectory,
                actor.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                actor.EntraTenantId.ToString("D"),
                actor.AuthenticationEmail,
                request.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                fieldKey,
                retiredSelectKey,
                corruptRequest.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                field.Label,
                retiredSelect.Label);
            Assert.AreEqual(0, exitCode, $"{stdout}{Environment.NewLine}{stderr}");

            await using var verify = await contexts.CreateDbContextAsync();
            var persisted = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id);
            Assert.AreEqual("Browser saved after clear", persisted.Title);
            Assert.IsFalse(originalVersion.SequenceEqual(persisted.RowVersion));
            using (var customFields = JsonDocument.Parse(persisted.CustomFieldsJson!))
            {
                Assert.IsFalse(customFields.RootElement.TryGetProperty(fieldKey, out _));
                var preservedRetiredSelect = customFields.RootElement.GetProperty(retiredSelectKey);
                Assert.AreEqual("Original select label", preservedRetiredSelect.GetProperty("label").GetString());
                Assert.AreEqual("select", preservedRetiredSelect.GetProperty("type").GetString());
                Assert.AreEqual("retired-choice", preservedRetiredSelect.GetProperty("value").GetString());
                Assert.AreEqual("Original retired display", preservedRetiredSelect.GetProperty("displayValue").GetString());
                Assert.AreEqual("Keep retired snapshot",
                    customFields.RootElement.GetProperty("retired_history").GetProperty("value").GetString());
            }
            Assert.AreEqual(1, await verify.TitleRequestEvents.CountAsync(item =>
                item.TitleRequestId == request.Id && item.EventType == "request_edited"));
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(artifactDirectory, "browser-results.json")));
            Assert.HasCount(3, report.RootElement.GetProperty("states").EnumerateArray().ToArray());
            var edit = report.RootElement.GetProperty("requestEdit");
            Assert.AreEqual("invalid_title", edit.GetProperty("invalidTitleCode").GetString());
            Assert.IsTrue(edit.GetProperty("invalidTitlePreservedVersion").GetBoolean());
            Assert.IsTrue(edit.GetProperty("retiredSnapshotPreserved").GetBoolean());
            Assert.IsTrue(edit.GetProperty("corruptHistoryBlocked").GetBoolean());
        }
        finally
        {
            await ExecuteNonQueryAsync(
                "DELETE FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] IN (@requestId, @rejectedRequestId, @corruptRequestId); " +
                "DELETE FROM [asap].[TitleRequest] WHERE [Id] IN (@requestId, @rejectedRequestId, @corruptRequestId); " +
                "DELETE FROM [asap].[MaterialFormatCustomFieldRule] WHERE [Id] IN (@clearRuleId, @retiredRuleId); " +
                "DELETE FROM [asap].[PatronCustomFieldOption] WHERE [PatronCustomFieldId] = @retiredSelectId; " +
                "DELETE FROM [asap].[PatronCustomField] WHERE [Id] IN (@fieldId, @retiredSelectId);",
                ("@requestId", request.Id), ("@rejectedRequestId", requestWithoutRetiredValue.Id),
                ("@corruptRequestId", corruptRequest.Id),
                ("@clearRuleId", clearRule.Id), ("@retiredRuleId", retiredRule.Id),
                ("@fieldId", field.Id), ("@retiredSelectId", retiredSelect.Id));
        }

        await RunImportedSnapshotBrowserJourneyAsync();
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunBrowserScriptAsync(
        string repositoryRoot,
        string scriptName,
        string baseOrigin,
        string artifactDirectory,
        params string[] additionalArguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.Environment["TZ"] = "America/New_York";
        startInfo.ArgumentList.Add(Path.Combine(repositoryRoot, "tests", "browser", scriptName));
        startInfo.ArgumentList.Add(baseOrigin);
        startInfo.ArgumentList.Add(artifactDirectory);
        foreach (var argument in additionalArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start the {scriptName} browser runner.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
