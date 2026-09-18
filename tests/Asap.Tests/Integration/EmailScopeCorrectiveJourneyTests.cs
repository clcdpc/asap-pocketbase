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
        var library = await context.EmailSettings.SingleOrDefaultAsync(item => item.OrganizationId == 2);
        var oldSystemSender = system.FromAddress;
        var oldLibrarySender = library?.FromAddress;
        var createdLibrary = library is null;
        library ??= new EmailSettings { OrganizationId = 2 };
        if (createdLibrary) context.EmailSettings.Add(library);
        system.FromAddress = systemSender;
        library.FromAddress = librarySender;
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
    public async Task WorkflowEmailReadinessUsesBusinessLibraryOutsideSqlLocks(string role, bool configured)
    {
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var staff = await CreateCorrectiveStaffAsync(superAdmin, role, role == "super_admin" ? 1 : 2);
        var actor = await ReadCorrectiveStaffAsync(staff);
        var sender = new ScopedReadinessEmailSender(configured);
        var provider = ScriptedHoldProvider.ReplyRequiredThenSuccess();
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
        var email = await context.EmailSettings.SingleOrDefaultAsync(item => item.OrganizationId == 2);
        var createdEmail = email is null;
        email ??= new EmailSettings { OrganizationId = 2 };
        var previousSender = email.FromAddress;
        if (createdEmail) context.EmailSettings.Add(email);
        email.FromAddress = "library.sender@example.org";
        await context.SaveChangesAsync();
        var format = await context.MaterialFormats.SingleAsync(item => item.OwnerOrganizationId == 1 && item.Code == "book");
        var now = timeProvider!.GetUtcNow().UtcDateTime;
        TitleRequest NewRequest(string status) => new()
        {
            LibraryOrganizationId = 2, MaterialFormatId = format.Id, Barcode = "20000000002105", Title = $"Scoped mail {Guid.NewGuid():N}",
            Status = status, BibId = status == "pending_hold" ? "9001" : null, AutoHold = true, PreferredPickupBranchId = 101,
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
            var titles = scoped.Services.GetRequiredService<TitleRequestMutationService>();
            var assigned = await titles.AssignAsync(actor, title.Id, new AssignTitleRequestInput(StaffVersion.Encode(title.RowVersion), staff.Id), CancellationToken.None);
            Assert.AreEqual("updated", assigned.Code);
            await context.Entry(title).ReloadAsync();
            var purchased = await titles.ActionAsync(actor, title.Id, new TitleRequestActionInput
            {
                Version = StaffVersion.Encode(title.RowVersion), Action = "purchase", EmailPurchaseReminder = true
            }, CancellationToken.None);
            Assert.AreEqual("updated", purchased.Code);
            var copies = scoped.Services.GetRequiredService<AdditionalCopyService>();
            var created = await copies.CreateAsync(actor, source.Id, new AdditionalCopyCreateInput(StaffVersion.Encode(source.RowVersion), true), CancellationToken.None);
            Assert.AreEqual("created", created.Code);
            var copy = await context.AdditionalCopyRequests.AsNoTracking().SingleAsync(item => item.Id == created.RequestId);
            Assert.AreEqual("updated", (await copies.AssignAsync(actor, copy.Id,
                new AssignAdditionalCopyInput(StaffVersion.Encode(copy.RowVersion), staff.Id), CancellationToken.None)).Code);
            await context.Entry(source).ReloadAsync();
            var hold = await scoped.Services.GetRequiredService<HoldPlacementService>().PlaceAsync(actor, source.Id,
                new VersionInput(StaffVersion.Encode(source.RowVersion)), CancellationToken.None);
            Assert.AreEqual("updated", hold.Code);
            CollectionAssert.AreEqual(new[] { 2, 2, 2, 2, 2 }, sender.Organizations.ToArray());
            var outboxes = await context.EmailOutbox.AsNoTracking().Where(item => item.Id > beforeId).ToListAsync();
            Assert.AreEqual(5, outboxes.Count);
            foreach (var outbox in outboxes)
            {
                Assert.AreEqual(2, outbox.OrganizationId);
                if (outbox.AuthorizationOrganizationId.HasValue) Assert.AreEqual(2, outbox.AuthorizationOrganizationId);
                Assert.AreEqual("library.sender@example.org", outbox.FromAddress);
                Assert.AreEqual(configured ? "pending" : "suppressed", outbox.Status);
                Assert.AreEqual(configured ? null : "mail_not_configured", outbox.SuppressionReason);
            }
            Assert.AreEqual(configured ? 5 : 0, localDispatcher.EnqueuedIds.Count);
        }
        finally
        {
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
