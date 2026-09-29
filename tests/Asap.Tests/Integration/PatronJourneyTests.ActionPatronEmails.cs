using Asap.Web.Features.Staff;
using Asap.Web.Features.Patron;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task StaffPurchaseAndAlreadyOwnJournalPatronEmailOnlyForApplicableOutcomes()
    {
        const int libraryId = 99080;
        var actor = await ReadConfiguredSuperAdminAsync();
        var patronProvider = new ActionPatronEmailProvider();
        using var scoped = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.AddSingleton<IPatronProvider>(patronProvider);
        }));
        await using var scope = scoped.Services.CreateAsyncScope();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var seed = await contexts.CreateDbContextAsync();
        var formatId = await seed.MaterialFormats.Where(item => item.Code == "book")
            .Select(item => item.Id).FirstAsync();
        seed.Organizations.Add(new Organization
        {
            Id = libraryId, DisplayName = "Action email test library", Abbreviation = "AET", IsActive = true
        });
        await seed.SaveChangesAsync();
        seed.EmailSettings.Add(new EmailSettings
        {
            OrganizationId = libraryId, FromAddress = "library@example.org", FromName = "Library"
        });
        await seed.SaveChangesAsync();

        var createdPurchaseSource = false;
        var createdOwnedSource = false;
        EmailTemplate? purchaseSource = null;
        EmailTemplate? ownedSource = null;
        try
        {

        purchaseSource = await seed.EmailTemplates.SingleOrDefaultAsync(item =>
            item.OrganizationId == 1 && item.TemplateKey == "purchase_approved");
        if (purchaseSource is null)
        {
            createdPurchaseSource = true;
            purchaseSource = new EmailTemplate
            {
                OrganizationId = 1, TemplateKey = "purchase_approved", DisplayName = "Purchase approved",
                SubjectTemplate = "Purchase approved: {{title}}", BodyTemplate = "Hello {{name}}",
                SortOrder = 20
            };
            seed.EmailTemplates.Add(purchaseSource);
        }
        ownedSource = await seed.EmailTemplates.SingleOrDefaultAsync(item =>
            item.OrganizationId == 1 && item.TemplateKey == "already_owned");
        if (ownedSource is null)
        {
            createdOwnedSource = true;
            ownedSource = new EmailTemplate
            {
                OrganizationId = 1, TemplateKey = "already_owned", DisplayName = "Already owned",
                SubjectTemplate = "Already owned: {{title}}", BodyTemplate = "Hello {{name}}",
                SortOrder = 30
            };
            seed.EmailTemplates.Add(ownedSource);
        }
        await seed.SaveChangesAsync();
        seed.EmailTemplates.AddRange(
            new EmailTemplate
            {
                OrganizationId = libraryId, TemplateKey = "purchase_approved",
                SourceTemplateId = purchaseSource.Id,
                SubjectTemplate = "Library purchase: {{title}}", BodyTemplate = "Hello {{firstName}}, {{title}} approved"
            },
            new EmailTemplate
            {
                OrganizationId = libraryId, TemplateKey = "already_owned",
                SourceTemplateId = ownedSource.Id,
                SubjectTemplate = "Library owns: {{title}}", BodyTemplate = "Hello {{firstName}}, {{title}} exists"
            });

        TitleRequest Request(string title, string? bib, bool autoHold, string? email = "patron@example.org") => new()
        {
            LibraryOrganizationId = libraryId,
            Barcode = $"2{Guid.NewGuid():N}"[..14],
            Email = email,
            NameFirst = "Pat",
            Title = title,
            MaterialFormatId = formatId,
            Status = "suggestion",
            BibId = bib,
            BibIdStaffVerified = bib is not null,
            AutoHold = autoHold,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        var purchase = Request("Purchase title", null, true);
        var purchaseWithBib = Request("Pending title", "9001", true);
        var alreadyOwned = Request("Owned title", "9001", false);
        var missingRecipient = Request("Owned without email", "9001", false, null);
        var unavailableRecipient = Request("Owned refresh unavailable", "9001", false);
        var rejected = Request("Rejected title", null, true);
        var rejectedUnavailable = Request("Rejected refresh unavailable", null, true);
        var cancelledPurchase = Request("Cancelled purchase", null, true);
        patronProvider.MissingEmailBarcode = missingRecipient.Barcode;
        patronProvider.UnavailableBarcode = unavailableRecipient.Barcode;
        patronProvider.UnavailableBarcodes.Add(rejectedUnavailable.Barcode);
        seed.TitleRequests.AddRange(purchase, purchaseWithBib, alreadyOwned, missingRecipient,
            unavailableRecipient, rejected, rejectedUnavailable, cancelledPurchase);
        await seed.SaveChangesAsync();

        var mutations = scope.ServiceProvider.GetRequiredService<TitleRequestMutationService>();
        var purchaseResult = await mutations.ActionAsync(actor, purchase.Id,
            new TitleRequestActionInput
            {
                Version = Convert.ToBase64String(purchase.RowVersion), Action = "purchase"
            }, CancellationToken.None);
        Assert.AreEqual("updated", purchaseResult.Code);
        Assert.AreEqual("not_requested", purchaseResult.NotificationStatus);
        Assert.AreEqual("queued", purchaseResult.PatronNotificationStatus);
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, purchaseWithBib.Id,
            new TitleRequestActionInput
            {
                Version = Convert.ToBase64String(purchaseWithBib.RowVersion), Action = "purchase"
            }, CancellationToken.None)).Code);
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, alreadyOwned.Id,
            new TitleRequestActionInput
            {
                Version = Convert.ToBase64String(alreadyOwned.RowVersion), Action = "alreadyOwn"
            }, CancellationToken.None)).Code);
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, missingRecipient.Id,
            new TitleRequestActionInput
            {
                Version = Convert.ToBase64String(missingRecipient.RowVersion), Action = "alreadyOwn"
            }, CancellationToken.None)).Code);
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, unavailableRecipient.Id,
            new TitleRequestActionInput
            {
                Version = Convert.ToBase64String(unavailableRecipient.RowVersion), Action = "alreadyOwn"
            }, CancellationToken.None)).Code);
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, rejected.Id,
            new TitleRequestActionInput
            {
                Version = Convert.ToBase64String(rejected.RowVersion), Action = "reject"
            }, CancellationToken.None)).Code);
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, rejectedUnavailable.Id,
            new TitleRequestActionInput
            {
                Version = Convert.ToBase64String(rejectedUnavailable.RowVersion), Action = "reject"
            }, CancellationToken.None)).Code);
        using var cancellation = new CancellationTokenSource();
        patronProvider.BeforeRefresh = _ => cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await mutations.ActionAsync(actor, cancelledPurchase.Id,
                new TitleRequestActionInput
                {
                    Version = Convert.ToBase64String(cancelledPurchase.RowVersion), Action = "purchase"
                }, cancellation.Token));
        patronProvider.BeforeRefresh = null;

        await using var verify = await contexts.CreateDbContextAsync();
        var approved = await verify.EmailOutbox.AsNoTracking().SingleAsync(item =>
            item.BusinessKey != null && item.BusinessKey.StartsWith($"staff-patron-action:purchase_approved:{purchase.Id}:"));
        Assert.AreEqual("business_event", approved.DeliveryClass);
        Assert.AreEqual("pending", approved.Status);
        CollectionAssert.Contains(purchaseResult.DispatchOutboxIds!.ToArray(), approved.Id);
        Assert.AreEqual("current-patron@example.org", approved.ToAddress);
        Assert.AreEqual("Library purchase: Purchase title", approved.Subject);
        Assert.AreEqual("Hello Current, Purchase title approved", approved.BodyText);
        Assert.AreEqual("outstanding_purchase", (await verify.TitleRequests.AsNoTracking()
            .SingleAsync(item => item.Id == purchase.Id)).Status);
        Assert.AreEqual("pending_hold", (await verify.TitleRequests.AsNoTracking()
            .SingleAsync(item => item.Id == purchaseWithBib.Id)).Status);
        Assert.IsFalse(await verify.EmailOutbox.AnyAsync(item => item.BusinessKey != null &&
            item.BusinessKey.StartsWith($"staff-patron-action:purchase_approved:{purchaseWithBib.Id}:")));
        var owned = await verify.EmailOutbox.AsNoTracking().SingleAsync(item =>
            item.BusinessKey != null && item.BusinessKey.StartsWith($"staff-patron-action:already_owned:{alreadyOwned.Id}:"));
        Assert.AreEqual("Library owns: Owned title", owned.Subject);
        Assert.AreEqual("Hello Current, Owned title exists", owned.BodyText);
        Assert.AreEqual("closed", (await verify.TitleRequests.AsNoTracking()
            .SingleAsync(item => item.Id == alreadyOwned.Id)).Status);
        var suppressed = await verify.EmailOutbox.AsNoTracking().SingleAsync(item =>
            item.BusinessKey != null && item.BusinessKey.StartsWith($"staff-patron-action:already_owned:{missingRecipient.Id}:"));
        Assert.AreEqual("suppressed", suppressed.Status);
        Assert.AreEqual("recipient_invalid", suppressed.SuppressionReason);
        var unavailable = await verify.EmailOutbox.AsNoTracking().SingleAsync(item =>
            item.BusinessKey != null && item.BusinessKey.StartsWith($"staff-patron-action:already_owned:{unavailableRecipient.Id}:"));
        Assert.AreEqual("suppressed", unavailable.Status);
        Assert.AreEqual("patron_refresh_unavailable", unavailable.SuppressionReason);
        Assert.IsNull(unavailable.ToAddress);
        var rejection = await verify.EmailOutbox.AsNoTracking().SingleAsync(item =>
            item.BusinessKey != null && item.BusinessKey.StartsWith($"rejection:{rejected.Id}:"));
        Assert.AreEqual("current-patron@example.org", rejection.ToAddress);
        var suppressedRejection = await verify.EmailOutbox.AsNoTracking().SingleAsync(item =>
            item.BusinessKey != null && item.BusinessKey.StartsWith($"rejection:{rejectedUnavailable.Id}:"));
        Assert.AreEqual("suppressed", suppressedRejection.Status);
        Assert.AreEqual("patron_refresh_unavailable", suppressedRejection.SuppressionReason);
        Assert.IsNull(suppressedRejection.ToAddress);
        Assert.AreEqual("suggestion", (await verify.TitleRequests.AsNoTracking()
            .SingleAsync(item => item.Id == cancelledPurchase.Id)).Status);
        Assert.IsFalse(await verify.EmailOutbox.AnyAsync(item => item.BusinessKey != null &&
            item.BusinessKey.Contains($":{cancelledPurchase.Id}:")));
        }
        finally
        {
            await using var cleanup = await contexts.CreateDbContextAsync();
            await cleanup.EmailOutbox.Where(item => item.OrganizationId == libraryId).ExecuteDeleteAsync();
            var requestIds = await cleanup.TitleRequests.Where(item => item.LibraryOrganizationId == libraryId)
                .Select(item => item.Id).ToArrayAsync();
            await cleanup.TitleRequestWorkflowTags.Where(item => requestIds.Contains(item.TitleRequestId))
                .ExecuteDeleteAsync();
            await cleanup.TitleRequestEvents.Where(item => requestIds.Contains(item.TitleRequestId))
                .ExecuteDeleteAsync();
            await cleanup.TitleRequests.Where(item => item.LibraryOrganizationId == libraryId).ExecuteDeleteAsync();
            await cleanup.EmailTemplates.Where(item => item.OrganizationId == libraryId).ExecuteDeleteAsync();
            await cleanup.EmailSettings.Where(item => item.OrganizationId == libraryId).ExecuteDeleteAsync();
            await cleanup.Organizations.Where(item => item.Id == libraryId).ExecuteDeleteAsync();
            if (createdPurchaseSource)
            {
                await cleanup.EmailTemplates.Where(item => item.Id == purchaseSource!.Id).ExecuteDeleteAsync();
            }
            if (createdOwnedSource)
            {
                await cleanup.EmailTemplates.Where(item => item.Id == ownedSource!.Id).ExecuteDeleteAsync();
            }
        }
    }

    private sealed class ActionPatronEmailProvider : IPatronProvider
    {
        public string? MissingEmailBarcode { get; set; }
        public string? UnavailableBarcode { get; set; }
        public HashSet<string> UnavailableBarcodes { get; } = [];
        public Action<CancellationToken>? BeforeRefresh { get; set; }

        public Task<PatronSnapshot> RefreshAsync(string barcode, CancellationToken cancellationToken)
        {
            BeforeRefresh?.Invoke(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (barcode == UnavailableBarcode || UnavailableBarcodes.Contains(barcode))
            {
                throw new PolarisOperationalException("polaris_patron_refresh_failed", "Patron lookup unavailable.");
            }
            return Task.FromResult(new PatronSnapshot(7001, barcode,
                barcode == MissingEmailBarcode ? null : "current-patron@example.org",
                "Current", "Patron", "1", "Adult", 101, 2, "Test Library", 101));
        }

        public Task<PatronSnapshot> AuthenticateAsync(string barcode, string pin,
            CancellationToken cancellationToken) => RefreshAsync(barcode, cancellationToken);

        public Task<IReadOnlyList<PickupBranch>> GetPickupBranchesAsync(PatronSnapshot patron,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task UpdatePreferredPickupBranchAsync(string barcode, int pickupBranchId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IdentifierLookupResult> LookupIdentifierAsync(string identifier,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
