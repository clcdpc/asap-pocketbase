using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SqlServer.Dac;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    private async Task RunNativeSnapshotBrowserJourneyAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-native-browser-{Guid.NewGuid():N}");
        var database = $"AsapNativeBrowser_{Guid.NewGuid():N}";
        var target = new SqlConnectionStringBuilder(masterConnectionString) { InitialCatalog = database }.ConnectionString;
        var fieldKey = $"native_clear_{Guid.NewGuid():N}";
        var retiredSelectKey = $"native_retired_{Guid.NewGuid():N}";
        const string clearFieldLabel = "Native clear note";
        const string retiredSelectLabel = "Native retired choice";
        const string nativeLibraryFormatCode = "native_local";
        const string nativeSnapshotSelectLabel = "Native select label";
        const string nativeSnapshotDisplayValue = "Native retired display";
        const string nativeRetiredHistoryValue = "Keep native snapshot";
        var nativeSnapshot = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [fieldKey] = new { label = clearFieldLabel, type = "text", value = "Remove me" },
            [retiredSelectKey] = new
            {
                label = nativeSnapshotSelectLabel,
                type = "select",
                value = "retired-choice",
                displayValue = nativeSnapshotDisplayValue
            },
            ["retired_history"] = new { label = "Retired history", type = "text", value = nativeRetiredHistoryValue }
        });
        Directory.CreateDirectory(root);
        try
        {
            DeployNativeBrowserDacpac(database);
            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using var seed = connection.CreateCommand();
                seed.CommandText = """
                    INSERT INTO [asap].[Organization] ([Id], [DisplayName], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
                    VALUES (2, N'Test Library', 2, 1, 1), (20, N'Test Branch', 3, 2, 1);
                    UPDATE [asap].[PolarisSettings] SET [WorkstationId] = 99, [SystemPolarisUserId] = 42 WHERE [OrganizationId] = 1;
                    UPDATE [asap].[EmailSettings] SET [FromAddress] = N'asap@example.org', [FromName] = N'ASAP Tests' WHERE [OrganizationId] = 1;
                    UPDATE [asap].[WorkflowSettings] SET [SuggestionLimit] = 9 WHERE [OrganizationId] = 1;
                    INSERT INTO [asap].[WorkflowSettings] ([OrganizationId], [SuggestionLimit], [OutstandingTimeoutDays],
                        [HoldPickupTimeoutDays], [PendingHoldTimeoutDays], [AdditionalCopyTimeoutDays], [AllowPatronAutoholdOptOut], [UpdatedUtc])
                    VALUES (2, 5, 30, 14, 14, 14, 1, SYSUTCDATETIME());
                    INSERT INTO [asap].[StaffUser] ([UserPrincipalName], [NormalizedUserPrincipalName], [DisplayName], [Role], [OrganizationId], [IsActive])
                    VALUES (@adminEmail, UPPER(@adminEmail), N'Test Administrator', N'super_admin', 1, 1);
                    INSERT INTO [asap].[StaffUser] ([UserPrincipalName], [NormalizedUserPrincipalName], [DisplayName], [Role], [OrganizationId], [IsActive])
                    VALUES (N'native-selector@example.org', N'NATIVE-SELECTOR@EXAMPLE.ORG', N'Library Selector', N'staff', 2, 1);
                    DECLARE @staff bigint = SCOPE_IDENTITY();
                    INSERT INTO [asap].[MaterialFormat] ([OwnerOrganizationId], [Code], [Label], [IsEnabled], [SortOrder], [CreatedUtc], [UpdatedUtc])
                    VALUES (2, N'native_local', N'Native local format', 1, 20, SYSUTCDATETIME(), SYSUTCDATETIME());
                    DECLARE @local bigint = SCOPE_IDENTITY();
                    DECLARE @book bigint = (SELECT [Id] FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'book');
                    INSERT INTO [asap].[PatronCustomField] ([LibraryOrganizationId], [FieldKey], [FieldType], [Label], [IsEnabled], [SortOrder])
                    VALUES (2, @field, N'text', N'Native clear note', 1, 997);
                    DECLARE @text bigint = SCOPE_IDENTITY();
                    INSERT INTO [asap].[PatronCustomField] ([LibraryOrganizationId], [FieldKey], [FieldType], [Label], [IsEnabled], [SortOrder])
                    VALUES (2, @retired, N'select', N'Native retired choice', 1, 998);
                    DECLARE @select bigint = SCOPE_IDENTITY();
                    INSERT INTO [asap].[PatronCustomFieldOption] ([PatronCustomFieldId], [OptionKey], [Label], [IsEnabled], [SortOrder])
                    VALUES (@select, N'current-choice', N'Current active choice', 1, 1), (@select, N'retired-choice', N'Configured retired label', 0, 2);
                    INSERT INTO [asap].[MaterialFormatCustomFieldRule] ([LibraryOrganizationId], [MaterialFormatId], [PatronCustomFieldId], [Mode])
                    VALUES (2, @local, @text, N'required'), (2, @book, @text, N'optional'), (2, @book, @select, N'optional');
                    INSERT INTO [asap].[FormatAutoClaimRule] ([LibraryOrganizationId], [MaterialFormatId], [StaffUserId], [IsActive], [CreatedUtc])
                    VALUES (2, @local, @staff, 1, SYSUTCDATETIME());
                    INSERT INTO [asap].[PublicationOptionSet] ([OrganizationId]) VALUES (2);
                    INSERT INTO [asap].[PublicationOption] ([OrganizationId], [OptionKey], [Label], [IsEnabled], [SortOrder])
                    VALUES (2, N'coming_soon', N'Coming soon', 1, 1), (2, N'local', N'Native local publication', 1, 2);
                    INSERT INTO [asap].[TitleRequest] ([LibraryOrganizationId], [Barcode], [Title], [Author], [Identifier], [Publication],
                        [AutoHold], [MaterialFormatId], [Status], [CustomFieldsJson], [CreatedUtc], [UpdatedUtc])
                    VALUES (2, N'A20000000001001', N'Native title before edit', N'A. Writer', N'9780000000101', N'Coming soon',
                        1, @book, N'suggestion', @snapshot, SYSUTCDATETIME(), SYSUTCDATETIME()),
                        (2, N'A20000000001002', N'Native request without retired value', N'G. Writer', N'9780000000102', N'Coming soon',
                        1, @book, N'suggestion', N'{}', SYSUTCDATETIME(), SYSUTCDATETIME());
                    INSERT INTO [asap].[TitleRequest] ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId],
                        [Status], [BibId], [BibIdStaffVerified], [CloseReason], [CreatedUtc], [UpdatedUtc])
                    VALUES (2, N'A20000000001003', N'Native protected history', 0, @book, N'closed', 9001, 1, N'manual', SYSUTCDATETIME(), SYSUTCDATETIME());
                    DECLARE @held bigint = SCOPE_IDENTITY();
                    INSERT INTO [asap].[HoldPlacementOperation] ([TitleRequestId], [PatronBarcodeSnapshot], [BibIdSnapshot], [AttemptNumber],
                        [State], [Phase], [ExecutionEpoch], [RequestStartedUtc], [CompletedUtc])
                    VALUES (@held, N'A20000000001003', 9001, 1, N'succeeded', N'result_recorded', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                    INSERT INTO [asap].[AdditionalCopyRequest] ([SourceTitleRequestId], [LibraryOrganizationId], [LibraryNameSnapshot],
                        [BibId], [Title], [Author], [MaterialFormatId], [FormatSnapshot], [Publication], [Status], [CreatedUtc], [UpdatedUtc])
                    VALUES (@held, 2, N'Frozen native library', 9001, N'Frozen native copy', N'Copy Author', @book, N'book',
                        N'Coming soon', N'open', SYSUTCDATETIME(), SYSUTCDATETIME());
                    """;
                seed.Parameters.AddWithValue("@adminEmail", TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin.UserPrincipalName!);
                seed.Parameters.AddWithValue("@field", fieldKey);
                seed.Parameters.AddWithValue("@retired", retiredSelectKey);
                seed.Parameters.AddWithValue("@snapshot", nativeSnapshot);
                await seed.ExecuteNonQueryAsync();
            }

            var nativeSettings = TestConfigurationFactory.Create(allowedDomains: ["example.org"]);
            nativeSettings.ConnectionStrings.AsapDatabase = target;
            nativeSettings.ConnectionStrings.HangfireDatabase = target;
            var keysPath = Path.Combine(root, "keys");
            var logsPath = Path.Combine(root, "logs");
            nativeSettings.Application.DataProtectionKeysPath = keysPath;
            nativeSettings.Application.LogPath = logsPath;
            nativeSettings.Application.DataProtectionKeyEncryptionCertificateThumbprint = certificateThumbprint;
            Directory.CreateDirectory(keysPath);
            Directory.CreateDirectory(logsPath);
            var nativeSettingsPath = Path.Combine(root, "web.settings.json");
            await File.WriteAllTextAsync(nativeSettingsPath,
                JsonSerializer.Serialize(nativeSettings, new JsonSerializerOptions { WriteIndented = true }));
            await InstallHangfireForNativeBrowserAsync(target);

            // Native requests predate the fixed package export timestamp at 2030-01-02.
            timeProvider!.SetUtcNow(new DateTimeOffset(2030, 1, 3, 3, 4, 5, TimeSpan.Zero));
            await using var nativeFactory = CreateApplicationFactory(nativeSettingsPath);
            nativeFactory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
            using var client = nativeFactory.CreateClient();
            var baseOrigin = client.BaseAddress?.GetLeftPart(UriPartial.Authority)
                ?? throw new InvalidOperationException("The native-snapshot Kestrel host did not expose a base address.");
            var contextFactory = nativeFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
            long actorId;
            string actorEmail;
            long formatId;
            long nativeLibraryFormatId;
            long nativeCustomFieldId;
            long nativeFormatRuleId;
            long nativeAutoClaimRuleId;
            DateTime nativeAutoClaimCreatedUtc;
            long nativeRequestId;
            long nativeRequestWithoutHistoryId;
            byte[] nativeRequestVersion;
            long corruptRequestId;
            await using (var native = await contextFactory.CreateDbContextAsync())
            {
                var actor = await native.StaffUsers.AsNoTracking()
                    .Where(item => item.IsActive && item.Role == StaffRole.SuperAdmin)
                    .OrderBy(item => item.Id)
                    .Select(item => new { item.Id, item.UserPrincipalName })
                    .SingleAsync();
                actorId = actor.Id;
                actorEmail = actor.UserPrincipalName!;
                formatId = await native.MaterialFormats.AsNoTracking()
                    .Where(item => item.OwnerOrganizationId == 1 && item.Code == "book")
                    .Select(item => item.Id).SingleAsync();
                var nativeLibraryFormat = await native.MaterialFormats.AsNoTracking()
                    .SingleAsync(item => item.OwnerOrganizationId == 2 && item.Code == nativeLibraryFormatCode);
                nativeLibraryFormatId = nativeLibraryFormat.Id;
                var nativeCustomField = await native.PatronCustomFields.AsNoTracking()
                    .SingleAsync(item => item.LibraryOrganizationId == 2 && item.FieldKey == fieldKey);
                nativeCustomFieldId = nativeCustomField.Id;
                var nativeFormatRule = await native.MaterialFormatCustomFieldRules.AsNoTracking()
                    .SingleAsync(item => item.LibraryOrganizationId == 2 &&
                                         item.MaterialFormatId == nativeLibraryFormatId &&
                                         item.PatronCustomFieldId == nativeCustomFieldId);
                nativeFormatRuleId = nativeFormatRule.Id;
                Assert.AreEqual("required", nativeFormatRule.Mode);
                var nativeAutoClaimRule = await native.FormatAutoClaimRules.AsNoTracking()
                    .SingleAsync(item => item.LibraryOrganizationId == 2 &&
                                         item.MaterialFormatId == nativeLibraryFormatId);
                nativeAutoClaimRuleId = nativeAutoClaimRule.Id;
                nativeAutoClaimCreatedUtc = nativeAutoClaimRule.CreatedUtc;
                Assert.IsTrue(nativeAutoClaimRule.IsActive);
                nativeRequestId = await native.TitleRequests.Where(item => item.Barcode == "A20000000001001")
                    .Select(item => item.Id).SingleAsync();
                nativeRequestWithoutHistoryId = await native.TitleRequests.Where(item => item.Barcode == "A20000000001002")
                    .Select(item => item.Id).SingleAsync();
                var nativeRow = await native.TitleRequests.AsNoTracking()
                    .SingleAsync(item => item.Id == nativeRequestId);
                nativeRequestVersion = nativeRow.RowVersion.ToArray();
                using (var storedSnapshot = JsonDocument.Parse(nativeRow.CustomFieldsJson!))
                {
                    var retired = storedSnapshot.RootElement.GetProperty(retiredSelectKey);
                    Assert.AreEqual(nativeSnapshotSelectLabel, retired.GetProperty("label").GetString());
                    Assert.AreEqual("retired-choice", retired.GetProperty("value").GetString());
                    Assert.AreEqual(nativeSnapshotDisplayValue, retired.GetProperty("displayValue").GetString());
                }

                var corruptRequest = new TitleRequest
                {
                    LibraryOrganizationId = 2,
                    Barcode = $"A{Guid.NewGuid():N}"[..14],
                    Title = "Native target corrupt history",
                    MaterialFormatId = formatId,
                    Status = "suggestion",
                    AutoHold = true,
                    CustomFieldsJson = "{\"broken_history\":\"unsupported scalar\"}",
                    CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime,
                    UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime
                };
                native.TitleRequests.Add(corruptRequest);
                await native.SaveChangesAsync();
                corruptRequestId = corruptRequest.Id;
            }

            var tenantId = Guid.Parse(nativeSettings.Authentication.Entra.InitialSuperAdmin.TenantId!);
            await VerifyNativeFunctionalRuntimeAsync(nativeFactory, contextFactory, target, actorId,
                tenantId, actorEmail, fieldKey, retiredSelectKey, nativeLibraryFormatId, nativeAutoClaimRuleId);
            using (var apiClient = nativeFactory.CreateClient())
            {
                AddTestingStaffHeaders(apiClient, actorId, tenantId, actorEmail);
                apiClient.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(apiClient));
                var beforeRejected = await ReadTitleRequestSnapshotAsync(nativeRequestWithoutHistoryId, target);
                using var rejected = await apiClient.PostAsJsonAsync(
                    $"/api/asap/staff/title-requests/{nativeRequestWithoutHistoryId}/action",
                    new
                    {
                        version = beforeRejected.Version,
                        action = "edit",
                        title = "Cannot newly choose a retired native option",
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
                await AssertTitleRequestUnchangedAsync(nativeRequestWithoutHistoryId, beforeRejected, target);
            }

            var repositoryRoot = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
            var artifactDirectory = Path.Combine(repositoryRoot, ".artifacts", "browser", $"native-snapshot-{Guid.NewGuid():N}");
            Directory.CreateDirectory(artifactDirectory);
            var (browserExitCode, stdout, stderr) = await RunBrowserScriptAsync(
                repositoryRoot,
                "staff-request-contracts.cjs",
                baseOrigin,
                artifactDirectory,
                actorId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                tenantId.ToString("D"),
                actorEmail,
                nativeRequestId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                fieldKey,
                retiredSelectKey,
                corruptRequestId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                clearFieldLabel,
                retiredSelectLabel);
            Assert.AreEqual(0, browserExitCode,
                $"Browser artifacts: {artifactDirectory}{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");

            await using var verify = await contextFactory.CreateDbContextAsync();
            var persisted = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == nativeRequestId);
            Assert.AreEqual("Browser saved after clear", persisted.Title);
            Assert.IsFalse(nativeRequestVersion.SequenceEqual(persisted.RowVersion));
            using var fieldsAfterEdit = JsonDocument.Parse(persisted.CustomFieldsJson!);
            var preservedRetired = fieldsAfterEdit.RootElement.GetProperty(retiredSelectKey);
            Assert.AreEqual(nativeSnapshotSelectLabel, preservedRetired.GetProperty("label").GetString());
            Assert.AreEqual("select", preservedRetired.GetProperty("type").GetString());
            Assert.AreEqual("retired-choice", preservedRetired.GetProperty("value").GetString());
            Assert.AreEqual(nativeSnapshotDisplayValue, preservedRetired.GetProperty("displayValue").GetString());
            Assert.AreEqual(nativeRetiredHistoryValue,
                fieldsAfterEdit.RootElement.GetProperty("retired_history").GetProperty("value").GetString());
            Assert.IsFalse(fieldsAfterEdit.RootElement.TryGetProperty(fieldKey, out _));
            Assert.AreEqual(1, await verify.TitleRequestEvents.CountAsync(item =>
                item.TitleRequestId == nativeRequestId && item.EventType == "request_edited"));

            using var resetClient = nativeFactory.CreateClient();
            AddTestingStaffHeaders(resetClient, actorId, tenantId, actorEmail);
            resetClient.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(resetClient));
            await using (var seedOverrides = await contextFactory.CreateDbContextAsync())
            {
                var workflow = await seedOverrides.WorkflowSettings.SingleAsync(item => item.OrganizationId == 2);
                workflow.SuggestionLimitMessage = "Temporary native browser override";
                workflow.UpdatedUtc = timeProvider!.GetUtcNow().UtcDateTime;
                seedOverrides.MaterialFormatOverrides.Add(new MaterialFormatOverride
                {
                    LibraryOrganizationId = 2,
                    MaterialFormatId = formatId,
                    Label = "Temporary native browser book override"
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
                .SingleAsync(item => item.Id == nativeLibraryFormatId);
            Assert.AreEqual(2, preservedFormat.OwnerOrganizationId);
            Assert.AreEqual(nativeLibraryFormatCode, preservedFormat.Code);
            Assert.IsTrue(await afterReset.PatronCustomFields.AsNoTracking().AnyAsync(item =>
                item.Id == nativeCustomFieldId && item.LibraryOrganizationId == 2 && item.FieldKey == fieldKey));
            Assert.IsTrue(await afterReset.MaterialFormatCustomFieldRules.AsNoTracking().AnyAsync(item =>
                item.Id == nativeFormatRuleId && item.LibraryOrganizationId == 2 &&
                item.MaterialFormatId == nativeLibraryFormatId && item.PatronCustomFieldId == nativeCustomFieldId &&
                item.Mode == "required"));
            var preservedAutoClaimRule = await afterReset.FormatAutoClaimRules.AsNoTracking()
                .SingleAsync(item => item.Id == nativeAutoClaimRuleId);
            Assert.AreEqual(2, preservedAutoClaimRule.LibraryOrganizationId);
            Assert.AreEqual(nativeLibraryFormatId, preservedAutoClaimRule.MaterialFormatId);
            Assert.AreEqual(nativeAutoClaimCreatedUtc, preservedAutoClaimRule.CreatedUtc);
            Assert.IsTrue(preservedAutoClaimRule.IsActive);
            var requestAfterReset = await afterReset.TitleRequests.AsNoTracking()
                .SingleAsync(item => item.Id == nativeRequestId);
            Assert.AreEqual("Browser saved after clear", requestAfterReset.Title);
            using var historyAfterReset = JsonDocument.Parse(requestAfterReset.CustomFieldsJson!);
            Assert.AreEqual(nativeSnapshotDisplayValue,
                historyAfterReset.RootElement.GetProperty(retiredSelectKey).GetProperty("displayValue").GetString());
            Assert.AreEqual(nativeRetiredHistoryValue,
                historyAfterReset.RootElement.GetProperty("retired_history").GetProperty("value").GetString());
            Assert.AreEqual(1, await afterReset.TitleRequestEvents.CountAsync(item =>
                item.TitleRequestId == nativeRequestId && item.EventType == "request_edited"));
        }
        finally
        {
            try
            {
                await DropNativeBrowserDatabaseAsync(database);
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
                        throw new InvalidOperationException("Refusing to remove the native-browser fixture outside the temporary directory.");
                    }
                    Directory.Delete(root, recursive: true);
                }
            }
        }
    }

    private static void DeployNativeBrowserDacpac(string databaseName)
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

    private static async Task InstallHangfireForNativeBrowserAsync(string connectionString)
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

    private static async Task DropNativeBrowserDatabaseAsync(string databaseName)
    {
        if (!databaseName.StartsWith("AsapNativeBrowser_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to drop a database outside the isolated browser-native fixture.");
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
