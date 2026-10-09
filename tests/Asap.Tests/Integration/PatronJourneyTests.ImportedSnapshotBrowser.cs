using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Migration;
using Asap.Tests.Migration;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SqlServer.Dac;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    private async Task RunImportedSnapshotBrowserJourneyAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-imported-browser-{Guid.NewGuid():N}");
        var database = $"AsapImportedBrowser_{Guid.NewGuid():N}";
        var target = new SqlConnectionStringBuilder(masterConnectionString) { InitialCatalog = database }.ConnectionString;
        var migrationEnvironmentName = $"ASAP_IMPORTED_BROWSER_{Guid.NewGuid():N}";
        var fieldKey = $"imported_clear_{Guid.NewGuid():N}";
        var retiredSelectKey = $"imported_retired_{Guid.NewGuid():N}";
        const string clearFieldLabel = "Imported clear note";
        const string retiredSelectLabel = "Imported retired choice";
        const string importedLibraryFormatCode = "imported_local";
        const string importedLibraryFormatSourceId = "fmt-imported-local";
        const string importedAutoClaimRuleSourceId = "claim-imported-local";
        const string importedSnapshotSelectLabel = "Imported select label";
        const string importedSnapshotDisplayValue = "Imported retired display";
        const string importedRetiredHistoryValue = "Keep imported snapshot";
        var sourceCreated = "2029-03-01T12:00:00Z";
        var fieldsJson = JsonSerializer.Serialize(new object[]
        {
            new
            {
                key = fieldKey,
                label = clearFieldLabel,
                type = "text",
                enabled = true,
                sortOrder = 997,
                options = Array.Empty<object>()
            },
            new
            {
                key = retiredSelectKey,
                label = retiredSelectLabel,
                type = "select",
                enabled = true,
                sortOrder = 998,
                options = new[]
                {
                    new { id = "current-choice", label = "Current active choice", enabled = true, sortOrder = 1 },
                    new { id = "retired-choice", label = "Configured retired label", enabled = false, sortOrder = 2 }
                }
            }
        });
        var rulesJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["book"] = new
            {
                customFields = new Dictionary<string, object>
                {
                    [fieldKey] = new { mode = "optional" },
                    [retiredSelectKey] = new { mode = "optional" }
                }
            },
            [importedLibraryFormatCode] = new
            {
                customFields = new Dictionary<string, object>
                {
                    [fieldKey] = new { mode = "required" }
                }
            }
        });
        var importedSnapshot = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [fieldKey] = new { label = clearFieldLabel, type = "text", value = "Remove me" },
            [retiredSelectKey] = new
            {
                label = importedSnapshotSelectLabel,
                type = "select",
                value = "retired-choice",
                displayValue = importedSnapshotDisplayValue
            },
            ["retired_history"] = new { label = "Retired history", type = "text", value = importedRetiredHistoryValue }
        });
        var quoteSql = static (string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        var additionalSql = $$"""
            CREATE TABLE [material_formats]
            (
                [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL,
                [libraryOrganization] TEXT, [code] TEXT NOT NULL, [label] TEXT NOT NULL,
                [enabled] INTEGER NOT NULL, [sortOrder] INTEGER NOT NULL
            );
            INSERT INTO [material_formats] VALUES ('fmt-book', 'system', NULL, 'book', 'Book', 1, 10);
            INSERT INTO [material_formats] VALUES
                ({{quoteSql(importedLibraryFormatSourceId)}}, 'library', 'pb-org-2',
                 {{quoteSql(importedLibraryFormatCode)}}, 'Imported local format', 1, 20);
            INSERT INTO [staff_users] VALUES
                ('pb-staff-imported-local', 'imported-selector@example.org', 'imported-selector',
                 'Imported Library Selector', 'staff', 1, '2', 0, NULL, 0, 0, 0);
            CREATE TABLE [format_claim_rules]
            (
                [id] TEXT NOT NULL PRIMARY KEY, [libraryOrgId] TEXT NOT NULL,
                [format] TEXT NOT NULL, [staffUserId] TEXT, [active] INTEGER NOT NULL,
                [created] TEXT, [updated] TEXT
            );
            INSERT INTO [format_claim_rules] VALUES
                ({{quoteSql(importedAutoClaimRuleSourceId)}}, '2', {{quoteSql(importedLibraryFormatCode)}},
                 'pb-staff-imported-local', 1, '{{sourceCreated}}', '{{sourceCreated}}');
            CREATE TABLE [patron_settings_overrides]
            (
                [id] TEXT NOT NULL PRIMARY KEY, [orgId] TEXT NOT NULL,
                [duplicateStatusLabels] TEXT, [publicationOptions] TEXT, [patronFormatRules] TEXT,
                [additionalFieldDefinitions] TEXT, [ebookMessage] TEXT, [eaudiobookMessage] TEXT,
                [created] TEXT, [updated] TEXT
            );
            INSERT INTO [patron_settings_overrides] VALUES
                ('patron-override-2', '2', NULL, NULL, {{quoteSql(rulesJson)}}, {{quoteSql(fieldsJson)}},
                 NULL, NULL, '{{sourceCreated}}', '{{sourceCreated}}');
            CREATE TABLE [title_requests]
            (
                [id] TEXT NOT NULL PRIMARY KEY, [libraryOrgId] TEXT NOT NULL, [formatRef] TEXT,
                [barcode] TEXT NOT NULL, [email] TEXT, [nameFirst] TEXT, [nameLast] TEXT,
                [title] TEXT NOT NULL, [author] TEXT, [identifier] TEXT, [publication] TEXT,
                [autohold] INTEGER NOT NULL, [status] TEXT NOT NULL, [customFields] TEXT,
                [created] TEXT NOT NULL, [updated] TEXT NOT NULL
            );
            INSERT INTO [title_requests] VALUES
                ('imported-request-with-history', '2', 'fmt-book', 'A20000000001001', NULL, 'Ada', 'Reader',
                 'Imported title before edit', 'A. Writer', '9780000000101', 'Coming soon', 1, 'suggestion',
                 {{quoteSql(importedSnapshot)}}, '{{sourceCreated}}', '{{sourceCreated}}'),
                ('imported-request-without-history', '2', 'fmt-book', 'A20000000001002', NULL, 'Grace', 'Reader',
                 'Imported request without retired value', 'G. Writer', '9780000000102', 'Coming soon', 1, 'suggestion',
                 {{quoteSql("{}")}}, '{{sourceCreated}}', '{{sourceCreated}}');
            """;
        Directory.CreateDirectory(root);

        try
        {
            var package = MigrationCliTests.CreateMinimalPackage(root, additionalSql);
            DeployImportedBrowserDacpac(database);
            Environment.SetEnvironmentVariable(migrationEnvironmentName, target);
            var importReportPath = Path.Combine(root, "import-report.json");
            using (var importError = new StringWriter())
            {
                var exitCode = MigrationCli.Run(
                    [
                        "import", "--package", package,
                        "--connection-string-env", migrationEnvironmentName,
                        "--allowed-tenant-ids", TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin.TenantId!,
                        "--report", importReportPath,
                        "--external-config", Path.Combine(Directory.GetParent(package)!.FullName, "asap.settings.json")
                    ],
                    TextWriter.Null,
                    importError);
                Assert.AreEqual(0, exitCode, importError.ToString());
            }

            using (var importReport = JsonDocument.Parse(await File.ReadAllTextAsync(importReportPath)))
            {
                Assert.IsTrue(importReport.RootElement.GetProperty("reconciliationPassed").GetBoolean());
                Assert.AreEqual(2, importReport.RootElement.GetProperty("importedCounts").GetProperty("title_requests").GetInt32());
            }

            var importedSettings = TestConfigurationFactory.Create(allowedDomains: ["example.org"]);
            importedSettings.ConnectionStrings.AsapDatabase = target;
            importedSettings.ConnectionStrings.HangfireDatabase = target;
            var keysPath = Path.Combine(root, "keys");
            var logsPath = Path.Combine(root, "logs");
            importedSettings.Application.DataProtectionKeysPath = keysPath;
            importedSettings.Application.LogPath = logsPath;
            importedSettings.Application.DataProtectionKeyEncryptionCertificateThumbprint = certificateThumbprint;
            Directory.CreateDirectory(keysPath);
            Directory.CreateDirectory(logsPath);
            var importedSettingsPath = Path.Combine(root, "web.settings.json");
            await File.WriteAllTextAsync(importedSettingsPath,
                JsonSerializer.Serialize(importedSettings, new JsonSerializerOptions { WriteIndented = true }));
            await InstallHangfireForImportedBrowserAsync(target);

            // Imported requests predate the fixed package export timestamp at 2030-01-02.
            timeProvider!.SetUtcNow(new DateTimeOffset(2030, 1, 3, 3, 4, 5, TimeSpan.Zero));
            await using var importedFactory = CreateApplicationFactory(importedSettingsPath);
            importedFactory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
            using var client = importedFactory.CreateClient();
            var baseOrigin = client.BaseAddress?.GetLeftPart(UriPartial.Authority)
                ?? throw new InvalidOperationException("The imported-snapshot Kestrel host did not expose a base address.");
            var contextFactory = importedFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
            long actorId;
            string actorEmail;
            long formatId;
            long importedLibraryFormatId;
            long importedCustomFieldId;
            long importedFormatRuleId;
            long importedAutoClaimRuleId;
            DateTime importedAutoClaimCreatedUtc;
            long importedRequestId;
            long importedRequestWithoutHistoryId;
            byte[] importedRequestVersion;
            long corruptRequestId;
            await using (var imported = await contextFactory.CreateDbContextAsync())
            {
                var actor = await imported.StaffUsers.AsNoTracking()
                    .Where(item => item.IsActive && item.Role == StaffRole.SuperAdmin)
                    .OrderBy(item => item.Id)
                    .Select(item => new { item.Id, item.UserPrincipalName })
                    .SingleAsync();
                actorId = actor.Id;
                actorEmail = actor.UserPrincipalName!;
                formatId = await imported.MaterialFormats.AsNoTracking()
                    .Where(item => item.OwnerOrganizationId == 1 && item.Code == "book")
                    .Select(item => item.Id).SingleAsync();
                var importedLibraryFormat = await imported.MaterialFormats.AsNoTracking()
                    .SingleAsync(item => item.OwnerOrganizationId == 2 && item.Code == importedLibraryFormatCode);
                importedLibraryFormatId = importedLibraryFormat.Id;
                var importedFormatMapping = await imported.LegacyPocketBaseMappings.AsNoTracking()
                    .SingleAsync(item => item.EntityType == "material_format" &&
                                         item.PocketBaseId == importedLibraryFormatSourceId);
                Assert.AreEqual(importedLibraryFormatId, importedFormatMapping.NewId);
                var importedCustomField = await imported.PatronCustomFields.AsNoTracking()
                    .SingleAsync(item => item.LibraryOrganizationId == 2 && item.FieldKey == fieldKey);
                importedCustomFieldId = importedCustomField.Id;
                var importedFormatRule = await imported.MaterialFormatCustomFieldRules.AsNoTracking()
                    .SingleAsync(item => item.LibraryOrganizationId == 2 &&
                                         item.MaterialFormatId == importedLibraryFormatId &&
                                         item.PatronCustomFieldId == importedCustomFieldId);
                importedFormatRuleId = importedFormatRule.Id;
                Assert.AreEqual("required", importedFormatRule.Mode);
                var importedAutoClaimRule = await imported.FormatAutoClaimRules.AsNoTracking()
                    .SingleAsync(item => item.LibraryOrganizationId == 2 &&
                                         item.MaterialFormatId == importedLibraryFormatId);
                importedAutoClaimRuleId = importedAutoClaimRule.Id;
                importedAutoClaimCreatedUtc = importedAutoClaimRule.CreatedUtc;
                Assert.IsTrue(importedAutoClaimRule.IsActive);
                var importedAutoClaimMapping = await imported.LegacyPocketBaseMappings.AsNoTracking()
                    .SingleAsync(item => item.EntityType == "format_auto_claim_rule" &&
                                         item.PocketBaseId == importedAutoClaimRuleSourceId);
                Assert.AreEqual(importedAutoClaimRuleId, importedAutoClaimMapping.NewId);
                importedRequestId = await imported.LegacyPocketBaseMappings.AsNoTracking()
                    .Where(item => item.EntityType == "title_request" && item.PocketBaseId == "imported-request-with-history")
                    .Select(item => item.NewId).SingleAsync();
                importedRequestWithoutHistoryId = await imported.LegacyPocketBaseMappings.AsNoTracking()
                    .Where(item => item.EntityType == "title_request" && item.PocketBaseId == "imported-request-without-history")
                    .Select(item => item.NewId).SingleAsync();
                var importedRow = await imported.TitleRequests.AsNoTracking()
                    .SingleAsync(item => item.Id == importedRequestId);
                importedRequestVersion = importedRow.RowVersion.ToArray();
                using (var storedSnapshot = JsonDocument.Parse(importedRow.CustomFieldsJson!))
                {
                    var retired = storedSnapshot.RootElement.GetProperty(retiredSelectKey);
                    Assert.AreEqual(importedSnapshotSelectLabel, retired.GetProperty("label").GetString());
                    Assert.AreEqual("retired-choice", retired.GetProperty("value").GetString());
                    Assert.AreEqual(importedSnapshotDisplayValue, retired.GetProperty("displayValue").GetString());
                }

                var corruptRequest = new TitleRequest
                {
                    LibraryOrganizationId = 2,
                    Barcode = $"A{Guid.NewGuid():N}"[..14],
                    Title = "Imported target corrupt history",
                    MaterialFormatId = formatId,
                    Status = "suggestion",
                    AutoHold = true,
                    CustomFieldsJson = "{\"broken_history\":\"unsupported scalar\"}",
                    CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime,
                    UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime
                };
                imported.TitleRequests.Add(corruptRequest);
                await imported.SaveChangesAsync();
                corruptRequestId = corruptRequest.Id;
            }

            var tenantId = Guid.Parse(importedSettings.Authentication.Entra.InitialSuperAdmin.TenantId!);
            using (var apiClient = importedFactory.CreateClient())
            {
                AddTestingStaffHeaders(apiClient, actorId, tenantId, actorEmail);
                apiClient.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(apiClient));
                var beforeRejected = await ReadTitleRequestSnapshotAsync(importedRequestWithoutHistoryId, target);
                using var rejected = await apiClient.PostAsJsonAsync(
                    $"/api/asap/staff/title-requests/{importedRequestWithoutHistoryId}/action",
                    new
                    {
                        version = beforeRejected.Version,
                        action = "edit",
                        title = "Cannot newly choose a retired imported option",
                        customFields = new Dictionary<string, object>
                        {
                            [retiredSelectKey] = new
                            {
                                label = retiredSelectLabel,
                                type = "select",
                                value = "retired-choice",
                                displayValue = "Configured retired label"
                            }
                        }
                    });
                Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode, await rejected.Content.ReadAsStringAsync());
                using var rejectedBody = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
                Assert.AreEqual("invalid_custom_fields", rejectedBody.RootElement.GetProperty("code").GetString());
                await AssertTitleRequestUnchangedAsync(importedRequestWithoutHistoryId, beforeRejected, target);
            }

            var repositoryRoot = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
            var artifactDirectory = Path.Combine(repositoryRoot, ".artifacts", "browser", $"imported-snapshot-{Guid.NewGuid():N}");
            Directory.CreateDirectory(artifactDirectory);
            var (browserExitCode, stdout, stderr) = await RunBrowserScriptAsync(
                repositoryRoot,
                "staff-request-contracts.cjs",
                baseOrigin,
                artifactDirectory,
                actorId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                tenantId.ToString("D"),
                actorEmail,
                importedRequestId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                fieldKey,
                retiredSelectKey,
                corruptRequestId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                clearFieldLabel,
                retiredSelectLabel);
            Assert.AreEqual(0, browserExitCode,
                $"Browser artifacts: {artifactDirectory}{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");

            await using var verify = await contextFactory.CreateDbContextAsync();
            var persisted = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == importedRequestId);
            Assert.AreEqual("Browser saved after clear", persisted.Title);
            Assert.IsFalse(importedRequestVersion.SequenceEqual(persisted.RowVersion));
            using var fieldsAfterEdit = JsonDocument.Parse(persisted.CustomFieldsJson!);
            var preservedRetired = fieldsAfterEdit.RootElement.GetProperty(retiredSelectKey);
            Assert.AreEqual(importedSnapshotSelectLabel, preservedRetired.GetProperty("label").GetString());
            Assert.AreEqual("select", preservedRetired.GetProperty("type").GetString());
            Assert.AreEqual("retired-choice", preservedRetired.GetProperty("value").GetString());
            Assert.AreEqual(importedSnapshotDisplayValue, preservedRetired.GetProperty("displayValue").GetString());
            Assert.AreEqual(importedRetiredHistoryValue,
                fieldsAfterEdit.RootElement.GetProperty("retired_history").GetProperty("value").GetString());
            Assert.IsFalse(fieldsAfterEdit.RootElement.TryGetProperty(fieldKey, out _));
            Assert.AreEqual(1, await verify.TitleRequestEvents.CountAsync(item =>
                item.TitleRequestId == importedRequestId && item.EventType == "request_edited"));

            using var resetClient = importedFactory.CreateClient();
            AddTestingStaffHeaders(resetClient, actorId, tenantId, actorEmail);
            resetClient.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(resetClient));
            await using (var seedOverrides = await contextFactory.CreateDbContextAsync())
            {
                seedOverrides.WorkflowSettings.Add(new WorkflowSettings
                {
                    OrganizationId = 2,
                    SuggestionLimitMessage = "Temporary imported browser override",
                    UpdatedUtc = timeProvider!.GetUtcNow().UtcDateTime
                });
                seedOverrides.MaterialFormatOverrides.Add(new MaterialFormatOverride
                {
                    LibraryOrganizationId = 2,
                    MaterialFormatId = formatId,
                    Label = "Temporary imported browser book override"
                });
                await seedOverrides.SaveChangesAsync();
            }

            using var settingsBeforeResetResponse = await resetClient.GetAsync("/api/asap/staff/settings?orgId=2");
            Assert.AreEqual(HttpStatusCode.OK, settingsBeforeResetResponse.StatusCode,
                await settingsBeforeResetResponse.Content.ReadAsStringAsync());
            using var settingsBeforeReset = JsonDocument.Parse(await settingsBeforeResetResponse.Content.ReadAsStringAsync());
            using var resetResponse = await resetClient.PostAsJsonAsync(
                "/api/asap/staff/settings/reset?organizationId=2",
                new { version = settingsBeforeReset.RootElement.GetProperty("version").GetString() });
            Assert.AreEqual(HttpStatusCode.OK, resetResponse.StatusCode, await resetResponse.Content.ReadAsStringAsync());
            using var resetBody = JsonDocument.Parse(await resetResponse.Content.ReadAsStringAsync());
            Assert.AreEqual("reset", resetBody.RootElement.GetProperty("code").GetString());

            await using var afterReset = await contextFactory.CreateDbContextAsync();
            Assert.IsFalse(await afterReset.WorkflowSettings.AsNoTracking().AnyAsync(item => item.OrganizationId == 2));
            Assert.IsFalse(await afterReset.MaterialFormatOverrides.AsNoTracking().AnyAsync(item =>
                item.LibraryOrganizationId == 2 && item.MaterialFormatId == formatId));
            var preservedFormat = await afterReset.MaterialFormats.AsNoTracking()
                .SingleAsync(item => item.Id == importedLibraryFormatId);
            Assert.AreEqual(2, preservedFormat.OwnerOrganizationId);
            Assert.AreEqual(importedLibraryFormatCode, preservedFormat.Code);
            Assert.IsTrue(await afterReset.LegacyPocketBaseMappings.AsNoTracking().AnyAsync(item =>
                item.EntityType == "material_format" && item.PocketBaseId == importedLibraryFormatSourceId &&
                item.NewId == importedLibraryFormatId));
            Assert.IsTrue(await afterReset.PatronCustomFields.AsNoTracking().AnyAsync(item =>
                item.Id == importedCustomFieldId && item.LibraryOrganizationId == 2 && item.FieldKey == fieldKey));
            Assert.IsTrue(await afterReset.MaterialFormatCustomFieldRules.AsNoTracking().AnyAsync(item =>
                item.Id == importedFormatRuleId && item.LibraryOrganizationId == 2 &&
                item.MaterialFormatId == importedLibraryFormatId && item.PatronCustomFieldId == importedCustomFieldId &&
                item.Mode == "required"));
            var preservedAutoClaimRule = await afterReset.FormatAutoClaimRules.AsNoTracking()
                .SingleAsync(item => item.Id == importedAutoClaimRuleId);
            Assert.AreEqual(2, preservedAutoClaimRule.LibraryOrganizationId);
            Assert.AreEqual(importedLibraryFormatId, preservedAutoClaimRule.MaterialFormatId);
            Assert.AreEqual(importedAutoClaimCreatedUtc, preservedAutoClaimRule.CreatedUtc);
            Assert.IsTrue(preservedAutoClaimRule.IsActive);
            Assert.IsTrue(await afterReset.LegacyPocketBaseMappings.AsNoTracking().AnyAsync(item =>
                item.EntityType == "format_auto_claim_rule" && item.PocketBaseId == importedAutoClaimRuleSourceId &&
                item.NewId == importedAutoClaimRuleId));
            var requestAfterReset = await afterReset.TitleRequests.AsNoTracking()
                .SingleAsync(item => item.Id == importedRequestId);
            Assert.AreEqual("Browser saved after clear", requestAfterReset.Title);
            using var historyAfterReset = JsonDocument.Parse(requestAfterReset.CustomFieldsJson!);
            Assert.AreEqual(importedSnapshotDisplayValue,
                historyAfterReset.RootElement.GetProperty(retiredSelectKey).GetProperty("displayValue").GetString());
            Assert.AreEqual(importedRetiredHistoryValue,
                historyAfterReset.RootElement.GetProperty("retired_history").GetProperty("value").GetString());
            Assert.AreEqual(1, await afterReset.TitleRequestEvents.CountAsync(item =>
                item.TitleRequestId == importedRequestId && item.EventType == "request_edited"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(migrationEnvironmentName, null);
            try
            {
                await DropImportedBrowserDatabaseAsync(database);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    var tempRoot = Path.GetFullPath(Path.GetTempPath())
                                       .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                                   Path.DirectorySeparatorChar;
                    var fullRoot = Path.GetFullPath(root);
                    if (!fullRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("Refusing to remove the imported-browser fixture outside the temporary directory.");
                    }
                    Directory.Delete(root, recursive: true);
                }
            }
        }
    }

    private static void DeployImportedBrowserDacpac(string databaseName)
    {
        using var package = DacPackage.Load(TestArtifactPaths.FindDacpac());
        new DacServices(masterConnectionString).Deploy(
            package,
            databaseName,
            upgradeExisting: true,
            new DacDeployOptions
            {
                BlockOnPossibleDataLoss = true,
                CreateNewDatabase = true,
                DropObjectsNotInSource = true
            });
    }

    private static async Task InstallHangfireForImportedBrowserAsync(string connectionString)
    {
        var sql = await File.ReadAllTextAsync(TestArtifactPaths.FindRepositoryFile(
            "scripts", "hangfire", "1.8.25", "install.sql"));
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var batch in System.Text.RegularExpressions.Regex.Split(
                     sql, @"^\s*GO\s*$", System.Text.RegularExpressions.RegexOptions.Multiline |
                          System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(batch))
            {
                continue;
            }

            await using var command = new SqlCommand(batch, connection) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task DropImportedBrowserDatabaseAsync(string databaseName)
    {
        if (!databaseName.StartsWith("AsapImportedBrowser_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to drop a database outside the isolated browser-import fixture.");
        }

        await using var connection = new SqlConnection(masterConnectionString);
        await connection.OpenAsync();
        var quoted = new SqlCommandBuilder().QuoteIdentifier(databaseName);
        await using var command = connection.CreateCommand();
        command.CommandText = $"IF DB_ID(@name) IS NOT NULL BEGIN ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {quoted}; END";
        command.Parameters.AddWithValue("@name", databaseName);
        await command.ExecuteNonQueryAsync();
    }
}
