using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Asap.Web.Features.Email;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    private const int EmailHistoryLibrary = 384459;
    private const int EmailHistoryOtherLibrary = 384460;
    private const long EmailHistoryHighId = 9_007_199_254_741_993;
    private const string EmailHistoryMarker = "i459-history-private";
    private const string EmailHistoryRange = "fromUtc=2030-01-01T00%3A00%3A00Z&beforeUtc=2030-01-03T00%3A00%3A00Z";

    [TestMethod]
    public async Task EmailHistoryPagesFullScopeInBothDirectionsAndKeepsExactReferences()
    {
        using var client = factory!.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        await SeedEmailHistoryAsync(actor);
        try
        {
            var stored = await ReadEmailHistoryAsync(EmailHistoryLibrary);
            Assert.IsGreaterThan(500, stored.Length);
            using var first = await EmailHistoryGetAsync(client, $"scope={EmailHistoryLibrary}");
            var data = first.RootElement;
            Assert.AreEqual(stored.LongLength, data.GetProperty("count").GetInt64());
            Assert.AreEqual(25, data.GetProperty("pageSize").GetInt32());
            Assert.AreEqual(25, data.GetProperty("items").GetArrayLength());
            Assert.AreEqual((EmailHistoryHighId + 1).ToString(CultureInfo.InvariantCulture), data.GetProperty("items")[0].GetProperty("id").GetString());
            Assert.AreEqual(EmailHistoryHighId.ToString(CultureInfo.InvariantCulture), data.GetProperty("items")[1].GetProperty("id").GetString());
            Assert.AreEqual("pending", data.GetProperty("items")[0].GetProperty("status").GetString(), "Failures must not outrank chronological order.");
            Assert.HasCount(3, data.GetProperty("types").EnumerateArray().ToArray());
            foreach (var direction in new[] { "oldest", "newest" })
            {
                var expected = direction == "oldest"
                    ? stored.OrderBy(item => item.CreatedUtc).ThenBy(item => item.Id)
                    : stored.OrderByDescending(item => item.CreatedUtc).ThenByDescending(item => item.Id);
                var observed = new List<string>();
                for (var page = 1; page <= (stored.Length + 99) / 100; page++)
                {
                    using var result = await EmailHistoryGetAsync(client, $"scope={EmailHistoryLibrary}&sort={direction}&pageSize=100&page={page}");
                    Assert.AreEqual(stored.LongLength, result.RootElement.GetProperty("count").GetInt64());
                    Assert.AreEqual(page, result.RootElement.GetProperty("page").GetInt32());
                    observed.AddRange(result.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!));
                }
                CollectionAssert.AreEqual(expected.Select(item => item.Id.ToString(CultureInfo.InvariantCulture)).ToArray(), observed.ToArray());
                Assert.AreEqual(stored.Length, observed.Distinct().Count(), "Equal SQL timestamps cannot duplicate or omit numeric references.");
            }
            var oldest = stored.OrderBy(item => item.CreatedUtc).ThenBy(item => item.Id).First();
            foreach (var parameter in new[] { "search", "reference" })
            {
                using var exact = await EmailHistoryGetAsync(client, $"scope={EmailHistoryLibrary}&{parameter}={oldest.Id}");
                Assert.AreEqual(1L, exact.RootElement.GetProperty("count").GetInt64(), "References older than the former 500-row slice remain reachable.");
                Assert.AreEqual(oldest.Id.ToString(CultureInfo.InvariantCulture), exact.RootElement.GetProperty("items")[0].GetProperty("id").GetString());
            }
            using var high = await EmailHistoryGetAsync(client, $"scope={EmailHistoryLibrary}&reference={EmailHistoryHighId}");
            Assert.AreEqual(EmailHistoryHighId.ToString(CultureInfo.InvariantCulture), high.RootElement.GetProperty("items")[0].GetProperty("id").GetString());
            using var clamped = await EmailHistoryGetAsync(client, $"scope={EmailHistoryLibrary}&page=999&pageSize=100");
            Assert.AreEqual((stored.Length + 99) / 100, clamped.RootElement.GetProperty("page").GetInt32());
            using var empty = await EmailHistoryGetAsync(client, $"scope={EmailHistoryLibrary}&search=unmatched-value&page=999");
            Assert.AreEqual(0L, empty.RootElement.GetProperty("count").GetInt64());
            Assert.AreEqual(1, empty.RootElement.GetProperty("page").GetInt32());
            Assert.AreEqual(0, empty.RootElement.GetProperty("items").GetArrayLength());
            AssertEmailHistoryReadOnly();
        }
        finally
        {
            await CleanupEmailHistoryAsync();
        }
    }

    [TestMethod]
    public async Task EmailHistoryProjectsAndSearchesCancelledPreSendReleaseWithoutPrivateDetail()
    {
        using var client = factory!.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        await SeedEmailHistoryAsync(actor);
        try
        {
            var stored = (await ReadEmailHistoryAsync(EmailHistoryLibrary)).Single(item => item.LastErrorCode == "pre_send_cancelled");
            Assert.AreEqual("pending", stored.Status);
            Assert.AreEqual(0, stored.AttemptCount);
            Assert.IsNull(stored.SendingStartedUtc);
            Assert.IsNull(stored.LeaseId);
            Assert.IsNull(stored.LeaseExpiresUtc);
            Assert.IsNull(stored.ProviderMessageId);
            Assert.IsTrue(stored.NextAttemptUtc > stored.LastAttemptUtc);
            foreach (var search in new[] { "pre_send_cancelled", "Pre-send cancelled", "send not started" })
            {
                using var result = await EmailHistoryGetAsync(client,
                    $"scope={EmailHistoryLibrary}&status=pending&type=business_event&search={Uri.EscapeDataString(search)}");
                Assert.AreEqual(1L, result.RootElement.GetProperty("count").GetInt64());
                var row = result.RootElement.GetProperty("items")[0];
                Assert.AreEqual(stored.Id.ToString(CultureInfo.InvariantCulture), row.GetProperty("id").GetString());
                Assert.AreEqual("pre_send_cancelled", row.GetProperty("lastErrorCode").GetString());
                Assert.AreEqual("Pre-send cancelled; send not started", row.GetProperty("detailLabel").GetString());
                Assert.IsFalse(row.GetProperty("canRetry").GetBoolean());
                Assert.IsFalse(row.TryGetProperty("lastErrorDetail", out _));
                Assert.IsFalse(row.GetRawText().Contains(stored.LastErrorDetail!, StringComparison.Ordinal));
            }
            using var privateSearch = await EmailHistoryGetAsync(client,
                $"scope={EmailHistoryLibrary}&search={Uri.EscapeDataString(stored.LastErrorDetail!)}");
            Assert.AreEqual(0L, privateSearch.RootElement.GetProperty("count").GetInt64());
            AssertEmailHistoryReadOnly();
        }
        finally
        {
            await CleanupEmailHistoryAsync();
        }
    }

    [TestMethod]
    public async Task EmailHistoryFiltersAndCountsUseOnlySafeDisplayedFields()
    {
        using var client = factory!.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        await SeedEmailHistoryAsync(actor);
        try
        {
            var stored = await ReadEmailHistoryAsync(EmailHistoryLibrary);
            foreach (var status in new[] { "pending", "sending", "sent", "failed", "suppressed" })
            {
                foreach (var type in new[] { "business_event", "staff_authorization_sensitive", "operational_test" })
                {
                    using var filtered = await EmailHistoryGetAsync(client, $"scope={EmailHistoryLibrary}&status={status}&type={type}&{EmailHistoryRange}");
                    var expected = stored.Where(item => item.Status == status && item.DeliveryClass == type && item.CreatedUtc.Year == 2030).ToArray();
                    Assert.AreEqual(expected.LongLength, filtered.RootElement.GetProperty("count").GetInt64(), $"{status}/{type}");
                    Assert.IsTrue(filtered.RootElement.GetProperty("items").EnumerateArray().All(item =>
                        item.GetProperty("status").GetString() == status && item.GetProperty("deliveryClass").GetString() == type));
                }
            }
            foreach (var (search, expected) in new[]
            {
                ("business_event", stored.LongCount(item => item.DeliveryClass == "business_event")),
                ("Staff notification", stored.LongCount(item => item.DeliveryClass == "staff_authorization_sensitive")),
                ("mail_not_configured", stored.LongCount(item => item.LastErrorCode == "mail_not_configured")),
                ("Recipient domain not allowed", stored.LongCount(item => item.SuppressionReason == "recipient_domain_not_allowed"))
            })
            {
                using var found = await EmailHistoryGetAsync(client, $"scope={EmailHistoryLibrary}&search={Uri.EscapeDataString(search)}");
                Assert.AreEqual(expected, found.RootElement.GetProperty("count").GetInt64(), search);
            }
            foreach (var search in new[] { EmailHistoryMarker, "hidden-subject", "private-recipient", "hidden-body", "hidden-error-detail", "hidden-error-code", "hidden-suppression", "hidden-provider", "hidden-from-name" })
            {
                using var hidden = await EmailHistoryGetAsync(client, $"scope={EmailHistoryLibrary}&search={search}");
                Assert.AreEqual(0L, hidden.RootElement.GetProperty("count").GetInt64(), $"Private values cannot influence counts: {search}");
            }
            using var page = await EmailHistoryGetAsync(client, $"scope={EmailHistoryLibrary}&status=failed&pageSize=100");
            var raw = page.RootElement.GetRawText();
            foreach (var hidden in new[] { EmailHistoryMarker, "hidden-error", "private-recipient", "hidden-provider", "hidden-body", "hidden-subject" })
            {
                Assert.IsFalse(raw.Contains(hidden, StringComparison.Ordinal), hidden);
            }
            var unknown = stored.Single(item => item.LastErrorCode == "hidden-error-code");
            using var unknownResult = await EmailHistoryGetAsync(client, $"scope={EmailHistoryLibrary}&reference={unknown.Id}");
            var row = unknownResult.RootElement.GetProperty("items")[0];
            Assert.AreEqual(JsonValueKind.Null, row.GetProperty("lastErrorCode").ValueKind);
            Assert.AreEqual("Recorded detail unavailable", row.GetProperty("detailLabel").GetString());
            foreach (var field in new[] { "businessKey", "toAddress", "fromAddress", "subject", "bodyText", "bodyHtml", "lastErrorDetail", "providerMessageId" })
            {
                Assert.IsFalse(row.TryGetProperty(field, out _), field);
            }
            // Exact operational-test recovery remains independent of filters and pages.
            var test = stored.Where(item => item.DeliveryClass == "operational_test").MinBy(item => item.CreatedUtc)!;
            using var exact = await client.GetAsync($"/api/asap/staff/email-operations/{test.Id}?organizationId={EmailHistoryLibrary}");
            Assert.AreEqual(HttpStatusCode.OK, exact.StatusCode);
            using var exactBody = JsonDocument.Parse(await exact.Content.ReadAsStringAsync());
            var exactRow = exactBody.RootElement.GetProperty("data");
            Assert.AreEqual(test.Id.ToString(CultureInfo.InvariantCulture), exactRow.GetProperty("id").GetString());
            Assert.AreEqual(Convert.ToBase64String(test.RowVersion), exactRow.GetProperty("version").GetString());
            foreach (var field in new[] { "businessKey", "toAddress", "subject", "bodyText", "bodyHtml", "lastErrorDetail", "providerMessageId" })
            {
                Assert.IsFalse(exactRow.TryGetProperty(field, out _), field);
            }
            var before = JsonSerializer.Serialize(stored);
            var after = JsonSerializer.Serialize(await ReadEmailHistoryAsync(EmailHistoryLibrary));
            Assert.AreEqual(before, after, "Browsing cannot change outbox state or rowversions.");
            AssertEmailHistoryReadOnly();
        }
        finally
        {
            await CleanupEmailHistoryAsync();
        }
    }

    [TestMethod]
    public async Task EmailHistorySeparatesSystemAggregateAndLibraryAuthority()
    {
        using var client = factory!.CreateClient();
        var super = await ReadConfiguredSuperAdminAsync();
        await SeedEmailHistoryAsync(super);
        var admin = await CreateCorrectiveStaffAsync(super, StaffRole.Admin, EmailHistoryLibrary);
        var staff = await CreateCorrectiveStaffAsync(super, StaffRole.Staff, EmailHistoryLibrary);
        try
        {
            AddTestingStaffHeaders(client, super.Id, super.EntraTenantId, super.AuthenticationEmail);
            foreach (var (scope, count) in new[] { ("system", 8L), ("all", 590L), ($"{EmailHistoryLibrary}", 522L), ($"{EmailHistoryOtherLibrary}", 60L) })
            {
                using var result = await EmailHistoryGetAsync(client, $"scope={scope}&{EmailHistoryRange}");
                Assert.AreEqual(count, result.RootElement.GetProperty("count").GetInt64(), scope);
                Assert.AreEqual(scope, result.RootElement.GetProperty("scope").GetString());
            }
            var actor = await ReadCorrectiveStaffAsync(admin);
            using var adminClient = factory.CreateClient();
            AddTestingStaffHeaders(adminClient, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            using var own = await EmailHistoryGetAsync(adminClient, EmailHistoryRange);
            Assert.AreEqual(522L, own.RootElement.GetProperty("count").GetInt64());
            Assert.AreEqual(1, own.RootElement.GetProperty("libraries").GetArrayLength());
            Assert.AreEqual(EmailHistoryLibrary, own.RootElement.GetProperty("libraries")[0].GetProperty("id").GetInt32());
            Assert.IsTrue(own.RootElement.GetProperty("items").EnumerateArray().All(item => item.GetProperty("organizationId").GetInt32() == EmailHistoryLibrary));
            var service = factory.Services.GetRequiredService<EmailOperationsService>();
            foreach (var forbidden in new[] { LibraryScope.All, LibraryScope.System, LibraryScope.ForLibrary(EmailHistoryOtherLibrary) })
            {
                var direct = await service.ListAsync(actor, new EmailOperationsQuery(forbidden), CancellationToken.None);
                Assert.AreEqual("staff_scope_forbidden", direct.Code);
                Assert.IsNull(direct.Data, "Denied service calls expose no count, rows or choices.");
                using var response = await adminClient.GetAsync($"/api/asap/staff/email-operations?scope={forbidden.ToTransportValue()}");
                Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.IsFalse((await response.Content.ReadAsStringAsync()).Contains("count", StringComparison.Ordinal));
            }
            var ordinary = await ReadCorrectiveStaffAsync(staff);
            using var staffClient = factory.CreateClient();
            AddTestingStaffHeaders(staffClient, ordinary.Id, ordinary.EntraTenantId, ordinary.AuthenticationEmail);
            foreach (var scope in new[] { LibraryScope.All, LibraryScope.System, LibraryScope.ForLibrary(EmailHistoryLibrary) })
            {
                var direct = await service.ListAsync(ordinary, new EmailOperationsQuery(scope), CancellationToken.None);
                Assert.AreEqual("staff_scope_forbidden", direct.Code);
                Assert.IsNull(direct.Data);
                using var response = await staffClient.GetAsync($"/api/asap/staff/email-operations?scope={scope.ToTransportValue()}");
                Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
            }
            AssertEmailHistoryReadOnly();
        }
        finally
        {
            await DeactivateCorrectiveStaffAsync(admin.Id);
            await DeactivateCorrectiveStaffAsync(staff.Id);
            await CleanupEmailHistoryAsync();
        }
    }

    [TestMethod]
    [DataRow("inactive")]
    [DataRow("role")]
    [DataRow("email")]
    [DataRow("organization")]
    [DataRow("promotion")]
    public async Task EmailHistoryRejectsChangedCurrentActorBeforeReturningRowsCountsOrOptions(string mutation)
    {
        var super = await ReadConfiguredSuperAdminAsync();
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await EnsureSlice5IsolatedLibraryAsync(contexts, EmailHistoryLibrary);
        await EnsureSlice5IsolatedLibraryAsync(contexts, EmailHistoryOtherLibrary);
        var created = await CreateCorrectiveStaffAsync(super, StaffRole.Admin, EmailHistoryLibrary);
        var actor = await ReadCorrectiveStaffAsync(created);
        try
        {
            await using (var context = await contexts.CreateDbContextAsync())
            {
                var row = await context.StaffUsers.SingleAsync(item => item.Id == actor.Id);
                if (mutation == "inactive")
                {
                    row.IsActive = false;
                }
                if (mutation == "role")
                {
                    row.Role = StaffRole.Staff;
                }
                if (mutation == "email")
                {
                    row.UserPrincipalName = "i459.changed@example.org";
                    row.NormalizedUserPrincipalName = "I459.CHANGED@EXAMPLE.ORG";
                }
                if (mutation == "organization")
                {
                    row.OrganizationId = EmailHistoryOtherLibrary;
                }
                if (mutation == "promotion")
                {
                    row.Role = StaffRole.SuperAdmin;
                    row.OrganizationId = LibraryScope.SystemOrganizationId;
                }
                await context.SaveChangesAsync();
            }
            var result = await factory.Services.GetRequiredService<EmailOperationsService>().ListAsync(actor,
                new EmailOperationsQuery(LibraryScope.ForLibrary(EmailHistoryLibrary)), CancellationToken.None);
            Assert.AreNotEqual("ok", result.Code);
            Assert.IsNull(result.Data, "Stale tickets cannot disclose even empty counts or library options.");
        }
        finally
        {
            await DeactivateCorrectiveStaffAsync(created.Id);
        }
    }

    [TestMethod]
    public async Task EmailHistoryRejectsStaleSuperAdminDemotedIntoRequestedLibrary()
    {
        var super = await ReadConfiguredSuperAdminAsync();
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await EnsureSlice5IsolatedLibraryAsync(contexts, EmailHistoryLibrary);
        var created = await CreateCorrectiveStaffAsync(super, StaffRole.SuperAdmin, LibraryScope.SystemOrganizationId);
        var stale = await ReadCorrectiveStaffAsync(created);
        try
        {
            var changed = await factory.Services.GetRequiredService<StaffLifecycleService>().ChangeRoleAsync(super, created.Id,
                new StaffRoleInput(StaffVersion.Encode(created.RowVersion), StaffRole.Admin, EmailHistoryLibrary), CancellationToken.None);
            Assert.AreEqual("updated", changed.Code);
            var service = factory.Services.GetRequiredService<EmailOperationsService>();
            foreach (var scope in new[] { LibraryScope.All, LibraryScope.System, LibraryScope.ForLibrary(EmailHistoryLibrary) })
            {
                var result = await service.ListAsync(stale, new EmailOperationsQuery(scope), CancellationToken.None);
                Assert.AreNotEqual("ok", result.Code);
                Assert.IsNull(result.Data);
            }
            var current = await ReadCorrectiveStaffAsync(changed.User!);
            var own = await service.ListAsync(current, new EmailOperationsQuery(LibraryScope.ForLibrary(EmailHistoryLibrary)), CancellationToken.None);
            Assert.AreEqual("ok", own.Code);
            Assert.HasCount(1, ((EmailOperationsPage)own.Data!).Libraries);
        }
        finally
        {
            await DeactivateCorrectiveStaffAsync(created.Id);
        }
    }

    [TestMethod]
    [DataRow("2026-03-08T05:00:00Z", "2026-03-09T04:00:00Z")]
    [DataRow("2026-11-01T04:00:00Z", "2026-11-02T05:00:00Z")]
    public async Task EmailHistoryCreatedBoundsAreHalfOpenAcrossLocalDstDays(string from, string before)
    {
        using var client = factory!.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        await SeedEmailHistoryAsync(actor);
        try
        {
            using var result = await EmailHistoryGetAsync(client, $"scope={EmailHistoryLibrary}&fromUtc={Uri.EscapeDataString(from)}&beforeUtc={Uri.EscapeDataString(before)}");
            Assert.AreEqual(2L, result.RootElement.GetProperty("count").GetInt64());
            var lower = DateTimeOffset.Parse(from, CultureInfo.InvariantCulture);
            var upper = DateTimeOffset.Parse(before, CultureInfo.InvariantCulture);
            foreach (var row in result.RootElement.GetProperty("items").EnumerateArray())
            {
                var timestamp = row.GetProperty("createdUtc").GetDateTimeOffset();
                Assert.IsTrue(timestamp >= lower && timestamp < upper);
                Assert.IsTrue(row.GetProperty("createdUtc").GetString()!.EndsWith('Z'));
            }
            using var fromOnly = await EmailHistoryGetAsync(client, $"scope={EmailHistoryLibrary}&fromUtc={Uri.EscapeDataString(before)}&beforeUtc={Uri.EscapeDataString(upper.AddTicks(1).ToString("O", CultureInfo.InvariantCulture))}");
            Assert.AreEqual(1L, fromOnly.RootElement.GetProperty("count").GetInt64(), "The exact end boundary belongs to the next range, retaining datetime2(7) precision.");
        }
        finally
        {
            await CleanupEmailHistoryAsync();
        }
    }

    [TestMethod]
    public async Task EmailHistoryValidatesBoundsAndCancelsBeforeAnyRead()
    {
        using var client = factory!.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        foreach (var bad in new[]
        {
            "page=0", "page=-1", "pageSize=0", "pageSize=101", "page=2147483647&pageSize=100",
            "page=2147483648", "sort=failed-first", "status=delivered", "type=patron", "scope=invalid", "scope=0",
            "scope=system&organizationId=1", "reference=01", "reference=0", "reference=-1", "reference=9223372036854775808",
            "search=" + new string('x', 201), "fromUtc=invalid", "beforeUtc=2026-02-30T00:00:00Z",
            "fromUtc=2026-11-02T00:00:00Z&beforeUtc=2026-11-01T00:00:00Z",
            "fromUtc=2026-11-02T00:00:00Z&beforeUtc=2026-11-02T00:00:00Z"
        })
        {
            using var response = await client.GetAsync($"/api/asap/staff/email-operations?{bad}");
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, bad);
        }
        using var missing = await client.GetAsync("/api/asap/staff/email-operations?scope=2147483647");
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        var service = factory.Services.GetRequiredService<EmailOperationsService>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => service.ListAsync(actor,
            new EmailOperationsQuery(LibraryScope.System), cancellation.Token));
        AssertEmailHistoryReadOnly();
    }

    [TestMethod]
    public async Task EmailHistoryCancelsDmvConfirmedBlockedCountAndRollsBackReadTransaction()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        await SeedEmailHistoryAsync(actor);
        try
        {
            var before = JsonSerializer.Serialize(await ReadEmailHistoryAsync(EmailHistoryLibrary));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var cancellation = new CancellationTokenSource();
            await using var blocker = new SqlConnection(databaseConnectionString);
            await blocker.OpenAsync(deadline.Token);
            await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync(deadline.Token);
            Task<EmailOperationResult>? reading = null;
            try
            {
                await using (var tableLock = new SqlCommand(
                    "SELECT COUNT_BIG(*) FROM [asap].[EmailOutbox] WITH (TABLOCKX,HOLDLOCK);", blocker, transaction))
                {
                    await tableLock.ExecuteScalarAsync(deadline.Token);
                }
                await using var observer = new SqlConnection(databaseConnectionString);
                await observer.OpenAsync(deadline.Token);
                var probe = new EmailHistoryBlockedCountProbe();
                var contexts = new EmailHistoryContextFactory(databaseConnectionString, probe);
                var service = new EmailOperationsService(contexts, dispatcher!, factory!.Services.GetRequiredService<IEmailSender>(),
                    factory.Services.GetRequiredService<RecipientDomainPolicy>(), timeProvider!, factory.Services.GetRequiredService<StaffEligibilityService>());
                reading = service.ListAsync(actor, new EmailOperationsQuery(LibraryScope.ForLibrary(EmailHistoryLibrary)), cancellation.Token);
                var readerSession = await probe.CountSession.Task.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
                await WaitForEmailHistoryCountBlockAsync(observer, readerSession, blocker.ServerProcessId, deadline.Token);

                var elapsed = Stopwatch.StartNew();
                cancellation.Cancel();
                var cancelled = await Assert.ThrowsAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token));
                Assert.AreEqual(cancellation.Token, cancelled.CancellationToken);
                TestContext.WriteLine($"DMV-confirmed history reader {readerSession} blocked by {blocker.ServerProcessId} cancelled in {elapsed.Elapsed.TotalMilliseconds:F1} ms; inner exception: {cancelled.InnerException?.GetType().Name ?? "none"}.");

                await using var readerTransactions = new SqlCommand("""
                    SELECT COUNT(*) FROM sys.dm_tran_session_transactions
                    WHERE [session_id] = @reader AND [is_user_transaction] = 1;
                    """, observer);
                readerTransactions.Parameters.AddWithValue("@reader", readerSession);
                Assert.AreEqual(0, Convert.ToInt32(await readerTransactions.ExecuteScalarAsync(deadline.Token)),
                    "The cancelled history read must release its serializable transaction before the blocker is released.");
                await using var blockerTransaction = new SqlCommand("SELECT @@TRANCOUNT;", blocker, transaction);
                Assert.AreEqual(1, Convert.ToInt32(await blockerTransaction.ExecuteScalarAsync(deadline.Token)),
                    "Cancellation must finish while this test still holds its blocking transaction.");
            }
            finally
            {
                cancellation.Cancel();
                await transaction.RollbackAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                if (reading is not null)
                {
                    // Drain a failed assertion's read after releasing only our own lock.
                    await reading.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            Assert.AreEqual(before, JsonSerializer.Serialize(await ReadEmailHistoryAsync(EmailHistoryLibrary)));
            AssertEmailHistoryReadOnly();
        }
        finally
        {
            await CleanupEmailHistoryAsync();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EmailHistoryPreservesUnrelatedSqlFailureEvenWhenCallerCancels(bool cancelAfterFailure)
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        using var cancellation = new CancellationTokenSource();
        var probe = new EmailHistoryFailureProbe(cancelAfterFailure ? cancellation : null);
        var contexts = new EmailHistoryContextFactory(databaseConnectionString, probe);
        var service = new EmailOperationsService(contexts, dispatcher!, factory!.Services.GetRequiredService<IEmailSender>(),
            factory.Services.GetRequiredService<RecipientDomainPolicy>(), timeProvider!, factory.Services.GetRequiredService<StaffEligibilityService>());
        var failure = await Assert.ThrowsExactlyAsync<SqlException>(() => service.ListAsync(actor,
            new EmailOperationsQuery(LibraryScope.System), cancellation.Token));
        Assert.IsTrue(probe.Injected);
        Assert.AreEqual(51059, failure.Number, "A genuine SQL failure must not be relabelled as caller cancellation.");
        Assert.AreEqual(cancelAfterFailure, cancellation.IsCancellationRequested);
        AssertEmailHistoryReadOnly();
    }

    [TestMethod]
    public async Task EmailHistoryExecutesBoundedParameterizedSqlWithoutReadingMessagePayloads()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        await SeedEmailHistoryAsync(actor);
        try
        {
            var probe = new EmailHistorySqlProbe();
            var contexts = new EmailHistoryContextFactory(databaseConnectionString, probe);
            var service = new EmailOperationsService(contexts, dispatcher!, factory!.Services.GetRequiredService<IEmailSender>(),
                factory.Services.GetRequiredService<RecipientDomainPolicy>(), timeProvider!, factory.Services.GetRequiredService<StaffEligibilityService>());
            var result = await service.ListAsync(actor, new EmailOperationsQuery(LibraryScope.ForLibrary(EmailHistoryLibrary),
                Search: "not configured", Status: "failed", Type: "operational_test", Page: 2, PageSize: 10), CancellationToken.None);
            Assert.AreEqual("ok", result.Code);
            var history = probe.Commands.Where(item => item.Sql.Contains("[asap].[EmailOutbox]", StringComparison.Ordinal)).ToArray();
            Assert.HasCount(2, history, "Count and bounded page execute the same authorized predicates.");
            Assert.IsTrue(history[0].Sql.Contains("COUNT_BIG(*)", StringComparison.Ordinal));
            Assert.IsTrue(history[1].Sql.Contains("OFFSET", StringComparison.Ordinal) && history[1].Sql.Contains("FETCH NEXT", StringComparison.Ordinal));
            foreach (var command in history)
            {
                Assert.IsTrue(command.ParameterCount > 0);
                foreach (var hidden in new[] { "[BodyText]", "[BodyHtml]", "[Subject]", "[ToAddress]", "[LastErrorDetail]", "[BusinessKey]" })
                {
                    Assert.IsFalse(command.Sql.Contains(hidden, StringComparison.Ordinal), hidden);
                }
                TestContext.WriteLine(command.Sql);
            }
            AssertEmailHistoryReadOnly();
        }
        finally
        {
            await CleanupEmailHistoryAsync();
        }
    }

    private static async Task<JsonDocument> EmailHistoryGetAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync($"/api/asap/staff/email-operations?{query}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        return JsonDocument.Parse(body);
    }

    private void AssertEmailHistoryReadOnly()
    {
        Assert.HasCount(0, dispatcher!.EnqueuedIds);
        Assert.HasCount(0, ((RecordingEmailSender)factory!.Services.GetRequiredService<IEmailSender>()).Envelopes);
    }

    private async Task<EmailOutbox[]> ReadEmailHistoryAsync(int organizationId)
    {
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>().CreateDbContextAsync();
        return await context.EmailOutbox.AsNoTracking().Where(item => item.OrganizationId == organizationId).OrderBy(item => item.Id).ToArrayAsync();
    }

    private async Task SeedEmailHistoryAsync(CurrentStaff actor)
    {
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await EnsureSlice5IsolatedLibraryAsync(contexts, EmailHistoryLibrary);
        await EnsureSlice5IsolatedLibraryAsync(contexts, EmailHistoryOtherLibrary);
        await using var context = await contexts.CreateDbContextAsync();
        var created = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        context.EmailOutbox.AddRange(Enumerable.Range(0, 520).Select(index => EmailHistoryRow(EmailHistoryLibrary, index, created.AddMinutes(index / 2), actor)));
        context.EmailOutbox.AddRange(Enumerable.Range(0, 60).Select(index => EmailHistoryRow(EmailHistoryOtherLibrary, index, created.AddMinutes(index), actor)));
        context.EmailOutbox.AddRange(Enumerable.Range(0, 8).Select(index => EmailHistoryRow(1, index, created.AddMinutes(index), actor)));
        var boundaries = new[]
        {
            "2026-03-08T05:00:00.0000000Z", "2026-03-09T03:59:59.9999999Z", "2026-03-09T04:00:00.0000000Z",
            "2026-11-01T04:00:00.0000000Z", "2026-11-02T04:59:59.9999999Z", "2026-11-02T05:00:00.0000000Z"
        };
        context.EmailOutbox.AddRange(boundaries.Select((value, index) => EmailHistoryRow(EmailHistoryLibrary, 1000 + index,
            DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), actor)));
        await context.SaveChangesAsync();
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SET IDENTITY_INSERT [asap].[EmailOutbox] ON;
            INSERT INTO [asap].[EmailOutbox]
                ([Id],[OrganizationId],[BusinessKey],[DeliveryClass],[Status],[AttemptCount],[CreatedUtc],[ToAddress],[FromAddress],[Subject],[BodyText])
            VALUES (@id,@scope,@key,N'operational_test',N'pending',0,'2030-01-02T12:00:00.1234567',N'private-recipient@example.org',N'sender@example.org',N'hidden-subject',N'hidden-body'),
                   (@id+1,@scope,@key2,N'operational_test',N'pending',0,'2030-01-02T12:00:00.1234567',N'private-recipient@example.org',N'sender@example.org',N'hidden-subject',N'hidden-body');
            SET IDENTITY_INSERT [asap].[EmailOutbox] OFF;
            """;
        command.Parameters.AddWithValue("@id", EmailHistoryHighId);
        command.Parameters.AddWithValue("@scope", EmailHistoryLibrary);
        command.Parameters.AddWithValue("@key", $"{EmailHistoryMarker}:high");
        command.Parameters.AddWithValue("@key2", $"{EmailHistoryMarker}:high2");
        await command.ExecuteNonQueryAsync();
    }

    private static EmailOutbox EmailHistoryRow(int organizationId, int index, DateTime created, CurrentStaff actor)
    {
        var status = new[] { "pending", "sending", "sent", "failed", "suppressed" }[index % 5];
        var type = new[] { "business_event", "staff_authorization_sensitive", "operational_test" }[index % 3];
        return new EmailOutbox
        {
            OrganizationId = organizationId, BusinessKey = $"{EmailHistoryMarker}:{organizationId}:{index}", DeliveryClass = type,
            RecipientStaffUserId = type == "staff_authorization_sensitive" ? actor.Id : null,
            RecipientAuthenticationEmail = type == "staff_authorization_sensitive" ? actor.AuthenticationEmail : null,
            AuthorizationOrganizationId = type == "staff_authorization_sensitive" ? organizationId : null,
            RecipientAddressKind = type == "staff_authorization_sensitive" ? "notification_email" : null,
            ToAddress = "private-recipient@example.org", FromAddress = "hidden-sender@example.org", FromName = "hidden-from-name",
            Subject = "hidden-subject", BodyText = "hidden-body", BodyHtml = "<p>hidden-body</p>",
            LastErrorDetail = index == 0 ? "The email transport readiness check did not complete." : "hidden-error-detail",
            Status = status, CreatedUtc = created, SentUtc = status == "sent" ? created.AddSeconds(1) : null,
            SuppressedUtc = status == "suppressed" ? created.AddSeconds(1) : null,
            SuppressionReason = status == "suppressed" ? index == 4 ? "hidden-suppression" : "recipient_domain_not_allowed" : null,
            LastErrorCode = index == 0 ? "pre_send_cancelled" : status == "failed" ? index == 3 ? "hidden-error-code" : "mail_not_configured" : null,
            LastAttemptUtc = index == 0 ? created.AddSeconds(30) : null,
            NextAttemptUtc = index == 0 ? created.AddMinutes(1) : null,
            AttemptCount = status is "sent" or "sending" or "failed" ? 1 : 0,
            ProviderMessageId = status == "sent" ? "hidden-provider" : null,
            AttemptDeliveryMode = status == "sent" ? "postmark" : null,
            SendingStartedUtc = status == "sending" ? created : null,
            LeaseId = status == "sending" ? Guid.NewGuid() : null,
            LeaseExpiresUtc = status == "sending" ? created.AddMinutes(2) : null
        };
    }

    private static async Task CleanupEmailHistoryAsync()
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM [asap].[EmailOutbox] WHERE [BusinessKey] LIKE @prefix;
            DECLARE @maximum bigint = COALESCE((SELECT MAX([Id]) FROM [asap].[EmailOutbox]),0);
            DBCC CHECKIDENT ('asap.EmailOutbox', RESEED, @maximum) WITH NO_INFOMSGS;
            """;
        command.Parameters.AddWithValue("@prefix", $"{EmailHistoryMarker}:%");
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WaitForEmailHistoryCountBlockAsync(
        SqlConnection observer, int readerSession, int blockerSession, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            await using var request = new SqlCommand("""
                SELECT COUNT(*) FROM sys.dm_exec_requests AS request
                CROSS APPLY sys.dm_exec_sql_text(request.[sql_handle]) AS statement
                WHERE request.[session_id] = @reader AND request.[blocking_session_id] = @blocker
                    AND request.[database_id] = DB_ID() AND request.[wait_type] LIKE N'LCK_M_%'
                    AND CHARINDEX(N'COUNT_BIG', statement.[text]) > 0
                    AND CHARINDEX(N'[asap].[EmailOutbox]', statement.[text]) > 0;
                """, observer) { CommandTimeout = 5 };
            request.Parameters.AddWithValue("@reader", readerSession);
            request.Parameters.AddWithValue("@blocker", blockerSession);
            if (Convert.ToInt32(await request.ExecuteScalarAsync(cancellationToken)) == 1)
            {
                return;
            }
            await Task.Delay(20, cancellationToken);
        }
        Assert.Fail($"Expected history count session {readerSession} to be blocked by owned session {blockerSession}.");
    }

    private sealed class EmailHistoryContextFactory(string connectionString, IInterceptor probe) : IDbContextFactory<AsapDbContext>
    {
        public AsapDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AsapDbContext>()
            .UseSqlServer(connectionString).AddInterceptors(probe).Options);
    }

    private sealed class EmailHistorySqlProbe : DbCommandInterceptor
    {
        public List<(string Sql, int ParameterCount)> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add((command.CommandText, command.Parameters.Count));
            return ValueTask.FromResult(result);
        }
    }

    private sealed class EmailHistoryBlockedCountProbe : DbCommandInterceptor
    {
        public TaskCompletionSource<int> CountSession { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("COUNT_BIG", StringComparison.Ordinal) &&
                command.CommandText.Contains("[asap].[EmailOutbox]", StringComparison.Ordinal))
            {
                CountSession.TrySetResult(((SqlConnection)command.Connection!).ServerProcessId);
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class EmailHistoryFailureProbe(CancellationTokenSource? cancelAfterFailure) : DbCommandInterceptor
    {
        public bool Injected { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("COUNT_BIG", StringComparison.Ordinal) &&
                command.CommandText.Contains("[asap].[EmailOutbox]", StringComparison.Ordinal))
            {
                command.CommandText = "THROW 51059, N'Synthetic email history query failure.', 1;";
                Injected = true;
            }
            return ValueTask.FromResult(result);
        }

        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Exception is SqlException { Number: 51059 })
            {
                cancelAfterFailure?.Cancel();
            }
            return Task.CompletedTask;
        }
    }
}
