using System.Diagnostics;
using System.Text.Json;
using Asap.Web.Infrastructure.Testing;
using Microsoft.Data.SqlClient;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Campaign2SettingsEditorPreservesIdentityOwnershipAndConditionalFieldsThroughSqlAndReload(bool libraryAdmin)
    {
        factory!.UseKestrel(0);
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        using var client = factory.CreateClient();
        AddTestingStaffHeaders(client, superAdmin.Id, superAdmin.EntraTenantId, superAdmin.AuthenticationEmail);
        var libraryId = await ReserveSettingsCourseLibraryIdAsync();
        var originalFormat = await ReadSystemFormatEvidenceAsync("book");
        var originalIdentity = 0L;
        object originalSuccessMessage = DBNull.Value;
        var auditBefore = await ReadAuditHighWatermarkAsync();
        var outboxBefore = await ReadCountAsync("SELECT COUNT(*) FROM [asap].[EmailOutbox];");
        var suffix = Guid.NewGuid().ToString("N");
        var email = $"campaign2-{suffix}@example.org";
        var staffId = superAdmin.Id;
        Assert.AreEqual(0, await ReadCountAsync("""
            SELECT COUNT(*) FROM [asap].[EmailTemplate] WHERE [OrganizationId] = 1
              AND [TemplateKey] IN (N'rejection:template_1', N'rejection:template_2', N'rejection:template_3');
            """), "The isolated fixture must not replace an existing named-template witness.");
        try
        {
            await using (var connection = new SqlConnection(databaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT CONVERT(bigint, IDENT_CURRENT(N'[asap].[EmailTemplate]'));";
                originalIdentity = (long)(await command.ExecuteScalarAsync())!;
                command.CommandText = "SELECT [SuccessMessage] FROM [asap].[PatronSettings] WHERE [OrganizationId] = 1;";
                originalSuccessMessage = (await command.ExecuteScalarAsync())!;
            }
            await ExecuteNonQueryAsync("""
                INSERT INTO [asap].[Organization]
                    ([Id], [DisplayName], [Abbreviation], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
                VALUES (@org, N'Campaign 2 library', N'C2', 2, 1, 1);
                SET IDENTITY_INSERT [asap].[EmailTemplate] ON;
                INSERT INTO [asap].[EmailTemplate]
                    ([Id], [OrganizationId], [TemplateKey], [DisplayName], [SubjectTemplate], [BodyTemplate], [IsHidden], [IsCustom], [SortOrder])
                VALUES (9007199254740993, 1, N'rejection:template_1', N'Campaign 2 first', N'First {{title}}', N'First body {{title}}', 0, 0, 9810),
                       (9007199254740995, 1, N'rejection:template_3', N'Campaign 2 third', N'Third {{title}}', N'Third body {{title}}', 0, 0, 9830);
                SET IDENTITY_INSERT [asap].[EmailTemplate] OFF;
                """, ("@org", libraryId));
            if (libraryAdmin)
            {
                await using var connection = new SqlConnection(databaseConnectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO [asap].[StaffUser]
                        ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                         [DisplayName], [NotificationEmail], [Role], [OrganizationId], [IsActive])
                    OUTPUT INSERTED.[Id]
                    VALUES (@tenant, NEWID(), @email, UPPER(@email), N'Campaign 2 admin', @email, N'admin', @org, 1);
                    """;
                command.Parameters.AddWithValue("@tenant", superAdmin.EntraTenantId);
                command.Parameters.AddWithValue("@email", email);
                command.Parameters.AddWithValue("@org", libraryId);
                staffId = (long)(await command.ExecuteScalarAsync())!;
            }

            var repository = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
            var artifacts = Path.Combine(repository, ".artifacts", "browser", $"settings-campaign2-{suffix}");
            Directory.CreateDirectory(artifacts);
            var startInfo = new ProcessStartInfo
            {
                FileName = "node", WorkingDirectory = repository,
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            };
            foreach (var argument in new[]
            {
                Path.Combine(repository, "tests", "browser", "settings.cjs"),
                client.BaseAddress!.GetLeftPart(UriPartial.Authority), artifacts, staffId.ToString(),
                superAdmin.EntraTenantId.ToString(), libraryAdmin ? email : superAdmin.AuthenticationEmail,
                libraryAdmin ? "campaign2-library-admin" : "campaign2", libraryId.ToString(), suffix
            })
            {
                startInfo.ArgumentList.Add(argument);
            }
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the Campaign 2 Settings browser runner.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                throw;
            }
            var output = await stdout + Environment.NewLine + await stderr;
            Assert.AreEqual(0, process.ExitCode, output);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(artifacts, "settings-browser-results.json")));
            Assert.IsTrue(report.RootElement.GetProperty(libraryAdmin ? "campaign2LibraryAdmin" : "campaign2SystemAndInheritance").GetBoolean());
            Assert.AreEqual(0, report.RootElement.GetProperty("externalRequests").GetInt32());
            Assert.AreEqual(0, report.RootElement.GetProperty("pageErrors").GetArrayLength());
            Assert.HasCount(7, report.RootElement.GetProperty("states").EnumerateArray().ToArray());
            Assert.IsTrue(report.RootElement.GetProperty("patronTextRoundTrip").GetBoolean());
            if (!libraryAdmin)
            {
                Assert.IsTrue(report.RootElement.GetProperty("providerIdentityRoundTrip").GetBoolean());
            }
            Assert.AreEqual(outboxBefore, await ReadCountAsync("SELECT COUNT(*) FROM [asap].[EmailOutbox];"));
            Assert.AreEqual(1, await ReadCountWithParameterAsync("""
                SELECT COUNT(*) FROM [asap].[EmailTemplate]
                WHERE [OrganizationId] = @org AND [SourceTemplateId] = @source
                  AND [SubjectTemplate] IS NULL AND [BodyTemplate] = @body AND [IsCustom] = 0;
                """, ("@org", libraryId), ("@source", libraryAdmin ? 9007199254740993L : 9007199254740996L),
                ("@body", libraryAdmin ? "Library admin {{title}}" : "Library body: {{title}}")),
                "The actual editor must preserve sparse library lineage in authoritative SQL.");
            if (!libraryAdmin)
            {
                Assert.AreEqual(1, await ReadCountWithParameterAsync("""
                    SELECT COUNT(*) FROM [asap].[EmailTemplate]
                    WHERE [Id] = 9007199254740996 AND [OrganizationId] = 1 AND [TemplateKey] = N'rejection:template_2'
                      AND [SubjectTemplate] = N'Updated System subject {{title}}' AND [BodyTemplate] = @body;
                    """, ("@body", "First\n{{title}} & last")));
                var savedFormat = await ReadSystemFormatEvidenceAsync("book");
                Assert.AreEqual("none", savedFormat.MessageBehavior);
                Assert.AreEqual("Draft conditional text & é", savedFormat.Message);
            }
        }
        finally
        {
            await ExecuteNonQueryAsync("""
                DELETE FROM [asap].[EmailTemplate] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[EmailTemplate] WHERE [OrganizationId] = 1
                  AND [TemplateKey] IN (N'rejection:template_1', N'rejection:template_2', N'rejection:template_3');
                DELETE FROM [asap].[AdministrativeAudit] WHERE [Id] > @auditBefore;
                DELETE FROM [asap].[StaffUser] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[EmailSettings] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[PatronSettings] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[ExternalSearchProviderOverride] WHERE [LibraryOrganizationId] = @org;
                DELETE FROM [asap].[ExternalSearchProvider] WHERE [Label] LIKE @providerLabel;
                UPDATE [asap].[PatronSettings] SET [SuccessMessage] = @success WHERE [OrganizationId] = 1;
                DELETE FROM [asap].[WorkflowSettings] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[Organization] WHERE [Id] = @org;
                UPDATE [asap].[MaterialFormat] SET [MessageBehavior] = @behavior, [Message] = @message
                WHERE [OwnerOrganizationId] = 1 AND [Code] = N'book';
                """, ("@org", libraryId), ("@auditBefore", auditBefore),
                ("@behavior", (object?)originalFormat.MessageBehavior ?? DBNull.Value),
                ("@message", (object?)originalFormat.Message ?? DBNull.Value),
                ("@success", originalSuccessMessage), ("@providerLabel", $"UI refinements provider {suffix}%"));
            if (originalIdentity > 0)
            {
                await ExecuteNonQueryAsync($"DBCC CHECKIDENT (N'[asap].[EmailTemplate]', RESEED, {originalIdentity}) WITH NO_INFOMSGS;");
            }
        }
    }
}
