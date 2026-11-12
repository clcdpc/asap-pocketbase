using Asap.Web.Features.Staff;
using Asap.Web.Features.Email;
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
        var originalRecipientSettings = new Dictionary<long, (bool Enabled, string? NotificationEmail, string? WeeklyEmail)>();
        var titles = new List<TitleRequest>();
        var copies = new List<AdditionalCopyRequest>();
        string? originalUrl = null;
        string? originalSystemSubject = null;
        string? originalSystemBody = null;
        EmailTemplate? weeklyOverride = null;
        long weeklySystemTemplateId = 0;
        var hadWeeklyOverride = false;
        var originalOverrideSubject = (string?)null;
        var originalOverrideBody = (string?)null;
        var selectedRun = Guid.NewGuid();
        var allRun = Guid.NewGuid();
        try
        {
            await using (var seed = await contexts.CreateDbContextAsync())
            {
                var system = await seed.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
                originalUrl = system.StaffApplicationUrl;
                system.StaffApplicationUrl = "https://staff.example.org/staff/";
                var weeklySystemTemplate = await seed.EmailTemplates.SingleAsync(item =>
                    item.OrganizationId == LibraryScope.SystemOrganizationId &&
                    item.TemplateKey == StaffEmailTemplateCatalog.WeeklySummary);
                weeklySystemTemplateId = weeklySystemTemplate.Id;
                originalSystemSubject = weeklySystemTemplate.SubjectTemplate;
                originalSystemBody = weeklySystemTemplate.BodyTemplate;
                weeklySystemTemplate.SubjectTemplate = "System weekly: {{submissionCount}}";
                weeklyOverride = await seed.EmailTemplates.SingleOrDefaultAsync(item =>
                    item.OrganizationId == libraryA && item.SourceTemplateId == weeklySystemTemplate.Id);
                if (weeklyOverride is not null)
                {
                    hadWeeklyOverride = true;
                    originalOverrideSubject = weeklyOverride.SubjectTemplate;
                    originalOverrideBody = weeklyOverride.BodyTemplate;
                }
                else
                {
                    weeklyOverride = new EmailTemplate
                    {
                        OrganizationId = libraryA,
                        TemplateKey = StaffEmailTemplateCatalog.WeeklySummary,
                        SourceTemplateId = weeklySystemTemplate.Id,
                        IsCustom = false,
                        IsHidden = false
                    };
                    seed.EmailTemplates.Add(weeklyOverride);
                }
                weeklyOverride.SubjectTemplate = "Library A weekly: {{submissionCount}}";
                foreach (var staff in await seed.StaffUsers.Where(item => item.WeeklyActionSummaryEnabled || staffIds.Contains(item.Id)).ToArrayAsync())
                {
                    originalRecipientSettings.Add(staff.Id,
                        (staff.WeeklyActionSummaryEnabled, staff.NotificationEmail, staff.WeeklyActionSummaryEmail));
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
                Assert.AreEqual("Library A weekly: 7", firstA.Subject,
                    "Library-scoped data uses the library override even when the recipient has system authorization.");
                var librarySubmissions = titles.Where(item => item.LibraryOrganizationId == libraryA && item.Status == "suggestion")
                    .OrderByDescending(item => item.CreatedUtc).ThenByDescending(item => item.Id).Take(5)
                    .Select(item => new WeeklyPreparationExample(item.Title, item.Author, item.Id.ToString(),
                        item.LibraryOrganizationId, item.Status)).ToArray();
                var libraryPurchases = titles.Where(item => item.LibraryOrganizationId == libraryA && item.Status == "outstanding_purchase")
                    .Where(item => item.BibId is null)
                    .OrderByDescending(item => item.UpdatedUtc).ThenByDescending(item => item.Id).Take(5)
                    .Select(item => new WeeklyPreparationExample(item.Title, item.Author, item.Id.ToString(),
                        item.LibraryOrganizationId, item.Status)).ToArray();
                var libraryCopies = copies.Where(item => item.LibraryOrganizationId == libraryA && item.Status == "open")
                    .OrderByDescending(item => item.CreatedUtc).ThenByDescending(item => item.Id).Take(5)
                    .Select(item => new WeeklyPreparationExample(item.Title, item.Author, item.Id.ToString(),
                        item.LibraryOrganizationId, "additional_copies", item.BibId.ToString())).ToArray();
                AssertWeeklyPreparationSection(firstA.BodyText!, "New submissions", "7 active requests", librarySubmissions);
                AssertWeeklyPreparationSection(firstA.BodyText!, "Approved purchases without bibs", "7 active requests", libraryPurchases);
                AssertWeeklyPreparationSection(firstA.BodyText!, "Additional copies", "7 open tasks", libraryCopies);
                StringAssert.Contains(firstA.BodyHtml!, "&lt;New 99442-6&gt;");
                Assert.IsFalse(firstA.BodyHtml!.Contains("<New 99442-6>", StringComparison.Ordinal));
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
                    Assert.AreEqual($"System weekly: {newTitles.Length}", global.Subject,
                        "An all-library summary uses the system template for its system-authorized recipient.");
                    var systemSubmissions = newTitles.OrderByDescending(item => item.CreatedUtc).ThenByDescending(item => item.Id).Take(5)
                        .Select(item => new WeeklyPreparationExample(item.Title, item.Author, item.Id.ToString(),
                            item.LibraryOrganizationId, item.Status)).ToArray();
                    var systemPurchases = purchases.OrderByDescending(item => item.UpdatedUtc).ThenByDescending(item => item.Id).Take(5)
                        .Select(item => new WeeklyPreparationExample(item.Title, item.Author, item.Id.ToString(),
                            item.LibraryOrganizationId, item.Status)).ToArray();
                    var systemCopies = allCopies.OrderByDescending(item => item.CreatedUtc).ThenByDescending(item => item.Id).Take(5)
                        .Select(item => new WeeklyPreparationExample(item.Title, item.Author, item.Id.ToString(),
                            item.LibraryOrganizationId, "additional_copies", item.BibId.ToString())).ToArray();
                    AssertWeeklyPreparationSection(global.BodyText!, "New submissions", $"{newTitles.Length} active requests", systemSubmissions);
                    AssertWeeklyPreparationSection(global.BodyText!, "Approved purchases without bibs", $"{purchases.Length} active requests", systemPurchases);
                    AssertWeeklyPreparationSection(global.BodyText!, "Additional copies", $"{allCopies.Length} open tasks", systemCopies);
                    var libraryBStaff = rows.Single(row => row.RecipientStaffUserId == staffB.Id);
                    Assert.AreEqual("System weekly: 7", libraryBStaff.Subject,
                        "A library without an override inherits the current system template.");
                    StringAssert.Contains(libraryBStaff.BodyText!, $"New {libraryB}-6");
                }
                var queueScope = scope.HasValue ? libraryA.ToString() : "system";
                foreach (var stage in new[] { "submitted", "purchased_waiting_for_bib", "additional_copies" })
                {
                    StringAssert.Contains(global.BodyText!,
                        $"https://staff.example.org/staff/?stage={stage}&scope={queueScope}");
                }
            }

            Dictionary<string, (string? Subject, string? BodyText, string? BodyHtml)> queuedSnapshots;
            await using (var beforeEdit = await contexts.CreateDbContextAsync())
            {
                queuedSnapshots = await beforeEdit.EmailOutbox.AsNoTracking()
                    .Where(item => item.BusinessKey!.StartsWith($"weekly-summary-force:{selectedRun:N}:") ||
                                   item.BusinessKey!.StartsWith($"weekly-summary-force:{allRun:N}:"))
                    .ToDictionaryAsync(item => item.BusinessKey!, item => (item.Subject, item.BodyText, item.BodyHtml));
                var template = await beforeEdit.EmailTemplates.SingleAsync(item => item.Id == weeklySystemTemplateId);
                template.BodyTemplate = originalSystemBody + "\nChanged after these summaries were queued.";
                await beforeEdit.SaveChangesAsync();
            }
            await using (var afterEdit = await contexts.CreateDbContextAsync())
            {
                foreach (var (key, snapshot) in queuedSnapshots)
                {
                    var stored = await afterEdit.EmailOutbox.AsNoTracking().SingleAsync(item => item.BusinessKey == key);
                    Assert.AreEqual(snapshot.Subject, stored.Subject);
                    Assert.AreEqual(snapshot.BodyText, stored.BodyText);
                    Assert.AreEqual(snapshot.BodyHtml, stored.BodyHtml);
                    Assert.IsFalse(stored.BodyText!.Contains("Changed after these summaries were queued.", StringComparison.Ordinal));
                }
            }
        }
        finally
        {
            await ExecuteNonQueryAsync("DELETE FROM [asap].[EmailOutbox] WHERE [BusinessKey] LIKE @selected OR [BusinessKey] LIKE @all;",
                ("@selected", $"weekly-summary-force:{selectedRun:N}:%"), ("@all", $"weekly-summary-force:{allRun:N}:%"));
            await using var restore = await contexts.CreateDbContextAsync();
            if (weeklySystemTemplateId > 0)
            {
                var weeklySystemTemplate = await restore.EmailTemplates.SingleAsync(item => item.Id == weeklySystemTemplateId);
                weeklySystemTemplate.SubjectTemplate = originalSystemSubject;
                weeklySystemTemplate.BodyTemplate = originalSystemBody;
                var currentWeeklyOverride = await restore.EmailTemplates.SingleOrDefaultAsync(item =>
                    item.OrganizationId == libraryA && item.SourceTemplateId == weeklySystemTemplateId);
                if (!hadWeeklyOverride)
                {
                    if (currentWeeklyOverride is not null)
                    {
                        restore.EmailTemplates.Remove(currentWeeklyOverride);
                    }
                }
                else if (currentWeeklyOverride is not null)
                {
                    currentWeeklyOverride.SubjectTemplate = originalOverrideSubject;
                    currentWeeklyOverride.BodyTemplate = originalOverrideBody;
                }
            }
            var copyIds = copies.Select(item => item.Id).ToArray();
            var titleIds = titles.Select(item => item.Id).ToArray();
            await restore.AdditionalCopyRequests.Where(item => copyIds.Contains(item.Id)).ExecuteDeleteAsync();
            await restore.TitleRequests.Where(item => titleIds.Contains(item.Id)).ExecuteDeleteAsync();
            (await restore.SystemSettings.SingleAsync(item => item.OrganizationId == 1)).StaffApplicationUrl = originalUrl;
            foreach (var (id, original) in originalRecipientSettings)
            {
                var staff = await restore.StaffUsers.SingleAsync(item => item.Id == id);
                staff.WeeklyActionSummaryEnabled = original.Enabled;
                staff.NotificationEmail = original.NotificationEmail;
                staff.WeeklyActionSummaryEmail = original.WeeklyEmail;
            }
            await restore.SaveChangesAsync();
            foreach (var id in staffIds)
            {
                await DeactivateCorrectiveStaffAsync(id);
            }
        }
    }

    private sealed record WeeklyPreparationExample(
        string Title,
        string? Author,
        string Id,
        int LibraryOrganizationId,
        string Stage,
        string? BibId = null);

    private static void AssertWeeklyPreparationSection(
        string body,
        string heading,
        string count,
        IEnumerable<WeeklyPreparationExample> examples)
    {
        var expectedLines = examples.Select((item, index) =>
        {
            var author = string.IsNullOrWhiteSpace(item.Author) ? item.BibId : item.Author;
            var detail = $"{index + 1}. {item.Title}{(string.IsNullOrWhiteSpace(author) ? string.Empty : $" — {author}")}";
            var kind = item.Stage == "additional_copies" ? "task" : "request";
            var url = $"https://staff.example.org/staff/?request={item.Id}&stage={item.Stage}&scope={item.LibraryOrganizationId}";
            return $"{detail}\nView {kind}: {url}";
        });
        var expected = $"{heading}\n{count}\nFive most recent:\n" + string.Join("\n", expectedLines);
        StringAssert.Contains(body, expected);
        var section = body[(body.IndexOf(heading, StringComparison.Ordinal) + heading.Length)..].Split("\n\n", 2)[0];
        Assert.AreEqual(5, section.Split('\n').Count(line => line.Length > 2 && char.IsDigit(line[0]) && line[1] == '.'));
    }
}
