using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow("system.sender@example.org", null, "system.sender@example.org")]
    [DataRow("system.sender@example.org", "library.sender@example.org", "library.sender@example.org")]
    [DataRow(null, "library.sender@example.org", "library.sender@example.org")]
    [DataRow(null, null, null)]
    public async Task WorkflowReadinessResolvesEffectiveSenderOverridesAndMissingConfiguration(string? systemSender, string? librarySender, string? expectedSender)
    {
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        var actor = await ReadConfiguredSuperAdminAsync();
        var recipient = await CreateCorrectiveStaffAsync(actor, "staff", 2);
        var sender = new ConfigurationReadinessEmailSender(factory.Services.GetRequiredService<PatronConfigurationService>());
        var localDispatcher = new RecordingOutboxDispatcher();
        await using var scoped = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(sender);
            services.RemoveAll<IEmailOutboxDispatcher>();
            services.AddSingleton<IEmailOutboxDispatcher>(localDispatcher);
        }));
        var contextFactory = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync();
        var system = await context.EmailSettings.SingleAsync(item => item.OrganizationId == 1);
        var systemSettings = await context.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
        var library = await context.EmailSettings.SingleOrDefaultAsync(item => item.OrganizationId == 2);
        var oldSystemSender = system.FromAddress;
        var oldStaffUrl = systemSettings.StaffApplicationUrl;
        var oldLibrarySender = library?.FromAddress;
        var createdLibrary = library is null;
        library ??= new EmailSettings { OrganizationId = 2 };
        if (createdLibrary) context.EmailSettings.Add(library);
        system.FromAddress = systemSender;
        library.FromAddress = librarySender;
        systemSettings.StaffApplicationUrl = "https://staff.example.org/staff/";
        var now = timeProvider!.GetUtcNow().UtcDateTime;
        var request = new TitleRequest
        {
            LibraryOrganizationId = 2, Barcode = "20000000003930", Title = "Effective readiness boundary",
            MaterialFormatId = await context.MaterialFormats.Where(item => item.OwnerOrganizationId == 1 && item.Code == "book").Select(item => item.Id).SingleAsync(),
            Status = "suggestion", CreatedUtc = now, UpdatedUtc = now
        };
        context.TitleRequests.Add(request);
        await context.SaveChangesAsync();
        try
        {
            Assert.AreEqual(systemSender is not null, (await sender.CheckReadinessAsync(1, CancellationToken.None)).IsConfigured);
            sender.Observed.Clear();
            var result = await scoped.Services.GetRequiredService<TitleRequestMutationService>().AssignAsync(actor, request.Id,
                new AssignTitleRequestInput(StaffVersion.Encode(request.RowVersion), recipient.Id), CancellationToken.None);
            Assert.AreEqual("updated", result.Code);
            Assert.HasCount(1, sender.Observed);
            Assert.AreEqual((2, expectedSender, expectedSender is not null), sender.Observed.Single());
            var outbox = await context.EmailOutbox.AsNoTracking().SingleAsync(item => item.Id == result.DispatchOutboxIds!.Single());
            Assert.AreEqual(2, outbox.OrganizationId);
            Assert.AreEqual(2, outbox.AuthorizationOrganizationId);
            Assert.AreEqual(expectedSender, outbox.FromAddress);
            Assert.AreEqual(expectedSender is null ? "suppressed" : "pending", outbox.Status);
            Assert.AreEqual(expectedSender is null ? "sender_missing" : null, outbox.SuppressionReason);
            Assert.AreEqual(expectedSender is null ? 0 : 1, localDispatcher.EnqueuedIds.Count);
            Assert.AreEqual(recipient.Id, (await context.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id)).ClaimedByStaffUserId);
        }
        finally
        {
            system.FromAddress = oldSystemSender;
            systemSettings.StaffApplicationUrl = oldStaffUrl;
            if (createdLibrary) context.EmailSettings.Remove(library); else library.FromAddress = oldLibrarySender;
            await context.SaveChangesAsync();
        }
    }

    [TestMethod]
    [DataRow("staff", true)]
    [DataRow("admin", true)]
    [DataRow("super_admin", true)]
    [DataRow("staff", false)]
    [DataRow("admin", false)]
    [DataRow("super_admin", false)]
    public Task WorkflowEmailReadinessUsesBusinessLibraryOutsideSqlLocks(string role, bool configured) =>
        VerifyWorkflowEmailActivityAsync(role, configured, missingStaffRecipient: false, invalidStaffUrl: false);

    [TestMethod]
    public Task WorkflowEmailMissingStaffRecipientsPersistSuppressionWithRequestActivity() =>
        VerifyWorkflowEmailActivityAsync("staff", configured: true, missingStaffRecipient: true, invalidStaffUrl: false);

    [TestMethod]
    public Task WorkflowStaffTemplatesSuppressWhenTheConfiguredStaffUrlIsInvalid() =>
        VerifyWorkflowEmailActivityAsync("staff", configured: true, missingStaffRecipient: false, invalidStaffUrl: true);

    private async Task VerifyWorkflowEmailActivityAsync(
        string role,
        bool configured,
        bool missingStaffRecipient,
        bool invalidStaffUrl)
    {
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var staff = await CreateCorrectiveStaffAsync(superAdmin, role, role == "super_admin" ? 1 : 2);
        if (missingStaffRecipient)
        {
            await ExecuteNonQueryAsync("UPDATE [asap].[StaffUser] SET [NotificationEmail] = NULL WHERE [Id] = @id;", ("@id", staff.Id));
        }
        var actor = await ReadCorrectiveStaffAsync(staff);
        var sender = new ScopedReadinessEmailSender(configured);
        var deliverySender = new RecordingEmailSender();
        var provider = ScriptedHoldProvider.ReplyRequiredThenSuccess();
        var roleOrdinal = role switch { "staff" => 1, "admin" => 2, _ => 3 };
        provider.PatronId = 71050 + roleOrdinal * 10 + (configured ? 1 : 0);
        var localDispatcher = new RecordingOutboxDispatcher();
        await using var scoped = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(sender);
            services.RemoveAll<IEmailOutboxDispatcher>();
            services.AddSingleton<IEmailOutboxDispatcher>(localDispatcher);
            services.RemoveAll<IPatronProvider>();
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton<IPatronProvider>(provider);
            services.AddSingleton<IStaffPolarisProvider>(provider);
        }));
        var contextFactory = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync();
        var systemSettings = await context.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
        var originalStaffUrl = systemSettings.StaffApplicationUrl;
        EmailTemplate? mutatedStaffTemplate = null;
        string? originalStaffTemplateBody = null;
        systemSettings.StaffApplicationUrl = invalidStaffUrl
            ? "https://attacker.example.org/staff/?untrusted=1"
            : "https://staff.example.org/staff/";
        var email = await context.EmailSettings.SingleOrDefaultAsync(item => item.OrganizationId == 2);
        var createdEmail = email is null;
        email ??= new EmailSettings { OrganizationId = 2 };
        var previousSender = email.FromAddress;
        if (createdEmail) context.EmailSettings.Add(email);
        email.FromAddress = "library.sender@example.org";
        await context.SaveChangesAsync();
        var format = await context.MaterialFormats.SingleAsync(item => item.OwnerOrganizationId == 1 && item.Code == "book");
        var now = timeProvider!.GetUtcNow().UtcDateTime;
        var barcode = $"200000000021{roleOrdinal}{(configured ? 1 : 0)}";
        TitleRequest NewRequest(string status) => new()
        {
            LibraryOrganizationId = 2, MaterialFormatId = format.Id, Barcode = barcode,
            Title = status == "suggestion" ? $"Scoped mail <script> & {Guid.NewGuid():N}" : $"Scoped mail {Guid.NewGuid():N}",
            PatronIdSnapshot = provider.PatronId,
            Status = status, BibId = status == "pending_hold" ? 9001 : null,
            BibIdStaffVerified = status == "pending_hold", AutoHold = true, PreferredPickupBranchId = 101,
            PreferredPickupBranchName = "Main Library", IsbnCheckStatus = status == "pending_hold" ? "found" : "not_found", CreatedUtc = now, UpdatedUtc = now,
            ClaimedByStaffUserId = staff.Id, ClaimedByDisplayName = staff.DisplayName ?? staff.UserPrincipalName,
            ClaimedAtUtc = now, ClaimType = "manual"
        };
        var title = NewRequest("suggestion");
        var source = NewRequest("pending_hold");
        context.TitleRequests.AddRange(title, source);
        await context.SaveChangesAsync();
        var beforeId = await context.EmailOutbox.Select(item => (long?)item.Id).MaxAsync() ?? 0;
        try
        {
            if (!invalidStaffUrl)
            {
                systemSettings.StaffApplicationUrl = "https://staff.example.org/staff/";
            }
            await context.SaveChangesAsync();
            var titles = scoped.Services.GetRequiredService<TitleRequestMutationService>();
            var assigned = await titles.AssignAsync(actor, title.Id, new AssignTitleRequestInput(StaffVersion.Encode(title.RowVersion), staff.Id), CancellationToken.None);
            Assert.AreEqual("updated", assigned.Code);
            await context.Entry(title).ReloadAsync();
            var purchased = await titles.ActionAsync(actor, title.Id, new TitleRequestActionInput
            {
                Version = StaffVersion.Encode(title.RowVersion), Action = "purchase", EmailPurchaseReminder = true
            }.ToCommand(), CancellationToken.None);
            Assert.AreEqual("updated", purchased.Code);
            var copies = scoped.Services.GetRequiredService<AdditionalCopyService>();
            var created = await copies.CreateAsync(actor, source.Id, new AdditionalCopyCreateInput(StaffVersion.Encode(source.RowVersion), true), CancellationToken.None);
            Assert.AreEqual("created", created.Code);
            var copy = await context.AdditionalCopyRequests.AsNoTracking().SingleAsync(item => item.Id == created.RequestId);
            Assert.AreEqual(now, copy.CreatedUtc, "A source rowversion bump must not future-date the new copy.");
            Assert.AreEqual(now, copy.UpdatedUtc);
            Assert.AreEqual("updated", (await copies.AssignAsync(actor, copy.Id,
                new AssignAdditionalCopyInput(StaffVersion.Encode(copy.RowVersion), staff.Id), CancellationToken.None)).Code);
            await context.Entry(source).ReloadAsync();
            Assert.IsTrue(source.UpdatedUtc > now, "Creating a copy must invalidate a stale source preview.");
            var hold = await scoped.Services.GetRequiredService<HoldPlacementService>().PlaceAsync(actor, source.Id,
                new VersionInput(StaffVersion.Encode(source.RowVersion)), CancellationToken.None);
            Assert.AreEqual("updated", hold.Code);
            CollectionAssert.AreEqual(new[] { 2, 2, 2, 2, 2 }, sender.Organizations.ToArray());
            var outboxes = await context.EmailOutbox.AsNoTracking().Where(item => item.Id > beforeId).ToListAsync();
            Assert.AreEqual(6, outboxes.Count);
            Assert.IsTrue(outboxes.Any(item => item.BusinessKey != null &&
                item.BusinessKey.StartsWith($"staff-patron-action:purchase_approved:{title.Id}:", StringComparison.Ordinal)));
            foreach (var outbox in outboxes)
            {
                Assert.AreEqual(2, outbox.OrganizationId);
                if (outbox.AuthorizationOrganizationId.HasValue) Assert.AreEqual(2, outbox.AuthorizationOrganizationId);
                Assert.AreEqual("library.sender@example.org", outbox.FromAddress);
                var missingRecipient = missingStaffRecipient && outbox.DeliveryClass == "staff_authorization_sensitive";
                var templateNotice = outbox.BusinessKey!.StartsWith("title-assignment:", StringComparison.Ordinal) ||
                    outbox.BusinessKey.StartsWith("purchase-reminder:", StringComparison.Ordinal) ||
                    outbox.BusinessKey.StartsWith("additional-copy-reminder:", StringComparison.Ordinal) ||
                    outbox.BusinessKey.StartsWith("additional-copy-assignment:", StringComparison.Ordinal);
                var suppressionReason = missingRecipient ? "recipient_missing" :
                    invalidStaffUrl && templateNotice ? "staff_url_missing" :
                    configured ? null : "mail_not_configured";
                Assert.AreEqual(suppressionReason is null ? "pending" : "suppressed", outbox.Status);
                Assert.AreEqual(suppressionReason, outbox.SuppressionReason);
                var kind = outbox.BusinessKey!.Split(':')[0] switch
                {
                    "title-assignment" => "assignment",
                    "purchase-reminder" => "purchase_reminder",
                    "staff-patron-action" => "purchase_approved",
                    "additional-copy-reminder" => "additional_copy_reminder",
                    "additional-copy-assignment" => "additional_copy_assignment",
                    "title-hold-placed" => "hold_placed",
                    _ => throw new AssertFailedException("Unexpected request-mail producer.")
                };
                if (outbox.DeliveryClass == "staff_authorization_sensitive")
                {
                    Assert.IsFalse(string.IsNullOrWhiteSpace(outbox.Subject));
                    Assert.IsFalse(string.IsNullOrWhiteSpace(outbox.BodyText));
                    Assert.IsFalse(string.IsNullOrWhiteSpace(outbox.BodyHtml));
                    Assert.IsFalse(outbox.BodyHtml!.Contains("<script>", StringComparison.OrdinalIgnoreCase));
                    switch (kind)
                    {
                        case "assignment":
                            Assert.AreEqual("ASAP request assigned", outbox.Subject);
                            StringAssert.Contains(outbox.BodyText!, "Scoped mail <script> &");
                            StringAssert.Contains(outbox.BodyHtml, "Scoped mail &lt;script&gt; &amp;");
                            if (invalidStaffUrl)
                            {
                                Assert.IsFalse(outbox.BodyText!.Contains("attacker.example.org", StringComparison.Ordinal));
                            }
                            else
                            {
                                StringAssert.Contains(outbox.BodyText!, $"request={title.Id}&stage=suggestion&scope=2");
                            }
                            break;
                        case "purchase_reminder":
                            Assert.AreEqual("ASAP purchase reminder", outbox.Subject);
                            if (invalidStaffUrl)
                            {
                                Assert.IsFalse(outbox.BodyText!.Contains("attacker.example.org", StringComparison.Ordinal));
                            }
                            else
                            {
                                StringAssert.Contains(outbox.BodyText!, $"request={title.Id}&stage=outstanding_purchase&scope=2");
                            }
                            break;
                        case "additional_copy_reminder":
                        case "additional_copy_assignment":
                            var taskId = copy.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            Assert.AreEqual(kind == "additional_copy_reminder"
                                ? "ASAP additional-copy reminder" : "ASAP additional-copy task assigned", outbox.Subject);
                            StringAssert.Contains(outbox.BodyText!, $"Additional-copy task {taskId}");
                            if (invalidStaffUrl)
                            {
                                Assert.IsFalse(outbox.BodyText!.Contains("attacker.example.org", StringComparison.Ordinal));
                            }
                            else
                            {
                                StringAssert.Contains(outbox.BodyText!,
                                    $"request={taskId}&stage=additional_copies&scope=2");
                            }
                            break;
                    }
                }
                await AssertRequestMailEvidenceAsync(outbox,
                    kind is "assignment" or "purchase_reminder" or "purchase_approved" ? title.Id : source.Id, kind);
            }
            Assert.AreEqual(outboxes.Count(item => item.Status == "pending"), localDispatcher.EnqueuedIds.Count);
            if (configured && !missingStaffRecipient && !invalidStaffUrl && role == "staff")
            {
                var assignment = outboxes.Single(item => item.BusinessKey!.StartsWith("title-assignment:", StringComparison.Ordinal));
                var acceptedSnapshot = (assignment.Subject, assignment.BodyText, assignment.BodyHtml);
                mutatedStaffTemplate = await context.EmailTemplates.SingleAsync(item =>
                    item.OrganizationId == LibraryScope.SystemOrganizationId &&
                    item.TemplateKey == StaffEmailTemplateCatalog.TitleAssignment);
                originalStaffTemplateBody = mutatedStaffTemplate.BodyTemplate;
                mutatedStaffTemplate.BodyTemplate += "\nChanged after this notification was queued.";
                await context.SaveChangesAsync();
                var storedSnapshot = await context.EmailOutbox.AsNoTracking().SingleAsync(item => item.Id == assignment.Id);
                Assert.AreEqual(acceptedSnapshot.Subject, storedSnapshot.Subject);
                Assert.AreEqual(acceptedSnapshot.BodyText, storedSnapshot.BodyText);
                Assert.AreEqual(acceptedSnapshot.BodyHtml, storedSnapshot.BodyHtml);

                await CreateEmailOutboxJobs(deliverySender, EmailOutboxRuntimeOptions.Default)
                    .DeliverAsync(assignment.Id, CancellationToken.None);
                Assert.AreEqual("sent", (await ReadOutboxStateAsync(assignment.Id)).Status);
                var envelope = deliverySender.Envelopes.Single();
                Assert.AreEqual(acceptedSnapshot.Subject, envelope.Subject);
                Assert.AreEqual(acceptedSnapshot.BodyText, envelope.BodyText);
                Assert.AreEqual(acceptedSnapshot.BodyHtml, envelope.BodyHtml);
                Assert.IsFalse(envelope.BodyText!.Contains("Changed after this notification was queued.", StringComparison.Ordinal));

            }
        }
        finally
        {
            var copyIds = await context.AdditionalCopyRequests.AsNoTracking()
                .Where(item => item.SourceTitleRequestId == source.Id)
                .Select(item => item.Id).ToArrayAsync();
            var ownedPrefixes = new[]
            {
                $"title-assignment:{title.Id}:", $"purchase-reminder:{title.Id}:",
                $"staff-patron-action:purchase_approved:{title.Id}:", $"title-hold-placed:{source.Id}:"
            }.Concat(copyIds.Select(id => $"additional-copy-assignment:{id}:")).ToArray();
            var copyReminderKeys = copyIds.Select(id => $"additional-copy-reminder:{id}").ToHashSet(StringComparer.Ordinal);
            var candidateOutboxes = await context.EmailOutbox.AsNoTracking()
                .Where(item => item.OrganizationId == 2 && item.Id > beforeId).ToListAsync();
            await DeleteCommittedOutboxIdsAsync(candidateOutboxes
                .Where(item => item.BusinessKey is { } key &&
                    (ownedPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)) || copyReminderKeys.Contains(key)))
                .Select(item => item.Id));
            await context.AdditionalCopyRequests.Where(item => item.SourceTitleRequestId == source.Id).ExecuteDeleteAsync();
            await DeleteRequestAsync(title.Id);
            await DeleteRequestAsync(source.Id);
            if (mutatedStaffTemplate is not null)
            {
                mutatedStaffTemplate.BodyTemplate = originalStaffTemplateBody;
            }
            systemSettings.StaffApplicationUrl = originalStaffUrl;
            if (createdEmail) context.EmailSettings.Remove(email); else email.FromAddress = previousSender;
            await context.SaveChangesAsync();
            var current = await context.StaffUsers.AsNoTracking().SingleAsync(item => item.Id == staff.Id);
            Assert.AreEqual("updated", (await factory.Services.GetRequiredService<StaffLifecycleService>().DeactivateAsync(superAdmin,
                staff.Id, new StaffDeactivateInput(StaffVersion.Encode(current.RowVersion)), CancellationToken.None)).Code);
        }
    }

    private sealed class ConfigurationReadinessEmailSender(PatronConfigurationService configuration) : IEmailSender
    {
        public List<(int OrganizationId, string? FromAddress, bool Configured)> Observed { get; } = [];

        public async Task<EmailTransportReadiness> CheckReadinessAsync(int organizationId, CancellationToken cancellationToken)
        {
            var effective = await configuration.GetAsync(organizationId, cancellationToken);
            var fromAddress = effective?.Email.FromAddress;
            var configured = !string.IsNullOrWhiteSpace(fromAddress);
            Observed.Add((organizationId, fromAddress, configured));
            return new EmailTransportReadiness(configured);
        }

        public Task<EmailSendResult> SendAsync(EmailEnvelope envelope, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Intent creation must not perform delivery.");
    }

    private sealed class ScopedReadinessEmailSender(bool configured) : IEmailSender
    {
        public List<int> Organizations { get; } = [];

        public async Task<EmailTransportReadiness> CheckReadinessAsync(int organizationId, CancellationToken cancellationToken)
        {
            Organizations.Add(organizationId);
            // A separate SQL connection must be able to take the business lock during provider work.
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var probe = new SqlCommand("SET LOCK_TIMEOUT 1000; SELECT [Id] FROM [asap].[Organization] WITH (UPDLOCK) WHERE [Id] = @id;", connection);
            probe.Parameters.AddWithValue("@id", organizationId);
            await probe.ExecuteScalarAsync(cancellationToken);
            return new EmailTransportReadiness(organizationId == 2 && configured);
        }

        public Task<EmailSendResult> SendAsync(EmailEnvelope envelope, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Intent creation must not perform delivery.");
    }
}
