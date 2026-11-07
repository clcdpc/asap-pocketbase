using System.Globalization;
using System.Text.Json;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [TestCategory(TestCategories.Browser)]
    public async Task SubmissionSearchBrowserVerifiesScopedResultsGuardedDetailsAndAccessibleLayouts()
    {
        factory!.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        using var client = factory.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        await SeedSubmissionSearchAsync();
        var row = await CreateCorrectiveStaffAsync(actor, StaffRole.Staff, SearchLibrary);
        var ordinary = await ReadCorrectiveStaffAsync(row);
        var secondRow = await CreateCorrectiveStaffAsync(actor, StaffRole.Staff, SearchLibrary);
        var second = await ReadCorrectiveStaffAsync(secondRow);
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var root = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
        var ciResults = Environment.GetEnvironmentVariable("ASAP_CI_BROWSER_RESULTS");
        var evidenceRoot = Environment.GetEnvironmentVariable("ASAP_SEARCH_EVIDENCE_DIR") ??
            (string.IsNullOrWhiteSpace(ciResults) ? Path.Combine(root, ".artifacts", "browser") : Path.GetDirectoryName(Path.GetFullPath(ciResults))!);
        var directory = Path.Combine(evidenceRoot, $"submission-search-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var before = await SearchStateAsync();
        var provider = factory.Services.GetRequiredService<DeterministicTestingPatronProvider>();
        var calls = provider.Calls.Count;
        var readOnlyVerified = false;
        await File.WriteAllTextAsync(Path.Combine(directory, "fixture.json"), JsonSerializer.Serialize(new
        {
            library = SearchLibrary.ToString(CultureInfo.InvariantCulture), otherLibrary = OtherSearchLibrary.ToString(CultureInfo.InvariantCulture),
            bigId = SearchBigId.ToString(CultureInfo.InvariantCulture),
            super = new { id = actor.Id.ToString(CultureInfo.InvariantCulture), tenantId = actor.EntraTenantId.ToString(), email = actor.AuthenticationEmail },
            ordinary = new { id = ordinary.Id.ToString(CultureInfo.InvariantCulture), tenantId = ordinary.EntraTenantId.ToString(), email = ordinary.AuthenticationEmail }
        }));
        using var stop = new CancellationTokenSource();
        var commands = Task.Run(async () =>
        {
            var path = Path.Combine(directory, "command.json");
            while (!stop.IsCancellationRequested)
            {
                if (!File.Exists(path)) { await Task.Delay(20, stop.Token); continue; }
                using var command = JsonDocument.Parse(await File.ReadAllTextAsync(path, stop.Token));
                File.Delete(path);
                switch (command.RootElement.GetProperty("kind").GetString())
                {
                    case "readOnly":
                        CollectionAssert.AreEqual(before, await SearchStateAsync());
                        if (!readOnlyVerified)
                        {
                            Assert.AreEqual(calls, provider.Calls.Count, "Initial search, copy selection and BIB-bearing title selection invoke no provider.");
                        }
                        Assert.IsTrue(provider.Calls.Skip(calls).All(item => item.Operation == TestingPolarisOperation.PatronCodes),
                            "Only explicit Settings reference-list GETs may read the deterministic reference provider.");
                        Assert.HasCount(0, dispatcher!.EnqueuedIds);
                        readOnlyVerified = true;
                        break;
                    case "changeTitle":
                        await ExecuteNonQueryAsync("UPDATE [asap].[TitleRequest] SET [Status]='suggestion',[CloseReason]=NULL WHERE [Id]=@id AND [LibraryOrganizationId]=46701;", ("@id", SearchBigId));
                        break;
                    case "deleteTitle":
                        await ExecuteNonQueryAsync("DELETE FROM [asap].[TitleRequest] WHERE [LibraryOrganizationId]=46701 AND [Barcode]='needle-closed';");
                        break;
                    default: Assert.Fail("Unknown search fixture command."); break;
                }
                await File.WriteAllTextAsync(Path.Combine(directory, "command-done"), "done", stop.Token);
            }
        }, stop.Token);
        try
        {
            var (code, stdout, stderr) = await RunBrowserScriptAsync(root, "submission-search.cjs", client.BaseAddress!.GetLeftPart(UriPartial.Authority), directory);
            if (commands.IsFaulted) { await commands; }
            Assert.AreEqual(0, code, $"{directory}\n{stdout}\n{stderr}");
            Assert.IsTrue(readOnlyVerified);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "browser-results.json")));
            Assert.IsTrue(report.RootElement.GetProperty("dirtySettings").GetBoolean());
            Assert.IsTrue(report.RootElement.GetProperty("dirtyTitle").GetBoolean());
            Assert.IsTrue(report.RootElement.GetProperty("dirtyCopy").GetBoolean());
            Assert.IsTrue(report.RootElement.GetProperty("staleDetails").GetBoolean());
            Assert.AreEqual(2, report.RootElement.GetProperty("nativeZoom").GetArrayLength());
            Assert.AreEqual(report.RootElement.GetProperty("referenceReads").GetArrayLength(), provider.Calls.Count - calls,
                "Every provider read must correspond to an explicit existing Settings reference-list HTTP read, never search or selection.");
            Assert.IsTrue(provider.Calls.Skip(calls).All(item => item.Operation == TestingPolarisOperation.PatronCodes));
            Assert.HasCount(0, dispatcher!.EnqueuedIds);
            TestContext.WriteLine($"Search rendered application evidence: {directory}");
        }
        finally
        {
            await stop.CancelAsync();
            try { await commands; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            await DeactivateCorrectiveStaffAsync(ordinary.Id);
            await DeactivateCorrectiveStaffAsync(second.Id);
            await ClearSubmissionSearchAsync();
        }
    }
}
