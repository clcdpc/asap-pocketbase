using System.Globalization;
using System.Text.Json;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [TestCategory(TestCategories.Browser)]
    public async Task SharedClaimsBrowserRetainsExplicitSelectionAcrossBothSqlQueuesAndActorReloads()
    {
        const int library = 45701;
        const int otherLibrary = 45702;
        string[] stages = ["suggestion", "outstanding_purchase", "pending_hold", "hold_placed", "closed"];
        factory!.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        using var client = factory.CreateClient();
        var authorizer = await ReadConfiguredSuperAdminAsync();
        await ExecuteNonQueryAsync("""
            INSERT INTO [asap].[Organization] ([Id],[DisplayName],[OrganizationCodeId],[ParentOrganizationId],[IsActive])
            VALUES (@library,N'Claims library',2,1,1),(@other,N'Other Claims library',2,1,1);
            """, ("@library", library), ("@other", otherLibrary));
        var firstRow = await CreateCorrectiveStaffAsync(authorizer, StaffRole.SuperAdmin, 1);
        var secondRow = await CreateCorrectiveStaffAsync(authorizer, StaffRole.Staff, library);
        var first = await ReadCorrectiveStaffAsync(firstRow);
        var second = await ReadCorrectiveStaffAsync(secondRow);
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        long mutationId = 0;
        var root = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
        var artifacts = Path.Combine(root, ".artifacts", "browser", $"shared-claims-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifacts);
        try
        {
            await using (var setup = await contexts.CreateDbContextAsync())
            {
                await setup.StaffUsers.Where(item => item.Id == first.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.DefaultMineUnclaimedFilter, true));
                var format = await setup.MaterialFormats.SingleAsync(item => item.OwnerOrganizationId == 1 && item.Code == "book");
                var now = timeProvider!.GetUtcNow().UtcDateTime;
                foreach (var organization in new[] { library, otherLibrary })
                {
                    foreach (var claimant in new long?[] { first.Id, second.Id, null })
                    {
                        foreach (var status in stages)
                        {
                            setup.TitleRequests.Add(new TitleRequest
                            {
                                LibraryOrganizationId = organization, Barcode = $"claims-{organization}-{status}-{claimant}",
                                Title = $"Claims {organization} {status} {claimant?.ToString(CultureInfo.InvariantCulture) ?? "unclaimed"}",
                                MaterialFormatId = format.Id, Status = status, AutoHold = false,
                                BibId = status is "pending_hold" or "hold_placed" ? 457900 : null,
                                BibIdStaffVerified = status is "pending_hold" or "hold_placed",
                                CloseReason = status == "closed" ? "manual" : null,
                                ClaimedByStaffUserId = claimant, ClaimedByDisplayName = claimant.HasValue ? $"Claims staff {claimant}" : null,
                                ClaimedAtUtc = claimant.HasValue ? now : null, ClaimType = claimant.HasValue ? "manual" : null,
                                CreatedUtc = now, UpdatedUtc = now
                            });
                        }
                        foreach (var status in new[] { "open", "closed" })
                        {
                            setup.AdditionalCopyRequests.Add(new AdditionalCopyRequest
                            {
                                LibraryOrganizationId = organization, BibId = 457900, MaterialFormatId = format.Id,
                                Title = $"Claims copy {organization} {status} {claimant?.ToString(CultureInfo.InvariantCulture) ?? "unclaimed"}",
                                Status = status, ClosedUtc = status == "closed" ? now : null,
                                ClaimedByStaffUserId = claimant, ClaimedByDisplayName = claimant.HasValue ? $"Claims staff {claimant}" : null,
                                ClaimedAtUtc = claimant.HasValue ? now : null, ClaimType = claimant.HasValue ? "manual" : null,
                                CreatedUtc = now, UpdatedUtc = now
                            });
                        }
                    }
                }
                await setup.SaveChangesAsync();
                var titles = await setup.TitleRequests.Where(item => item.LibraryOrganizationId == library || item.LibraryOrganizationId == otherLibrary)
                    .OrderBy(item => item.Id).ToListAsync();
                var copies = await setup.AdditionalCopyRequests.Where(item => item.LibraryOrganizationId == library || item.LibraryOrganizationId == otherLibrary)
                    .OrderBy(item => item.Id).ToListAsync();
                mutationId = titles.Single(item => item.LibraryOrganizationId == library && item.Status == "suggestion" && item.ClaimedByStaffUserId == null).Id;
                await File.WriteAllTextAsync(Path.Combine(artifacts, "fixture.json"), JsonSerializer.Serialize(new
                {
                    library = library.ToString(CultureInfo.InvariantCulture), otherLibrary = otherLibrary.ToString(CultureInfo.InvariantCulture),
                    mutationId = mutationId.ToString(CultureInfo.InvariantCulture),
                    first = new { id = first.Id.ToString(CultureInfo.InvariantCulture), tenantId = first.EntraTenantId.ToString(), email = first.AuthenticationEmail },
                    second = new { id = second.Id.ToString(CultureInfo.InvariantCulture), tenantId = second.EntraTenantId.ToString(), email = second.AuthenticationEmail },
                    titles = titles.Select(item => new { id = item.Id.ToString(CultureInfo.InvariantCulture), scope = item.LibraryOrganizationId.ToString(CultureInfo.InvariantCulture), item.Status,
                        claimedBy = item.ClaimedByStaffUserId?.ToString(CultureInfo.InvariantCulture) }),
                    copies = copies.Select(item => new { id = item.Id.ToString(CultureInfo.InvariantCulture), scope = item.LibraryOrganizationId.ToString(CultureInfo.InvariantCulture), item.Status,
                        claimedBy = item.ClaimedByStaffUserId?.ToString(CultureInfo.InvariantCulture) })
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            }
            var (exitCode, stdout, stderr) = await RunBrowserScriptAsync(root, "shared-claims.cjs",
                client.BaseAddress!.GetLeftPart(UriPartial.Authority), artifacts);
            Assert.AreEqual(0, exitCode, $"{artifacts}{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(artifacts, "browser-results.json")));
            Assert.IsTrue(report.RootElement.GetProperty("mutation").GetBoolean());
            Assert.IsTrue(report.RootElement.GetProperty("reload").GetBoolean());
            Assert.IsTrue(report.RootElement.GetProperty("actorReplacement").GetBoolean());
            Assert.AreEqual(84, report.RootElement.GetProperty("matrix").GetArrayLength(), "Four choices across seven statuses for two super-admin scopes and one ordinary scope.");
            await using var verify = await contexts.CreateDbContextAsync();
            var mutated = await verify.TitleRequests.SingleAsync(item => item.Id == mutationId);
            Assert.AreEqual(first.Id, mutated.ClaimedByStaffUserId);
            Assert.AreEqual("manual", mutated.ClaimType);
            Assert.IsTrue(await verify.TitleRequestEvents.AnyAsync(item => item.TitleRequestId == mutationId && item.StaffUserId == first.Id));
            Assert.HasCount(0, dispatcher!.EnqueuedIds);
        }
        finally
        {
            await using (var cleanup = await contexts.CreateDbContextAsync())
            {
                var ids = cleanup.TitleRequests.Where(item => item.LibraryOrganizationId == library || item.LibraryOrganizationId == otherLibrary).Select(item => item.Id);
                await cleanup.TitleRequestEvents.Where(item => ids.Contains(item.TitleRequestId)).ExecuteDeleteAsync();
                await cleanup.AdditionalCopyRequests.Where(item => item.LibraryOrganizationId == library || item.LibraryOrganizationId == otherLibrary).ExecuteDeleteAsync();
                await cleanup.TitleRequests.Where(item => item.LibraryOrganizationId == library || item.LibraryOrganizationId == otherLibrary).ExecuteDeleteAsync();
                await cleanup.Organizations.Where(item => item.Id == library || item.Id == otherLibrary)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsActive, false));
            }
            await DeactivateCorrectiveStaffAsync(first.Id);
            await DeactivateCorrectiveStaffAsync(second.Id);
        }
    }
}
