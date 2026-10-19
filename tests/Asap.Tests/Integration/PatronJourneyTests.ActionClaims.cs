using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task StaffActionsPreserveAndReapplyAutomaticClaims()
    {
        const int libraryId = 92913;
        var actor = await ReadConfiguredSuperAdminAsync();
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var seed = await contexts.CreateDbContextAsync();
        var formats = await seed.MaterialFormats.AsNoTracking()
            .Where(item => item.OwnerOrganizationId == 1 &&
                (item.Code == "book" || item.Code == "dvd" || item.Code == "music_cd" || item.Code == "ebook"))
            .ToDictionaryAsync(item => item.Code, item => item.Id);
        seed.Organizations.Add(new Organization
        {
            Id = libraryId, DisplayName = "Action claim library", Abbreviation = "ACL",
            OrganizationCodeId = 2, ParentOrganizationId = 1, IsActive = true
        });
        var assignee = new StaffUser
        {
            UserPrincipalName = "action.claim@example.org",
            NormalizedUserPrincipalName = "ACTION.CLAIM@EXAMPLE.ORG",
            Role = "staff", OrganizationId = libraryId, IsActive = true
        };
        var inactiveCandidate = new StaffUser
        {
            UserPrincipalName = "inactive.action.claim@example.org",
            NormalizedUserPrincipalName = "INACTIVE.ACTION.CLAIM@EXAMPLE.ORG",
            Role = "staff", OrganizationId = libraryId, IsActive = false
        };
        seed.StaffUsers.AddRange(assignee, inactiveCandidate);
        await seed.SaveChangesAsync();
        var bookRule = new FormatAutoClaimRule
        {
            LibraryOrganizationId = libraryId, MaterialFormatId = formats["book"],
            StaffUserId = actor.Id, IsActive = true, CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime
        };
        var dvdRule = new FormatAutoClaimRule
        {
            LibraryOrganizationId = libraryId, MaterialFormatId = formats["dvd"],
            StaffUserId = assignee.Id, IsActive = true, CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime
        };
        var ebookRule = new FormatAutoClaimRule
        {
            LibraryOrganizationId = libraryId, MaterialFormatId = formats["ebook"],
            StaffUserId = inactiveCandidate.Id, IsActive = true, CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime
        };
        seed.FormatAutoClaimRules.AddRange(bookRule, dvdRule, ebookRule);
        await seed.SaveChangesAsync();
        TitleRequest NewRequest(string title) => new()
        {
            LibraryOrganizationId = libraryId, Barcode = $"2{Guid.NewGuid():N}"[..14],
            Title = title, MaterialFormatId = formats["book"], Status = "suggestion", AutoHold = true,
            ClaimedByStaffUserId = actor.Id, ClaimedByDisplayName = "Actor", ClaimedAtUtc = timeProvider!.GetUtcNow().UtcDateTime,
            ClaimType = "automatic_format_rule", ClaimRuleId = bookRule.Id,
            CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime, UpdatedUtc = timeProvider!.GetUtcNow().UtcDateTime
        };
        var request = NewRequest("Preserve claim");
        var noRule = NewRequest("Clear claim");
        var skipped = NewRequest("Skip ineligible claimant");
        seed.TitleRequests.AddRange(request, noRule, skipped);
        await seed.SaveChangesAsync();
        try
        {
            var mutations = factory.Services.GetRequiredService<TitleRequestMutationService>();
            Assert.AreEqual("updated", (await mutations.ActionAsync(actor, request.Id,
                new TitleRequestActionInput
                {
                    Version = StaffVersion.Encode(request.RowVersion), Action = "edit", Title = "Edited title"
                }.ToCommand(), CancellationToken.None)).Code);
            await using var afterEdit = await contexts.CreateDbContextAsync();
            var preserved = await afterEdit.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id);
            Assert.AreEqual("automatic_format_rule", preserved.ClaimType);
            Assert.AreEqual(bookRule.Id, preserved.ClaimRuleId);
            Assert.AreEqual(actor.Id, preserved.ClaimedByStaffUserId);

            Assert.AreEqual("updated", (await mutations.ActionAsync(actor, request.Id,
                new TitleRequestActionInput
                {
                    Version = StaffVersion.Encode(preserved.RowVersion), Action = "edit", Format = "dvd"
                }.ToCommand(), CancellationToken.None)).Code);
            await using var afterFormat = await contexts.CreateDbContextAsync();
            var reassigned = await afterFormat.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id);
            Assert.AreEqual("automatic_format_rule", reassigned.ClaimType);
            Assert.AreEqual(dvdRule.Id, reassigned.ClaimRuleId);
            Assert.AreEqual(assignee.Id, reassigned.ClaimedByStaffUserId);
            Assert.IsTrue(await afterFormat.TitleRequestEvents.AnyAsync(item =>
                item.TitleRequestId == request.Id && item.EventType == "claim_auto_reassigned"));

            Assert.AreEqual("updated", (await mutations.ActionAsync(actor, noRule.Id,
                new TitleRequestActionInput
                {
                    Version = StaffVersion.Encode(noRule.RowVersion), Action = "edit", Format = "music_cd"
                }.ToCommand(), CancellationToken.None)).Code);
            await using var afterNoRule = await contexts.CreateDbContextAsync();
            var cleared = await afterNoRule.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == noRule.Id);
            Assert.IsNull(cleared.ClaimedByStaffUserId);
            Assert.IsNull(cleared.ClaimRuleId);
            Assert.IsNull(cleared.ClaimType);
            Assert.IsTrue(await afterNoRule.TitleRequestEvents.AnyAsync(item =>
                item.TitleRequestId == noRule.Id && item.EventType == "claim_auto_cleared"));

            Assert.AreEqual("updated", (await mutations.ActionAsync(actor, skipped.Id,
                new TitleRequestActionInput
                {
                    Version = StaffVersion.Encode(skipped.RowVersion), Action = "edit", Format = "ebook"
                }.ToCommand(), CancellationToken.None)).Code);
            await using var afterSkipped = await contexts.CreateDbContextAsync();
            var preservedOnSkip = await afterSkipped.TitleRequests.AsNoTracking()
                .SingleAsync(item => item.Id == skipped.Id);
            Assert.AreEqual(actor.Id, preservedOnSkip.ClaimedByStaffUserId);
            Assert.AreEqual("automatic_format_rule", preservedOnSkip.ClaimType);
            Assert.AreEqual(bookRule.Id, preservedOnSkip.ClaimRuleId);
            Assert.IsTrue(await afterSkipped.TitleRequestEvents.AnyAsync(item =>
                item.TitleRequestId == skipped.Id && item.EventType == "claim_auto_skipped"));

            Assert.AreEqual("updated", (await mutations.ActionAsync(actor, request.Id,
                new TitleRequestActionInput
                {
                    Version = StaffVersion.Encode(reassigned.RowVersion), Action = "edit", Title = "Transferred title"
                }.ToCommand(), CancellationToken.None)).Code);
            await using var afterTransfer = await contexts.CreateDbContextAsync();
            var transferred = await afterTransfer.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id);
            Assert.AreEqual("manual", transferred.ClaimType);
            Assert.IsNull(transferred.ClaimRuleId);
            Assert.AreEqual(actor.Id, transferred.ClaimedByStaffUserId);
            Assert.IsTrue(await afterTransfer.TitleRequestEvents.AnyAsync(item =>
                item.TitleRequestId == request.Id && item.EventType == "claim_manual_transferred"));
        }
        finally
        {
            await ExecuteNonQueryAsync(
                "DELETE FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] IN (@requestId, @noRuleId, @skippedId); " +
                "DELETE FROM [asap].[TitleRequest] WHERE [Id] IN (@requestId, @noRuleId, @skippedId); " +
                "DELETE FROM [asap].[FormatAutoClaimRule] WHERE [LibraryOrganizationId] = @libraryId; " +
                "DELETE FROM [asap].[StaffUser] WHERE [Id] IN (@assigneeId, @inactiveCandidateId); " +
                "DELETE FROM [asap].[Organization] WHERE [Id] = @libraryId;",
                ("@requestId", request.Id), ("@noRuleId", noRule.Id),
                ("@skippedId", skipped.Id), ("@libraryId", libraryId),
                ("@assigneeId", assignee.Id), ("@inactiveCandidateId", inactiveCandidate.Id));
        }
    }
}
