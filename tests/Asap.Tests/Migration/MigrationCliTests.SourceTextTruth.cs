using Asap.Migration;
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace Asap.Tests.Migration;

public sealed partial class MigrationCliTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task ImportAndReconcilePreserveRawCurrentAndAcceptedHistoryText()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-source-text-truth-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationSourceTextTruth_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        Directory.CreateDirectory(root);

        async Task<int> SetRequestIdentifier(string identifier)
        {
            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE request SET [Identifier] = @identifier FROM [asap].[LegacyPocketBaseMapping] mapping JOIN [asap].[TitleRequest] request ON request.[Id] = mapping.[NewId] WHERE mapping.[EntityType] = N'title_request' AND mapping.[PocketBaseId] = N'request-raw-1';";
            command.Parameters.AddWithValue("@identifier", identifier);
            return await command.ExecuteNonQueryAsync();
        }

        try
        {
            var package = CreateMinimalPackage(
                root,
                """
                CREATE TABLE [material_formats]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL, [libraryOrganization] TEXT,
                    [code] TEXT NOT NULL, [label] TEXT NOT NULL, [enabled] INTEGER NOT NULL, [sortOrder] INTEGER NOT NULL
                );
                INSERT INTO [material_formats] VALUES ('fmt-book', 'system', NULL, 'book', 'Book', 1, 10);
                CREATE TABLE [title_requests]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [libraryOrgId] TEXT NOT NULL, [formatRef] TEXT,
                    [barcode] TEXT NOT NULL, [title] TEXT NOT NULL, [email] TEXT, [identifier] TEXT,
                    [claimedByStaffUserId] TEXT, [claimedByDisplayName] TEXT, [claimedAt] TEXT, [claimType] TEXT,
                    [autohold] INTEGER NOT NULL, [status] TEXT NOT NULL, [created] TEXT NOT NULL, [updated] TEXT NOT NULL
                );
                INSERT INTO [title_requests] VALUES
                    ('request-raw-1', '2', 'fmt-book', 'A20000000000001', 'Raw request text',
                     ' patron@example.org ', '  REQUEST-IDENTIFIER  ', 'missing-staff', ' Historical Request Claimant ',
                     '2029-01-01T12:00:00Z', ' manual ', 0, 'suggestion',
                     '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z');
                CREATE TABLE [title_request_events]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [titleRequest] TEXT NOT NULL, [eventType] TEXT NOT NULL,
                    [fromStatus] TEXT, [toStatus] TEXT, [closeReason] TEXT, [actorType] TEXT NOT NULL,
                    [actorName] TEXT, [message] TEXT, [metadata] TEXT, [created] TEXT NOT NULL
                );
                INSERT INTO [title_request_events] VALUES
                    ('event-raw-1', 'request-raw-1', ' status_changed ', NULL, NULL, NULL, 'system',
                     ' Event Actor ', ' Event message ', NULL, '2029-01-03T00:00:00Z');
                CREATE TABLE [additional_copy_requests]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [sourceTitleRequest] TEXT, [libraryOrgId] TEXT NOT NULL,
                    [bibid] TEXT NOT NULL, [title] TEXT NOT NULL, [identifier] TEXT, [format] TEXT,
                    [status] TEXT NOT NULL, [createdByStaff] TEXT, [createdByUsername] TEXT,
                    [closedByStaff] TEXT, [closedByUsername] TEXT, [closedAt] TEXT,
                    [claimedByStaffUserId] TEXT, [claimedByDisplayName] TEXT, [claimedAt] TEXT,
                    [created] TEXT NOT NULL, [updated] TEXT NOT NULL
                );
                INSERT INTO [additional_copy_requests] VALUES
                    ('copy-raw-1', 'request-raw-1', '2', '7001', 'Raw additional copy',
                     '  COPY-IDENTIFIER  ', ' book ', 'closed', 'pb-staff-1', ' Historical Creator ',
                     'pb-staff-1', ' Closing Staff ', '2029-01-04T00:00:00Z',
                     'missing-staff', ' Historical Request Claimant ', '2029-01-01T12:00:00Z',
                     '2029-01-02T00:00:00Z', '2029-01-04T00:00:00Z');
                CREATE TABLE [email_templates]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL, [libraryOrganization] TEXT,
                    [templateKey] TEXT NOT NULL, [name] TEXT, [subject] TEXT, [body] TEXT,
                    [enabled] INTEGER NOT NULL, [sourceTemplateId] TEXT
                );
                INSERT INTO [email_templates] VALUES
                    ('template-raw-1', 'system', NULL, 'suggestion_submitted', 'Submission receipt',
                     'Received: {{title}}', '<p>Hello {{name}}</p>', 1, NULL);
                CREATE TABLE [email_delivery_events]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [titleRequest] TEXT, [emailTemplate] TEXT,
                    [templateKey] TEXT, [recipient] TEXT, [subject] TEXT, [status] TEXT,
                    [error] TEXT, [metadata] TEXT, [created] TEXT NOT NULL
                );
                INSERT INTO [email_delivery_events] VALUES
                    ('mail-raw-1', 'request-raw-1', 'template-raw-1', ' suggestion_submitted ',
                     ' patron@example.org ', 'Received: Raw request text', ' sent ', NULL,
                     '{"transport":"smtp"}', '2029-01-06T00:00:00Z');
                CREATE TABLE [deleted_request_audit]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [titleRequestId] TEXT NOT NULL,
                    [libraryOrgId] TEXT NOT NULL, [barcode] TEXT, [title] TEXT, [identifier] TEXT,
                    [bibid] TEXT, [status] TEXT NOT NULL, [closeReason] TEXT, [deletedAt] TEXT NOT NULL,
                    [deletedByStaff] TEXT, [deletedByUsername] TEXT, [snapshot] TEXT
                );
                INSERT INTO [deleted_request_audit] VALUES
                    ('deleted-raw-1', 'original-request-1', '2', 'A20000000000002', 'Deleted raw request',
                     '  DELETED-IDENTIFIER  ', '7002', 'closed', 'manual', '2029-01-05T00:00:00Z',
                     NULL, ' Deleted By User ', '{"created":"2029-01-01T00:00:00Z"}');
                """);
            var report = Path.Combine(root, "report.json");
            DeployDacpac(master, databaseName);
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);

            var failures = new List<string>();
            using (var validationError = new StringWriter())
            {
                var validationExitCode = MigrationCli.Run(
                    ["validate", "--package", package],
                    TextWriter.Null,
                    validationError);
                if (validationExitCode != 0)
                {
                    failures.Add("Public validation rejected the canonical raw-text source fixture.");
                }
            }
            using (var importError = new StringWriter())
            {
                var importExitCode = RunImport(package, report, connectionEnvironmentName, tenantId, importError);
                if (importExitCode != 0)
                {
                    failures.Add("Public import rejected the canonical raw-text source fixture.");
                }
            }

            if (File.Exists(report))
            {
                using (var reportDocument = JsonDocument.Parse(await File.ReadAllTextAsync(report)))
                {
                    var transformations = reportDocument.RootElement.GetProperty("transformations").EnumerateArray().ToArray();
                    var requestClaim = transformations.Single(item =>
                        item.GetProperty("entity").GetString() == "title_request_claim" &&
                        item.GetProperty("sourceId").GetString() == "request-raw-1");
                    var copyClaim = transformations.Single(item =>
                        item.GetProperty("entity").GetString() == "additional_copy_claim" &&
                        item.GetProperty("sourceId").GetString() == "copy-raw-1");
                    if (!string.Equals(
                            requestClaim.GetProperty("sourceDisplayName").GetString(),
                            " Historical Request Claimant ",
                            StringComparison.Ordinal) ||
                        !string.Equals(requestClaim.GetProperty("sourceClaimType").GetString(), " manual ", StringComparison.Ordinal) ||
                        !string.Equals(
                            copyClaim.GetProperty("sourceDisplayName").GetString(),
                            " Historical Request Claimant ",
                            StringComparison.Ordinal))
                    {
                        failures.Add("Claim transformations did not retain exact source display and type provenance.");
                    }
                }

                await using (var connection = new SqlConnection(target))
                {
                    await connection.OpenAsync();
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        "SELECT request.[Email], request.[Identifier], event.[ActorName], event.[Message], copy.[Identifier], copy.[CreatedByDisplayName], copy.[ClosedByDisplayName], deleted.[Identifier], deleted.[DeletedByDisplayName], event.[EventType], JSON_VALUE(event.[MetadataJson], '$.sourceEventType'), copy.[FormatSnapshot], copy.[ClaimedByDisplayName], mail.[EventType], JSON_VALUE(mail.[MetadataJson], '$.templateKey'), JSON_VALUE(mail.[MetadataJson], '$.recipient'), JSON_VALUE(mail.[MetadataJson], '$.sourceStatus'), JSON_VALUE(claimAnnotation.[MetadataJson], '$.sourceDisplayName'), JSON_VALUE(claimAnnotation.[MetadataJson], '$.sourceClaimType') FROM [asap].[LegacyPocketBaseMapping] requestMap JOIN [asap].[TitleRequest] request ON request.[Id] = requestMap.[NewId] JOIN [asap].[LegacyPocketBaseMapping] eventMap ON eventMap.[EntityType] = N'title_request_event' AND eventMap.[PocketBaseId] = N'event-raw-1' JOIN [asap].[TitleRequestEvent] event ON event.[Id] = eventMap.[NewId] JOIN [asap].[LegacyPocketBaseMapping] copyMap ON copyMap.[EntityType] = N'additional_copy' AND copyMap.[PocketBaseId] = N'copy-raw-1' JOIN [asap].[AdditionalCopyRequest] copy ON copy.[Id] = copyMap.[NewId] JOIN [asap].[LegacyPocketBaseMapping] deletedMap ON deletedMap.[EntityType] = N'deleted_request_audit' AND deletedMap.[PocketBaseId] = N'deleted-raw-1' JOIN [asap].[DeletedRequestAudit] deleted ON deleted.[Id] = deletedMap.[NewId] JOIN [asap].[LegacyPocketBaseMapping] mailMap ON mailMap.[EntityType] = N'email_delivery_event' AND mailMap.[PocketBaseId] = N'mail-raw-1' JOIN [asap].[EmailDeliveryEvent] mail ON mail.[Id] = mailMap.[NewId] OUTER APPLY (SELECT TOP (1) claimEvent.[MetadataJson] FROM [asap].[TitleRequestEvent] claimEvent WHERE claimEvent.[TitleRequestId] = request.[Id] AND JSON_VALUE(claimEvent.[MetadataJson], '$.transform') = N'claim_attribution_normalization_v1') claimAnnotation WHERE requestMap.[EntityType] = N'title_request' AND requestMap.[PocketBaseId] = N'request-raw-1';";
                    await using var reader = await command.ExecuteReaderAsync();
                    var expected = new string?[]
                    {
                        " patron@example.org ",
                        "  REQUEST-IDENTIFIER  ",
                        " Event Actor ",
                        " Event message ",
                        "  COPY-IDENTIFIER  ",
                        " Historical Creator ",
                        " Closing Staff ",
                        "  DELETED-IDENTIFIER  ",
                        " Deleted By User ",
                        "status_changed",
                        " status_changed ",
                        " book ",
                        " Historical Request Claimant ",
                        "sent",
                        " suggestion_submitted ",
                        " patron@example.org ",
                        " sent ",
                        " Historical Request Claimant ",
                        " manual "
                    };
                    if (!await reader.ReadAsync())
                    {
                        failures.Add("The current and accepted-history source rows did not all map to SQL.");
                    }
                    else
                    {
                        for (var index = 0; index < expected.Length; index++)
                        {
                            var actual = reader.IsDBNull(index) ? null : reader.GetString(index);
                            if (!string.Equals(expected[index], actual, StringComparison.Ordinal))
                            {
                                failures.Add($"Mapped raw-text projection {index + 1} differs from the immutable SQLite source value.");
                            }
                        }
                        if (await reader.ReadAsync())
                        {
                            failures.Add("Current and accepted-history source mappings produced duplicate joined rows.");
                        }
                    }
                }

                var originalTargetFingerprint = ComputeTargetFingerprintForTest(target);
                var reportBeforeDrift = await File.ReadAllBytesAsync(report);
                using (var reconcileError = new StringWriter())
                {
                    if (RunReconcile(package, report, connectionEnvironmentName, reconcileError) != 0)
                    {
                        failures.Add("Independent reconciliation rejected the authentic raw-text source projection.");
                    }
                }

                if (await SetRequestIdentifier("drifted-request-identifier") != 1)
                {
                    failures.Add("The request text drift fixture did not update exactly one mapped SQL row.");
                }
                var driftFingerprint = ComputeTargetFingerprintForTest(target);
                RefreshReportFingerprint(report, target);
                var reportWithRefreshedDriftFingerprint = await File.ReadAllBytesAsync(report);
                using (var driftError = new StringWriter())
                {
                    if (RunReconcile(package, report, connectionEnvironmentName, driftError) != 1)
                    {
                        failures.Add("Independent reconciliation accepted changed target request text.");
                    }
                    if (!driftError.ToString().Contains(
                            "A target request's BIB authority fields differ from the immutable source classification inputs.",
                            StringComparison.Ordinal))
                    {
                        failures.Add("Changed request text was rejected without the independent source-to-row mismatch diagnostic.");
                    }
                }
                if (!string.Equals(driftFingerprint, ComputeTargetFingerprintForTest(target), StringComparison.Ordinal))
                {
                    failures.Add("Failed raw-text reconciliation repaired or otherwise changed target SQL.");
                }
                var reportAfterDriftReconcile = await File.ReadAllBytesAsync(report);
                if (!reportWithRefreshedDriftFingerprint.SequenceEqual(reportAfterDriftReconcile))
                {
                    failures.Add("Failed raw-text reconciliation rewrote the report after its drift fingerprint was refreshed.");
                }

                if (await SetRequestIdentifier("  REQUEST-IDENTIFIER  ") != 1)
                {
                    failures.Add("The exact raw request text could not be restored after the negative reconciliation case.");
                }
                if (!string.Equals(
                        ComputeTargetFingerprintForTest(target),
                        originalTargetFingerprint,
                        StringComparison.Ordinal))
                {
                    failures.Add("Restoring the exact source identifier did not restore the original target SQL fingerprint.");
                }
                await File.WriteAllBytesAsync(report, reportBeforeDrift);
                using (var restoredError = new StringWriter())
                {
                    if (RunReconcile(package, report, connectionEnvironmentName, restoredError) != 0)
                    {
                        failures.Add("Independent reconciliation rejected the restored exact raw-text projection.");
                    }
                }
            }
            else
            {
                failures.Add("Public import did not create the report required for independent reconciliation.");
            }

            Assert.AreEqual(
                string.Empty,
                string.Join(Environment.NewLine, failures),
                "Current and accepted historical raw text must survive source export/import and be independently reconciled without repair.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionEnvironmentName, null);
            await DropDatabaseAsync(master, databaseName);
            Directory.Delete(root, recursive: true);
        }
    }
}
