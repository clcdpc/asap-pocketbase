using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
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

        var (exitCode, stdout, stderr) = await RunBrowserScriptAsync(
            repositoryRoot,
            "patron-submit-session.cjs",
            baseOrigin,
            artifactDirectory);
        Assert.AreEqual(0, exitCode, $"{stdout}{Environment.NewLine}{stderr}");

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(artifactDirectory, "browser-results.json")));
        var root = report.RootElement;
        Assert.HasCount(5, root.GetProperty("races").EnumerateArray().ToArray());
        Assert.HasCount(1, root.GetProperty("unknownOutcomes").EnumerateArray().ToArray());
        Assert.HasCount(6, root.GetProperty("states").EnumerateArray().ToArray());
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
