using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task EmptyIdentifierClearsAutomationBibButPreservesStaffVerifiedBibAndDerivedTags()
    {
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var scope = Slice5IsolatedLibraryId;
        await EnsureSlice5IsolatedLibraryAsync(contextFactory, scope);
        long requestId;
        long staffVerifiedRequestId;
        var seedUtc = timeProvider!.GetUtcNow().UtcDateTime.AddMinutes(-1);
        await using (var seed = await contextFactory.CreateDbContextAsync())
        {
            var bookFormat = await seed.MaterialFormats.SingleAsync(item => item.Code == "book");
            var request = new TitleRequest
            {
                LibraryOrganizationId = scope,
                Barcode = $"slice5-empty-{Guid.NewGuid():N}",
                Title = "Empty identifier normalization",
                Author = "Slice Five",
                Identifier = null,
                BibId = "stale-bib-9001",
                MaterialFormatId = bookFormat.Id,
                Status = "suggestion",
                IsbnCheckStatus = "pending",
                AutoHold = false,
                CreatedUtc = seedUtc,
                UpdatedUtc = seedUtc
            };
            seed.TitleRequests.Add(request);
            await seed.SaveChangesAsync();
            requestId = request.Id;

            var staffVerifiedRequest = new TitleRequest
            {
                LibraryOrganizationId = scope,
                Barcode = $"slice5-staff-bib-{Guid.NewGuid():N}",
                Title = "Staff verified BIB with empty identifier",
                Identifier = null,
                BibId = "staff-bib-9002",
                BibIdStaffVerified = true,
                MaterialFormatId = bookFormat.Id,
                Status = "suggestion",
                IsbnCheckStatus = "pending",
                AutoHold = false,
                CreatedUtc = seedUtc,
                UpdatedUtc = seedUtc
            };
            seed.TitleRequests.Add(staffVerifiedRequest);
            await seed.SaveChangesAsync();
            staffVerifiedRequestId = staffVerifiedRequest.Id;

            var codes = new[] { "polaris_bib_found", "polaris_bib_not_found", "polaris_multiple_matches", "slice5-unrelated" };
            foreach (var code in codes)
            {
                var tag = await seed.WorkflowTags.SingleOrDefaultAsync(item => item.Code == code);
                if (tag is null)
                {
                    tag = new WorkflowTag { Code = code, Label = code, SortOrder = 99 };
                    seed.WorkflowTags.Add(tag);
                    await seed.SaveChangesAsync();
                }
                seed.TitleRequestWorkflowTags.Add(new TitleRequestWorkflowTag
                {
                    TitleRequestId = requestId,
                    WorkflowTagId = tag.Id
                });
            }
            await seed.SaveChangesAsync();
        }

        try
        {
            var service = factory.Services.GetRequiredService<WorkflowProcessingService>();
            var result = await service.ProcessIdentifierAsync(scope, CancellationToken.None);
            Assert.AreEqual("completed", result.Code);

            await using var verify = await contextFactory.CreateDbContextAsync();
            var request = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == requestId);
            Assert.IsNull(request.BibId);
            Assert.IsFalse(request.BibIdStaffVerified);
            Assert.AreEqual("skipped_no_isbn", request.IsbnCheckStatus);
            Assert.AreEqual(0, request.IsbnCheckRetryCount);
            Assert.IsNull(request.IsbnCheckLastErrorCode);
            var tags = await (
                from link in verify.TitleRequestWorkflowTags.AsNoTracking()
                join tag in verify.WorkflowTags.AsNoTracking() on link.WorkflowTagId equals tag.Id
                where link.TitleRequestId == requestId
                select tag.Code).ToListAsync();
            CollectionAssert.AreEquivalent(new[] { "slice5-unrelated" }, tags);
            var staffVerifiedRequest = await verify.TitleRequests.AsNoTracking()
                .SingleAsync(item => item.Id == staffVerifiedRequestId);
            Assert.AreEqual("staff-bib-9002", staffVerifiedRequest.BibId);
            Assert.IsTrue(staffVerifiedRequest.BibIdStaffVerified);
            Assert.AreEqual("skipped_no_isbn", staffVerifiedRequest.IsbnCheckStatus);
            var progress = await verify.QueueProgress.AsNoTracking().SingleAsync(item =>
                item.QueueName == QueueNames.IdentifierProcessing && item.ScopeOrganizationId == scope);
            Assert.IsNull(progress.LastItemId);
            Assert.AreEqual(staffVerifiedRequestId, progress.LastOutcomeItemId);
            Assert.AreEqual("cycle_complete", progress.LastOutcomeCode);
        }
        finally
        {
            await ExecuteNonQueryAsync(
                "DELETE FROM [asap].[TitleRequestWorkflowTag] WHERE [TitleRequestId] IN (@id, @staffId); DELETE FROM [asap].[TitleRequest] WHERE [Id] IN (@id, @staffId);",
                ("@id", requestId),
                ("@staffId", staffVerifiedRequestId));
            await ExecuteNonQueryAsync(
                "DELETE FROM [asap].[WorkflowTag] WHERE [Code] = N'slice5-unrelated' AND NOT EXISTS (SELECT 1 FROM [asap].[TitleRequestWorkflowTag] WHERE [WorkflowTagId] = [asap].[WorkflowTag].[Id]);");
        }
    }
}
