using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task WeeklyPreparationPreservesBacklogSamplesAndScopesWithNarrowSharedReads()
    {
        const int libraryA = 99442, libraryB = 99443;
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await EnsureSlice5IsolatedLibraryAsync(contexts, libraryA);
        await EnsureSlice5IsolatedLibraryAsync(contexts, libraryB);
        var administrator = await ReadConfiguredSuperAdminAsync();
        var staffA = await CreateCorrectiveStaffAsync(administrator, StaffRole.Staff, libraryA);
        var secondA = await CreateCorrectiveStaffAsync(administrator, StaffRole.Staff, libraryA);
        var staffB = await CreateCorrectiveStaffAsync(administrator, StaffRole.Staff, libraryB);
        var superAdmin = await CreateCorrectiveStaffAsync(administrator, StaffRole.SuperAdmin, 1);
        var staffIds = new[] { staffA.Id, secondA.Id, staffB.Id, superAdmin.Id };
        var originalFlags = new Dictionary<long, bool>();
        var titles = new List<TitleRequest>();
        var copies = new List<AdditionalCopyRequest>();
        string? originalUrl = null;
        var selectedRun = Guid.NewGuid();
        var allRun = Guid.NewGuid();
        try
        {
            await using (var seed = await contexts.CreateDbContextAsync())
            {
                var system = await seed.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
                originalUrl = system.StaffApplicationUrl;
                system.StaffApplicationUrl = "https://staff.example.org/staff/";
                foreach (var staff in await seed.StaffUsers.Where(item => item.WeeklyActionSummaryEnabled || staffIds.Contains(item.Id)).ToArrayAsync())
                {
                    originalFlags.Add(staff.Id, staff.WeeklyActionSummaryEnabled);
                    staff.WeeklyActionSummaryEnabled = staffIds.Contains(staff.Id);
                    if (staff.WeeklyActionSummaryEnabled)
                    {
                        staff.NotificationEmail = $"weekly-preparation-{staff.Id}@example.org";
                        staff.WeeklyActionSummaryEmail = null;
                    }
                }
                var formatId = await seed.MaterialFormats.Where(item => item.Code == "book").Select(item => item.Id).SingleAsync();
                // Every row predates the displayed week. Ties deliberately require the ID tiebreaker;
                // purchase CreatedUtc runs opposite UpdatedUtc so the two orders cannot be confused.
                var old = timeProvider!.GetUtcNow().UtcDateTime.AddYears(-2);
                foreach (var library in new[] { libraryA, libraryB })
                {
                    for (var index = 0; index < 7; index++)
                    {
                        titles.Add(new TitleRequest
                        {
                            LibraryOrganizationId = library, Barcode = $"weekly-{library}-new-{index}",
                            Title = $"<New {library}-{index}>", Author = index == 6 ? null : $"Author {index}",
                            MaterialFormatId = formatId, Status = "suggestion", BibId = index == 6 ? 9123 : null,
                            CreatedUtc = old.AddDays(index / 2), UpdatedUtc = old.AddDays(index / 2)
                        });
                        titles.Add(new TitleRequest
                        {
                            LibraryOrganizationId = library, Barcode = $"weekly-{library}-purchase-{index}",
                            Title = $"Purchase {library}-{index}", Author = $"Purchase author {index}",
                            MaterialFormatId = formatId, Status = "outstanding_purchase",
                            CreatedUtc = old.AddDays(-index), UpdatedUtc = old.AddDays(index / 2)
                        });
                        copies.Add(new AdditionalCopyRequest
                        {
                            LibraryOrganizationId = library, Title = $"Copy {library}-{index}", BibId = 9200 + index,
                            Author = index == 6 ? null : $"Copy author {index}", Status = "open",
                            CreatedUtc = old.AddDays(index / 2), UpdatedUtc = old.AddDays(index / 2)
                        });
                    }
                    titles.Add(new TitleRequest
                    {
                        LibraryOrganizationId = library, Barcode = $"weekly-{library}-excluded", Title = "Excluded assigned BIB",
                        MaterialFormatId = formatId, Status = "outstanding_purchase", BibId = 9999,
                        CreatedUtc = old.AddYears(1), UpdatedUtc = old.AddYears(1)
                    });
                    copies.Add(new AdditionalCopyRequest
                    {
                        LibraryOrganizationId = library, Title = "Excluded closed copy", BibId = 9999, Status = "closed",
                        CreatedUtc = old.AddYears(1), UpdatedUtc = old.AddYears(1), ClosedUtc = old.AddYears(1)
                    });
                }
                seed.TitleRequests.AddRange(titles);
                seed.AdditionalCopyRequests.AddRange(copies);
                await seed.SaveChangesAsync();
            }

            foreach (var (run, scope) in new (Guid, int?)[] { (selectedRun, libraryA), (allRun, null) })
            {
                var probe = new WeeklySummarySqlProbe();
                var dispatch = new RecordingOutboxDispatcher();
                var service = ActivatorUtilities.CreateInstance<WorkflowProcessingService>(factory.Services,
                    new WeeklySummaryContextFactory(databaseConnectionString, probe), dispatch);
                var result = await service.SendWeeklyStaffSummaryAsync(run, scope, CancellationToken.None);
                Assert.AreEqual("completed", result.Code);
                Assert.AreEqual(scope.HasValue ? 3 : 4, result.Changed);
                Assert.AreEqual(result.Changed, dispatch.EnqueuedIds.Count);
                Assert.AreEqual(0, probe.FullRequestEntities, "Summary reads must not materialize complete request entities.");
                var titleRead = probe.Reads.Single(read => read.Sql.Contains("[asap].[TitleRequest]", StringComparison.Ordinal));
                var copyRead = probe.Reads.Single(read => read.Sql.Contains("[asap].[AdditionalCopyRequest]", StringComparison.Ordinal));
                CollectionAssert.AreEqual(new[] { "Id", "LibraryOrganizationId", "Status", "Title", "Author", "CreatedUtc", "UpdatedUtc" }, titleRead.Columns);
                CollectionAssert.AreEqual(new[] { "Id", "LibraryOrganizationId", "Title", "Author", "BibId", "CreatedUtc" }, copyRead.Columns);
                StringAssert.Contains(titleRead.Sql, "[BibId] IS NULL");
                Assert.IsFalse(titleRead.Sql.Contains("[CreatedUtc] >=", StringComparison.Ordinal), "The report covers the full active backlog.");

                await using var verify = await contexts.CreateDbContextAsync();
                var prefix = $"weekly-summary-force:{run:N}:";
                var rows = await verify.EmailOutbox.AsNoTracking().Where(item => item.BusinessKey!.StartsWith(prefix)).ToArrayAsync();
                Assert.HasCount(scope.HasValue ? 3 : 4, rows);
                var firstA = rows.Single(row => row.RecipientStaffUserId == staffA.Id);
                var otherA = rows.Single(row => row.RecipientStaffUserId == secondA.Id);
                Assert.AreEqual(libraryA, firstA.AuthorizationOrganizationId);
                Assert.AreEqual($"weekly-summary-force:{run:N}:{staffA.Id}", firstA.BusinessKey);
                Assert.AreEqual($"weekly-summary-force:{run:N}:{secondA.Id}", otherA.BusinessKey);
                Assert.AreEqual(firstA.Subject, otherA.Subject);
                Assert.AreEqual(firstA.BodyText, otherA.BodyText, "Recipients sharing a data scope render the same prepared report.");
                Assert.AreNotEqual(firstA.ToAddress, otherA.ToAddress, "Addresses remain recipient-specific.");
                Assert.AreEqual($"Weekly ASAP action summary: 7 new, 7 awaiting bibs, 7 additional copies", firstA.Subject);
                AssertWeeklyPreparationSection(firstA.BodyText!, "New submissions", "7 active requests", Enumerable.Range(2, 5).Reverse()
                    .Select(index => $"New {libraryA}-{index}" + (index == 6 ? "" : $" - Author {index}")));
                AssertWeeklyPreparationSection(firstA.BodyText!, "Approved purchases without bibs", "7 active requests", Enumerable.Range(2, 5).Reverse()
                    .Select(index => $"Purchase {libraryA}-{index} - Purchase author {index}"));
                AssertWeeklyPreparationSection(firstA.BodyText!, "Additional copies", "7 open tasks", Enumerable.Range(2, 5).Reverse()
                    .Select(index => $"Copy {libraryA}-{index} - " + (index == 6 ? "9206" : $"Copy author {index}")));
                Assert.IsFalse(firstA.BodyText!.Contains($"{libraryB}-", StringComparison.Ordinal));
                Assert.IsFalse(firstA.BodyText.Contains("Excluded", StringComparison.Ordinal));
                var global = rows.Single(row => row.RecipientStaffUserId == superAdmin.Id);
                Assert.AreEqual(1, global.AuthorizationOrganizationId);
                Assert.AreEqual($"weekly-summary-force:{run:N}:{superAdmin.Id}", global.BusinessKey);
                Assert.AreEqual($"weekly-preparation-{superAdmin.Id}@example.org", global.ToAddress);
                Assert.AreNotEqual(firstA.BusinessKey, global.BusinessKey,
                    "Sharing prepared data must retain independent recipient occurrences and system authorization.");
                if (scope.HasValue)
                {
                    Assert.AreEqual(firstA.Subject, global.Subject);
                    Assert.AreEqual(firstA.BodyText, global.BodyText, "Selected-library super-admin data remains limited to the selected library.");
                    Assert.IsFalse(rows.Any(row => row.RecipientStaffUserId == staffB.Id));
                    // EF's reader counter includes the terminal ReadAsync returning false.
                    Assert.AreEqual(15, titleRead.ReadCalls, "Fourteen eligible title rows plus the terminal read.");
                    Assert.AreEqual(8, copyRead.ReadCalls, "Seven open copy rows plus the terminal read.");
                }
                else
                {
                    var active = await verify.Organizations.Where(item => item.IsActive && item.Id > 1 && item.OrganizationCodeId == 2)
                        .Select(item => item.Id).ToArrayAsync();
                    var allTitles = await verify.TitleRequests.AsNoTracking().Where(item => active.Contains(item.LibraryOrganizationId) &&
                        (item.Status == "suggestion" || item.Status == "outstanding_purchase" && item.BibId == null)).ToArrayAsync();
                    var allCopies = await verify.AdditionalCopyRequests.AsNoTracking().Where(item => active.Contains(item.LibraryOrganizationId) && item.Status == "open").ToArrayAsync();
                    var newTitles = allTitles.Where(item => item.Status == "suggestion").ToArray();
                    var purchases = allTitles.Where(item => item.Status == "outstanding_purchase").ToArray();
                    Assert.AreEqual($"Weekly ASAP action summary: {newTitles.Length} new, {purchases.Length} awaiting bibs, {allCopies.Length} additional copies", global.Subject);
                    static string Sample(string title, string? author) => title.Replace("<", "").Replace(">", "").Trim() +
                        (string.IsNullOrWhiteSpace(author) ? "" : " - " + author.Replace("<", "").Replace(">", "").Trim());
                    AssertWeeklyPreparationSection(global.BodyText!, "New submissions", $"{newTitles.Length} active requests", newTitles
                        .OrderByDescending(item => item.CreatedUtc).ThenByDescending(item => item.Id).Take(5).Select(item => Sample(item.Title, item.Author)));
                    AssertWeeklyPreparationSection(global.BodyText!, "Approved purchases without bibs", $"{purchases.Length} active requests", purchases
                        .OrderByDescending(item => item.UpdatedUtc).ThenByDescending(item => item.Id).Take(5).Select(item => Sample(item.Title, item.Author)));
                    AssertWeeklyPreparationSection(global.BodyText!, "Additional copies", $"{allCopies.Length} open tasks", allCopies
                        .OrderByDescending(item => item.CreatedUtc).ThenByDescending(item => item.Id).Take(5)
                        .Select(item => Sample(item.Title, item.Author ?? item.BibId.ToString(System.Globalization.CultureInfo.InvariantCulture))));
                    StringAssert.Contains(rows.Single(row => row.RecipientStaffUserId == staffB.Id).BodyText!, $"New {libraryB}-6");
                }
                foreach (var stage in new[] { "submitted", "purchased_waiting_for_bib", "additional_copies" })
                {
                    StringAssert.Contains(global.BodyText!, $"https://staff.example.org/staff/?stage={stage}");
                }
            }
        }
        finally
        {
            await ExecuteNonQueryAsync("DELETE FROM [asap].[EmailOutbox] WHERE [BusinessKey] LIKE @selected OR [BusinessKey] LIKE @all;",
                ("@selected", $"weekly-summary-force:{selectedRun:N}:%"), ("@all", $"weekly-summary-force:{allRun:N}:%"));
            await using var restore = await contexts.CreateDbContextAsync();
            var copyIds = copies.Select(item => item.Id).ToArray();
            var titleIds = titles.Select(item => item.Id).ToArray();
            await restore.AdditionalCopyRequests.Where(item => copyIds.Contains(item.Id)).ExecuteDeleteAsync();
            await restore.TitleRequests.Where(item => titleIds.Contains(item.Id)).ExecuteDeleteAsync();
            (await restore.SystemSettings.SingleAsync(item => item.OrganizationId == 1)).StaffApplicationUrl = originalUrl;
            foreach (var (id, flag) in originalFlags)
            {
                (await restore.StaffUsers.SingleAsync(item => item.Id == id)).WeeklyActionSummaryEnabled = flag;
            }
            await restore.SaveChangesAsync();
            foreach (var id in staffIds)
            {
                await DeactivateCorrectiveStaffAsync(id);
            }
        }
    }

    private static void AssertWeeklyPreparationSection(string body, string heading, string count, IEnumerable<string> examples)
    {
        var expected = $"{heading}\n{count}\nFive most recent:\n" + string.Join("\n", examples.Select((line, index) => $"{index + 1}. {line}"));
        StringAssert.Contains(body, expected);
        var section = body[(body.IndexOf(heading, StringComparison.Ordinal) + heading.Length)..].Split("\n\n", 2)[0];
        Assert.AreEqual(5, section.Split('\n').Count(line => line.Length > 2 && char.IsDigit(line[0]) && line[1] == '.'));
    }
}
