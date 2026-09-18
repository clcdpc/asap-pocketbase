using System.Diagnostics;
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
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task WorkflowPrivilegedMutationsUseLockedActorInBothLifecycleOrderings(bool additionalCopy, bool delete)
    {
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var lifecycle = factory.Services.GetRequiredService<StaffLifecycleService>();
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        foreach (var change in new[] { "demote", "deactivate", "move" })
        foreach (var workflowFirst in new[] { false, true })
        {
            var admin = await CreateCorrectiveStaffAsync(superAdmin, "admin", 2);
            var actor = await ReadCorrectiveStaffAsync(admin);
            var assignee = await CreateCorrectiveStaffAsync(superAdmin, "staff", 2);
            long requestId;
            byte[] requestVersion;
            await using (var context = await contextFactory.CreateDbContextAsync())
            {
                if (!await context.Organizations.AnyAsync(item => item.Id == 91904))
                {
                    context.Organizations.Add(new Organization { Id = 91904, DisplayName = "Workflow race destination", IsActive = true });
                }
                if (additionalCopy)
                {
                    var row = new AdditionalCopyRequest
                    {
                        LibraryOrganizationId = 2, BibId = "12345", Title = "Privileged copy race",
                        Status = delete ? "closed" : "open", ClaimedByStaffUserId = assignee.Id,
                        ClaimedByDisplayName = assignee.DisplayName ?? assignee.UserPrincipalName,
                        ClaimType = "manual", ClaimedAtUtc = DateTime.UtcNow,
                        Notes = "Committed copy history.", CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime, UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime,
                        ClosedUtc = delete ? timeProvider.GetUtcNow().UtcDateTime : null
                    };
                    context.AdditionalCopyRequests.Add(row);
                    await context.SaveChangesAsync();
                    requestId = row.Id;
                    requestVersion = row.RowVersion;
                }
                else
                {
                    var row = new TitleRequest
                    {
                        LibraryOrganizationId = 2, Barcode = Guid.NewGuid().ToString("N"), Title = "Privileged title race",
                        MaterialFormatId = await context.MaterialFormats.Where(item => item.OwnerOrganizationId == 1 && item.Code == "book").Select(item => item.Id).SingleAsync(),
                        Status = delete ? "closed" : "suggestion", CloseReason = delete ? "manual" : null, ClaimedByStaffUserId = assignee.Id,
                        ClaimedByDisplayName = assignee.DisplayName ?? assignee.UserPrincipalName,
                        ClaimType = "manual", ClaimedAtUtc = DateTime.UtcNow,
                        CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow
                    };
                    context.TitleRequests.Add(row);
                    await context.SaveChangesAsync();
                    requestId = row.Id;
                    requestVersion = row.RowVersion;
                }
            }
            async Task<string> MutateAsync()
            {
                var input = new VersionInput(StaffVersion.Encode(requestVersion));
                if (additionalCopy)
                {
                    var copies = factory.Services.GetRequiredService<AdditionalCopyService>();
                    return (delete
                        ? await copies.DeleteClosedAsync(actor, requestId, input, CancellationToken.None)
                        : await copies.ClaimAsync(actor, requestId, input, true, CancellationToken.None)).Code;
                }
                var titles = factory.Services.GetRequiredService<TitleRequestMutationService>();
                return (delete
                    ? await titles.DeleteClosedAsync(actor, requestId, input, CancellationToken.None)
                    : await titles.ClaimAsync(actor, requestId, input, true, CancellationToken.None)).Code;
            }
            Task<StaffLifecycleResult> ContractAsync() => change == "deactivate"
                ? lifecycle.DeactivateAsync(superAdmin, admin.Id, new StaffDeactivateInput(StaffVersion.Encode(admin.RowVersion)), CancellationToken.None)
                : lifecycle.ChangeRoleAsync(superAdmin, admin.Id,
                    new StaffRoleInput(StaffVersion.Encode(admin.RowVersion), change == "demote" ? "staff" : "admin", change == "move" ? 91904 : 2), CancellationToken.None);

            await using var blocker = new SqlConnection(databaseConnectionString);
            await blocker.OpenAsync();
            await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
            var table = workflowFirst ? additionalCopy ? "AdditionalCopyRequest" : "TitleRequest" : "StaffUser";
            await using (var command = new SqlCommand($"SELECT [Id] FROM [asap].[{table}] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = @id;", blocker, transaction))
            {
                command.Parameters.AddWithValue("@id", workflowFirst ? requestId : admin.Id);
                await command.ExecuteScalarAsync();
            }
            Task<string> mutation;
            Task<StaffLifecycleResult> contraction;
            if (workflowFirst)
            {
                mutation = MutateAsync();
                var workflowSession = await WaitForCorrectiveSqlBlockAsync(blocker.ServerProcessId);
                contraction = ContractAsync();
                await WaitForCorrectiveSqlBlockAsync(workflowSession);
            }
            else
            {
                contraction = ContractAsync();
                var lifecycleSession = await WaitForCorrectiveSqlBlockAsync(blocker.ServerProcessId);
                mutation = MutateAsync();
                await WaitForCorrectiveSqlBlockAsync(lifecycleSession);
            }
            await transaction.CommitAsync();
            Assert.AreEqual("updated", (await contraction).Code);
            var expected = workflowFirst ? delete ? "deleted" : "updated"
                : change == "demote" ? delete ? "delete_forbidden" : "claim_forbidden" : "staff_scope_forbidden";
            Assert.AreEqual(expected, await mutation, $"copy={additionalCopy}, delete={delete}, change={change}, workflowFirst={workflowFirst}");
            await using var verify = await contextFactory.CreateDbContextAsync();
            if (delete)
            {
                var exists = additionalCopy
                    ? await verify.AdditionalCopyRequests.AnyAsync(item => item.Id == requestId)
                    : await verify.TitleRequests.AnyAsync(item => item.Id == requestId);
                Assert.AreEqual(!workflowFirst, exists);
                Assert.AreEqual(workflowFirst ? 1 : 0, await verify.DeletedRequestAudits.CountAsync(item =>
                    item.RequestType == (additionalCopy ? "additional_copy" : "title_request") && item.OriginalRequestKey == requestId.ToString()));
            }
            else
            {
                var claim = additionalCopy
                    ? await verify.AdditionalCopyRequests.Where(item => item.Id == requestId).Select(item => item.ClaimedByStaffUserId).SingleAsync()
                    : await verify.TitleRequests.Where(item => item.Id == requestId).Select(item => item.ClaimedByStaffUserId).SingleAsync();
                Assert.AreEqual(workflowFirst ? null : (long?)assignee.Id, claim);
            }
        }
    }

    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, false, true)]
    [DataRow(false, true, false)]
    [DataRow(false, true, true)]
    [DataRow(true, false, false)]
    [DataRow(true, false, true)]
    [DataRow(true, true, false)]
    [DataRow(true, true, true)]
    public async Task GlobalStaffCleanupSerializesWithOtherLibraryTitleMutations(
        bool deactivate, bool reassign, bool lifecycleFirst)
    {
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        var lifecycleActor = await ReadConfiguredSuperAdminAsync();
        var lifecycle = factory.Services.GetRequiredService<StaffLifecycleService>();
        var titles = factory.Services.GetRequiredService<TitleRequestMutationService>();
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var otherLibrary = 91910 + (deactivate ? 4 : 0) + (reassign ? 2 : 0) + (lifecycleFirst ? 1 : 0);
        await using var seed = await contextFactory.CreateDbContextAsync();
        seed.Organizations.Add(new Organization { Id = otherLibrary, DisplayName = "Cross-library cleanup race", IsActive = true });
        await seed.SaveChangesAsync();
        var claimant = await CreateCorrectiveStaffAsync(lifecycleActor, "super_admin", 1);
        var admin = await CreateCorrectiveStaffAsync(lifecycleActor, "admin", otherLibrary);
        var workflowActor = await ReadCorrectiveStaffAsync(admin);
        var replacement = await CreateCorrectiveStaffAsync(lifecycleActor, "staff", otherLibrary);
        Assert.AreNotEqual(lifecycleActor.Id, claimant.Id);
        Assert.AreNotEqual(workflowActor.Id, claimant.Id);
        Assert.AreNotEqual(workflowActor.Id, lifecycleActor.Id);

        var formatId = await seed.MaterialFormats.Where(item => item.OwnerOrganizationId == 1 && item.Code == "book")
            .Select(item => item.Id).SingleAsync();
        var now = timeProvider!.GetUtcNow().UtcDateTime;
        var rule = new FormatAutoClaimRule
        {
            LibraryOrganizationId = otherLibrary, MaterialFormatId = formatId,
            StaffUserId = claimant.Id, IsActive = true, CreatedUtc = now
        };
        seed.FormatAutoClaimRules.Add(rule);
        await seed.SaveChangesAsync();
        TitleRequest Title(string status) => new()
        {
            LibraryOrganizationId = otherLibrary, MaterialFormatId = formatId,
            Barcode = Guid.NewGuid().ToString("N"), Title = "Global claimant cleanup race", Status = status,
            CloseReason = status == "closed" ? "manual" : null, ClaimedByStaffUserId = claimant.Id,
            ClaimedByDisplayName = claimant.DisplayName ?? claimant.UserPrincipalName,
            ClaimType = "automatic_format_rule", ClaimRuleId = rule.Id,
            ClaimedAtUtc = now, CreatedUtc = now, UpdatedUtc = now
        };
        AdditionalCopyRequest Copy(string status) => new()
        {
            LibraryOrganizationId = otherLibrary, BibId = "19001", Title = "Global claimant cleanup barrier", Status = status,
            ClaimedByStaffUserId = claimant.Id,
            ClaimedByDisplayName = claimant.DisplayName ?? claimant.UserPrincipalName,
            ClaimType = "automatic_format_rule", ClaimRuleId = rule.Id, ClaimedAtUtc = now,
            CreatedUtc = now, UpdatedUtc = now, ClosedUtc = status == "closed" ? now : null,
            Notes = "Committed copy history."
        };
        var racingTitle = Title("suggestion");
        var otherTitle = Title("suggestion");
        var closedTitle = Title("closed");
        var openCopy = Copy("open");
        var closedCopy = Copy("closed");
        seed.TitleRequests.AddRange(racingTitle, otherTitle, closedTitle);
        seed.AdditionalCopyRequests.AddRange(openCopy, closedCopy);
        await seed.SaveChangesAsync();

        Task<StaffLifecycleResult> ContractAsync() => deactivate
            ? lifecycle.DeactivateAsync(lifecycleActor, claimant.Id,
                new StaffDeactivateInput(StaffVersion.Encode(claimant.RowVersion)), CancellationToken.None)
            : lifecycle.ChangeRoleAsync(lifecycleActor, claimant.Id,
                new StaffRoleInput(StaffVersion.Encode(claimant.RowVersion), "admin", 2), CancellationToken.None);
        Task<TitleRequestMutationResult> MutateAsync() => reassign
            ? titles.AssignAsync(workflowActor, racingTitle.Id,
                new AssignTitleRequestInput(StaffVersion.Encode(racingTitle.RowVersion), replacement.Id), CancellationToken.None)
            : titles.ClaimAsync(workflowActor, racingTitle.Id,
                new VersionInput(StaffVersion.Encode(racingTitle.RowVersion)), true, CancellationToken.None);

        await using var blocker = new SqlConnection(databaseConnectionString);
        await blocker.OpenAsync();
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await using (var command = new SqlCommand(lifecycleFirst
            ? "SELECT [Id] FROM [asap].[AdditionalCopyRequest] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = @id;"
            : "SELECT COUNT_BIG(*) FROM [asap].[TitleRequestEvent] WITH (TABLOCKX,HOLDLOCK);", blocker, transaction))
        {
            if (lifecycleFirst) command.Parameters.AddWithValue("@id", openCopy.Id);
            await command.ExecuteScalarAsync();
        }

        Task<StaffLifecycleResult>? contraction = null;
        Task<TitleRequestMutationResult>? mutation = null;
        var inFlight = new List<Task>();
        Exception? barrierFailure = null;
        try
        {
            if (lifecycleFirst)
            {
                // Copy cleanup follows the title read; Org1/(2) does not serialize this other-library admin.
                contraction = ContractAsync();
                inFlight.Add(contraction);
                var lifecycleSession = await WaitForCorrectiveSqlBlockAsync(blocker.ServerProcessId);
                mutation = MutateAsync();
                inFlight.Add(mutation);
                await WaitForCorrectiveSqlBlockAsync(lifecycleSession);
            }
            else
            {
                // The actual workflow holds its Organization/Staff/title locks while its event insert waits.
                mutation = MutateAsync();
                inFlight.Add(mutation);
                var workflowSession = await WaitForCorrectiveSqlBlockAsync(blocker.ServerProcessId);
                contraction = ContractAsync();
                inFlight.Add(contraction);
                await WaitForCorrectiveSqlBlockAsync(workflowSession);
            }
        }
        catch (Exception error)
        {
            barrierFailure = error;
        }
        finally
        {
            await transaction.CommitAsync();
        }
        // Drain both real operations after releasing the barrier, including on a failed ordering assertion.
        await Task.WhenAll(inFlight);
        Assert.IsNull(barrierFailure, barrierFailure?.ToString());
        var cleanup = await contraction!;
        Assert.AreEqual("updated", cleanup.Code);
        Assert.AreEqual(lifecycleFirst ? "stale_version" : "updated", (await mutation!).Code);
        Assert.AreEqual(1, cleanup.RulesDeactivated);
        Assert.AreEqual(lifecycleFirst ? 2 : 1, cleanup.OpenTitleClaimsCleared);
        Assert.AreEqual(1, cleanup.OpenAdditionalCopyClaimsCleared);
        Assert.AreEqual("stale_version", (await MutateAsync()).Code, "The original request version cannot be reused after either winner.");

        await using var verify = await contextFactory.CreateDbContextAsync();
        var currentClaimant = await verify.StaffUsers.SingleAsync(item => item.Id == claimant.Id);
        Assert.AreEqual(!deactivate, currentClaimant.IsActive);
        Assert.AreEqual(deactivate ? "super_admin" : "admin", currentClaimant.Role);
        Assert.AreEqual(deactivate ? 1 : 2, currentClaimant.OrganizationId);
        CollectionAssert.AreNotEqual(claimant.RowVersion, currentClaimant.RowVersion);
        var currentTitle = await verify.TitleRequests.SingleAsync(item => item.Id == racingTitle.Id);
        Assert.AreEqual(!lifecycleFirst && reassign ? (long?)replacement.Id : null, currentTitle.ClaimedByStaffUserId);
        Assert.IsNull(currentTitle.ClaimRuleId);
        var events = await verify.TitleRequestEvents.Where(item => item.TitleRequestId == racingTitle.Id).ToListAsync();
        Assert.HasCount(1, events);
        Assert.AreEqual(lifecycleFirst ? "claim_cleared" : reassign ? "claim_manual_assigned" : "claim_manual_cleared", events[0].EventType);
        Assert.AreEqual(lifecycleFirst ? lifecycleActor.Id : workflowActor.Id, events[0].StaffUserId);
        if (lifecycleFirst)
        {
            using var metadata = JsonDocument.Parse(events[0].MetadataJson!);
            Assert.AreEqual(claimant.Id, metadata.RootElement.GetProperty("targetStaffUserId").GetInt64());
        }
        Assert.IsNull((await verify.TitleRequests.SingleAsync(item => item.Id == otherTitle.Id)).ClaimedByStaffUserId);
        Assert.AreEqual(1, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == otherTitle.Id && item.EventType == "claim_cleared"));
        var currentCopy = await verify.AdditionalCopyRequests.SingleAsync(item => item.Id == openCopy.Id);
        Assert.IsNull(currentCopy.ClaimedByStaffUserId);
        Assert.IsNull(currentCopy.ClaimRuleId);
        StringAssert.StartsWith(currentCopy.Notes!, "Committed copy history.");
        Assert.AreEqual(2, currentCopy.Notes!.Split("System cleared claim because the assignee's staff access changed.", StringSplitOptions.None).Length);
        Assert.IsFalse((await verify.FormatAutoClaimRules.SingleAsync(item => item.Id == rule.Id)).IsActive);
        var retainedTitle = await verify.TitleRequests.SingleAsync(item => item.Id == closedTitle.Id);
        var retainedCopy = await verify.AdditionalCopyRequests.SingleAsync(item => item.Id == closedCopy.Id);
        CollectionAssert.AreEqual(closedTitle.RowVersion, retainedTitle.RowVersion);
        CollectionAssert.AreEqual(closedCopy.RowVersion, retainedCopy.RowVersion);
        Assert.AreEqual(claimant.Id, retainedTitle.ClaimedByStaffUserId);
        Assert.AreEqual(claimant.Id, retainedCopy.ClaimedByStaffUserId);
        Assert.AreEqual("Committed copy history.", retainedCopy.Notes);
        Assert.AreEqual(0, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == closedTitle.Id));
        var audit = await verify.AdministrativeAudits.SingleAsync(item => item.TargetId == claimant.Id.ToString() &&
            item.Action == (deactivate ? "staff_deactivated" : "staff_lifecycle_updated"));
        Assert.AreEqual(lifecycleActor.Id, audit.ActorStaffUserId);
        using var details = JsonDocument.Parse(audit.DetailsJson!);
        Assert.AreEqual(1, details.RootElement.GetProperty("rulesDeactivated").GetInt32());
        Assert.AreEqual(lifecycleFirst ? 2 : 1, details.RootElement.GetProperty("openTitleClaimsCleared").GetInt32());
        Assert.AreEqual(1, details.RootElement.GetProperty("openAdditionalCopyClaimsCleared").GetInt32());
    }

    private static async Task<int> WaitForCorrectiveSqlBlockAsync(int blockerSessionId)
    {
        await using var observer = new SqlConnection(databaseConnectionString);
        await observer.OpenAsync();
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(7))
        {
            await using var query = new SqlCommand(
                "SELECT TOP(1) [session_id] FROM sys.dm_exec_requests WHERE [blocking_session_id] = @blocker AND [database_id] = DB_ID();", observer);
            query.Parameters.AddWithValue("@blocker", blockerSessionId);
            var result = await query.ExecuteScalarAsync();
            if (result is not null && result is not DBNull) return Convert.ToInt32(result);
            await Task.Delay(20);
        }
        Assert.Fail($"Expected an actual SQL session waiting behind {blockerSessionId}.");
        return 0;
    }
}
