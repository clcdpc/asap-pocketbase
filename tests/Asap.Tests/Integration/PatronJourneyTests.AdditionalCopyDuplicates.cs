using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Asap.Web.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task StaffActionEmailsUseSelectedTemplateAndCommittedHistory()
    {
        const int libraryId = 99041;
        var actor = await ReadConfiguredSuperAdminAsync();
        var provider = ScriptedHoldProvider.ReplyRequiredThenSuccess();
        provider.Email = "current-patron@example.org";
        await using var scoped = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.AddSingleton<IPatronProvider>(provider);
        }));
        await using var scope = scoped.Services.CreateAsyncScope();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var seed = await contexts.CreateDbContextAsync();
        var formatId = await seed.MaterialFormats.Where(item => item.Code == "book")
            .Select(item => item.Id).FirstAsync();
        seed.Organizations.Add(new Organization
        {
            Id = libraryId, DisplayName = "Action email library", Abbreviation = "AEL", IsActive = true
        });
        await seed.SaveChangesAsync();
        seed.EmailSettings.Add(new EmailSettings
        {
            OrganizationId = libraryId, FromAddress = "library@example.org", FromName = "Library"
        });
        var rejection = new EmailTemplate
        {
            OrganizationId = libraryId, TemplateKey = "rejection:staff_action_test",
            DisplayName = "Selected rejection", SubjectTemplate = "Selected: {{title}}",
            BodyTemplate = "Hello {{firstName}}, {{title}} was declined.", IsCustom = true
        };
        seed.EmailTemplates.Add(rejection);
        var rejected = new TitleRequest
        {
            LibraryOrganizationId = libraryId, Barcode = $"2{Guid.NewGuid():N}"[..14],
            Email = "old-patron@example.org", NameFirst = "Pat", Title = "Declined title",
            MaterialFormatId = formatId, Status = "suggestion", AutoHold = true,
            Notes = "Draft comment", CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow
        };
        var purchased = new TitleRequest
        {
            LibraryOrganizationId = libraryId, Barcode = $"2{Guid.NewGuid():N}"[..14],
            Email = "old-patron@example.org", NameFirst = "Pat", Title = "Approved title",
            MaterialFormatId = formatId, Status = "suggestion", AutoHold = true,
            CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow
        };
        var alreadyOwned = new TitleRequest
        {
            LibraryOrganizationId = libraryId, Barcode = $"2{Guid.NewGuid():N}"[..14],
            Email = "old-patron@example.org", NameFirst = "Pat", Title = "Owned title",
            MaterialFormatId = formatId, Status = "suggestion", AutoHold = true,
            CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow
        };
        seed.TitleRequests.AddRange(rejected, purchased, alreadyOwned);
        await seed.SaveChangesAsync();

        var mutations = scope.ServiceProvider.GetRequiredService<TitleRequestMutationService>();
        var invalid = await mutations.ActionAsync(actor, rejected.Id, new TitleRequestActionInput
        {
            Version = Convert.ToBase64String(rejected.RowVersion), Action = "reject",
            RejectionTemplateId = "999999999"
        }, CancellationToken.None);
        Assert.AreEqual("invalid_rejection_template", invalid.Code);
        await using (var unchanged = await contexts.CreateDbContextAsync())
        {
            var row = await unchanged.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == rejected.Id);
            Assert.AreEqual("suggestion", row.Status);
            Assert.IsTrue(row.RowVersion.SequenceEqual(rejected.RowVersion));
            Assert.IsFalse(await unchanged.EmailOutbox.AnyAsync(item =>
                item.BusinessKey != null && item.BusinessKey.Contains($":{rejected.Id}:")));
        }

        var rejectedResult = await mutations.ActionAsync(actor, rejected.Id, new TitleRequestActionInput
        {
            Version = Convert.ToBase64String(rejected.RowVersion), Action = "reject",
            RejectionTemplateId = rejection.Id.ToString()
        }, CancellationToken.None);
        Assert.AreEqual("updated", rejectedResult.Code);
        var purchaseResult = await mutations.ActionAsync(actor, purchased.Id, new TitleRequestActionInput
        {
            Version = Convert.ToBase64String(purchased.RowVersion), Action = "purchase",
            EmailPurchaseReminder = true
        }, CancellationToken.None);
        Assert.AreEqual("updated", purchaseResult.Code);
        Assert.IsTrue(purchaseResult.ReminderRequested);
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, alreadyOwned.Id,
            new TitleRequestActionInput
            {
                Version = Convert.ToBase64String(alreadyOwned.RowVersion), Action = "alreadyOwn",
                Bibid = JsonSerializer.SerializeToElement("9001")
            }, CancellationToken.None)).Code);
        await using var verify = await contexts.CreateDbContextAsync();
        var email = await verify.EmailOutbox.AsNoTracking().SingleAsync(item =>
            item.BusinessKey != null && item.BusinessKey.StartsWith($"staff-patron-action:reject:{rejected.Id}:"));
        Assert.AreEqual("Selected: Declined title", email.Subject);
        StringAssert.Contains(email.BodyText!, "Hello Hold, Declined title was declined.");
        Assert.AreEqual("current-patron@example.org", email.ToAddress);
        Assert.AreEqual("business_event", email.DeliveryClass);
        Assert.IsTrue(await verify.EmailOutbox.AnyAsync(item =>
            item.BusinessKey != null && item.BusinessKey.StartsWith($"staff-patron-action:purchase:{purchased.Id}:")));
        Assert.IsTrue(await verify.EmailOutbox.AnyAsync(item =>
            item.BusinessKey != null && item.BusinessKey.StartsWith($"staff-patron-action:alreadyOwn:{alreadyOwned.Id}:")));
        Assert.IsFalse(await verify.EmailOutbox.AnyAsync(item =>
            item.ToAddress == "old-patron@example.org"));
        var views = scope.ServiceProvider.GetRequiredService<TitleRequestViewService>();
        var rowDto = await views.GetAsync(actor, rejected.Id.ToString(), CancellationToken.None);
        Assert.IsNotNull(rowDto);
        Assert.AreEqual("Draft comment", rowDto.Notes);
        Assert.IsTrue(rowDto.Activity.Any(item => item.EventType == "status_changed" && item.Message.Contains("closed")));

        var immediate = new TitleRequest
        {
            LibraryOrganizationId = libraryId, Barcode = $"2{Guid.NewGuid():N}"[..14],
            Email = "old-patron@example.org", Title = "Immediate hold purchase",
            MaterialFormatId = formatId, Status = "suggestion", AutoHold = true,
            CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow
        };
        verify.TitleRequests.Add(immediate);
        await verify.SaveChangesAsync();
        var immediateResult = await mutations.ActionAsync(actor, immediate.Id,
            new TitleRequestActionInput
            {
                Version = Convert.ToBase64String(immediate.RowVersion), Action = "purchase",
                Bibid = JsonSerializer.SerializeToElement("9001"), EmailPurchaseReminder = true
            }, CancellationToken.None);
        Assert.AreEqual("updated", immediateResult.Code);
        Assert.IsTrue(immediateResult.ReminderRequested);
        Assert.IsFalse(immediateResult.ReminderQueued);
        Assert.AreEqual("skipped_purchase_queue", immediateResult.ReminderSkippedReason);
        await using var afterImmediate = await contexts.CreateDbContextAsync();
        Assert.IsFalse(await afterImmediate.EmailOutbox.AnyAsync(item =>
            item.BusinessKey != null && item.BusinessKey.StartsWith($"purchase-reminder:{immediate.Id}:")));

        var unavailable = new TitleRequest
        {
            LibraryOrganizationId = libraryId, Barcode = $"2{Guid.NewGuid():N}"[..14],
            Email = "old-patron@example.org", Title = "Unavailable patron",
            MaterialFormatId = formatId, Status = "suggestion", AutoHold = true,
            CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow
        };
        verify.TitleRequests.Add(unavailable);
        await verify.SaveChangesAsync();
        provider.RefreshFailure = true;
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, unavailable.Id,
            new TitleRequestActionInput
            {
                Version = Convert.ToBase64String(unavailable.RowVersion), Action = "reject"
            }, CancellationToken.None)).Code);
        await using var afterUnavailable = await contexts.CreateDbContextAsync();
        var suppressed = await afterUnavailable.EmailOutbox.AsNoTracking().SingleAsync(item =>
            item.BusinessKey != null && item.BusinessKey.StartsWith($"staff-patron-action:reject:{unavailable.Id}:"));
        Assert.AreEqual("suppressed", suppressed.Status);
        Assert.AreEqual("patron_refresh_unavailable", suppressed.SuppressionReason);
        Assert.IsNull(suppressed.ToAddress);
    }

    [TestMethod]
    public async Task StaffActionsPreserveAutomaticClaimAndReapplyChangedFormatRule()
    {
        const int libraryId = 99042;
        var actor = await ReadConfiguredSuperAdminAsync();
        await using var scope = factory!.Services.CreateAsyncScope();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var seed = await contexts.CreateDbContextAsync();
        var bookId = await seed.MaterialFormats.Where(item => item.Code == "book").Select(item => item.Id).FirstAsync();
        var dvdId = await seed.MaterialFormats.Where(item => item.Code == "dvd").Select(item => item.Id).FirstAsync();
        seed.Organizations.Add(new Organization
        {
            Id = libraryId, DisplayName = "Claim action library", Abbreviation = "CAL", IsActive = true
        });
        var assignee = new StaffUser
        {
            UserPrincipalName = "claim.action@example.org",
            NormalizedUserPrincipalName = "CLAIM.ACTION@EXAMPLE.ORG",
            Role = "staff", OrganizationId = libraryId, IsActive = true
        };
        seed.StaffUsers.Add(assignee);
        await seed.SaveChangesAsync();
        var bookRule = new FormatAutoClaimRule
        {
            LibraryOrganizationId = libraryId, MaterialFormatId = bookId,
            StaffUserId = actor.Id, IsActive = true, CreatedUtc = DateTime.UtcNow
        };
        var dvdRule = new FormatAutoClaimRule
        {
            LibraryOrganizationId = libraryId, MaterialFormatId = dvdId,
            StaffUserId = assignee.Id, IsActive = true, CreatedUtc = DateTime.UtcNow
        };
        seed.FormatAutoClaimRules.AddRange(bookRule, dvdRule);
        await seed.SaveChangesAsync();
        var request = new TitleRequest
        {
            LibraryOrganizationId = libraryId, Barcode = $"2{Guid.NewGuid():N}"[..14],
            Title = "Auto claim", MaterialFormatId = bookId, Status = "suggestion", AutoHold = true,
            ClaimedByStaffUserId = actor.Id, ClaimedByDisplayName = "Actor",
            ClaimedAtUtc = DateTime.UtcNow, ClaimType = "automatic_format_rule", ClaimRuleId = bookRule.Id,
            CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow
        };
        seed.TitleRequests.Add(request);
        await seed.SaveChangesAsync();
        var mutations = scope.ServiceProvider.GetRequiredService<TitleRequestMutationService>();
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, request.Id, new TitleRequestActionInput
        {
            Version = Convert.ToBase64String(request.RowVersion), Action = "edit", Title = "Auto claim edited"
        }, CancellationToken.None)).Code);
        await using var afterTitle = await contexts.CreateDbContextAsync();
        var preserved = await afterTitle.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id);
        Assert.AreEqual("automatic_format_rule", preserved.ClaimType);
        Assert.AreEqual(bookRule.Id, preserved.ClaimRuleId);
        Assert.AreEqual(actor.Id, preserved.ClaimedByStaffUserId);
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, request.Id, new TitleRequestActionInput
        {
            Version = Convert.ToBase64String(preserved.RowVersion), Action = "edit", Format = "dvd"
        }, CancellationToken.None)).Code);
        await using var afterFormat = await contexts.CreateDbContextAsync();
        var reassigned = await afterFormat.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id);
        Assert.AreEqual("automatic_format_rule", reassigned.ClaimType);
        Assert.AreEqual(dvdRule.Id, reassigned.ClaimRuleId);
        Assert.AreEqual(assignee.Id, reassigned.ClaimedByStaffUserId);
        Assert.IsTrue(await afterFormat.TitleRequestEvents.AnyAsync(item =>
            item.TitleRequestId == request.Id && item.EventType == "claim_auto_reassigned"));

        var noRule = new TitleRequest
        {
            LibraryOrganizationId = libraryId, Barcode = $"2{Guid.NewGuid():N}"[..14],
            Title = "No matching rule", MaterialFormatId = bookId, Status = "suggestion", AutoHold = true,
            ClaimedByStaffUserId = actor.Id, ClaimedByDisplayName = "Actor",
            ClaimedAtUtc = DateTime.UtcNow, ClaimType = "automatic_format_rule", ClaimRuleId = bookRule.Id,
            CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow
        };
        afterFormat.TitleRequests.Add(noRule);
        await afterFormat.SaveChangesAsync();
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, noRule.Id, new TitleRequestActionInput
        {
            Version = Convert.ToBase64String(noRule.RowVersion), Action = "edit", Format = "music_cd"
        }, CancellationToken.None)).Code);
        await using var afterNoRule = await contexts.CreateDbContextAsync();
        var cleared = await afterNoRule.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == noRule.Id);
        Assert.IsNull(cleared.ClaimedByStaffUserId);
        Assert.IsNull(cleared.ClaimType);
        Assert.IsTrue(await afterNoRule.TitleRequestEvents.AnyAsync(item =>
            item.TitleRequestId == noRule.Id && item.EventType == "claim_auto_cleared"));

        var transferred = await afterNoRule.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id);
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, request.Id, new TitleRequestActionInput
        {
            Version = Convert.ToBase64String(transferred.RowVersion), Action = "edit", Format = "book"
        }, CancellationToken.None)).Code);
        await using var afterTransfer = await contexts.CreateDbContextAsync();
        var manuallyTransferred = await afterTransfer.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id);
        Assert.AreEqual("manual", manuallyTransferred.ClaimType);
        Assert.AreEqual(actor.Id, manuallyTransferred.ClaimedByStaffUserId);
        Assert.IsTrue(await afterTransfer.TitleRequestEvents.AnyAsync(item =>
            item.TitleRequestId == request.Id && item.EventType == "claim_manual_transferred"));
    }

    [TestMethod]
    public async Task StaffEditCustomFieldsPreservesCanonicalValuesAndHistoricalFields()
    {
        const int libraryId = 99040;
        var actor = await ReadConfiguredSuperAdminAsync();
        await using var scope = factory!.Services.CreateAsyncScope();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var seed = await contexts.CreateDbContextAsync();
        var formatId = await seed.MaterialFormats.Where(item => item.Code == "book")
            .Select(item => item.Id).FirstAsync();
        seed.Organizations.Add(new Organization
        {
            Id = libraryId, DisplayName = "Staff custom field library", Abbreviation = "SCF", IsActive = true
        });
        var field = new PatronCustomField
        {
            LibraryOrganizationId = libraryId, FieldKey = "audience_note", FieldType = "text",
            Label = "Audience note", IsEnabled = true, SortOrder = 1
        };
        seed.PatronCustomFields.Add(field);
        await seed.SaveChangesAsync();
        seed.MaterialFormatCustomFieldRules.Add(new MaterialFormatCustomFieldRule
        {
            LibraryOrganizationId = libraryId, MaterialFormatId = formatId,
            PatronCustomFieldId = field.Id, Mode = "optional"
        });
        var request = new TitleRequest
        {
            LibraryOrganizationId = libraryId, Barcode = $"2{Guid.NewGuid():N}"[..14],
            Title = "Custom field round trip", MaterialFormatId = formatId,
            Status = "suggestion", AutoHold = true,
            CustomFieldsJson = """
                {"audience_note":{"label":"Audience note","type":"text","value":"Original"},
                 "legacy_note":{"label":"Legacy note","type":"text","value":"Keep"}}
                """,
            CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow
        };
        seed.TitleRequests.Add(request);
        await seed.SaveChangesAsync();

        var mutations = scope.ServiceProvider.GetRequiredService<TitleRequestMutationService>();
        foreach (var value in new[] { "Original", "Updated", "" })
        {
            await using var before = await contexts.CreateDbContextAsync();
            var version = Convert.ToBase64String((await before.TitleRequests.AsNoTracking()
                .SingleAsync(item => item.Id == request.Id)).RowVersion);
            var result = await mutations.ActionAsync(actor, request.Id, new TitleRequestActionInput
            {
                Version = version, Action = "edit", Title = "Custom field round trip",
                CustomFields = JsonSerializer.SerializeToElement(new { audience_note = value })
            }, CancellationToken.None);
            Assert.AreEqual("updated", result.Code);

            await using var verify = await contexts.CreateDbContextAsync();
            var current = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id);
            using var customFields = JsonDocument.Parse(current.CustomFieldsJson!);
            Assert.AreEqual("Keep", customFields.RootElement.GetProperty("legacy_note")
                .GetProperty("value").GetString());
            if (value.Length == 0)
            {
                Assert.IsFalse(customFields.RootElement.TryGetProperty("audience_note", out _));
            }
            else
            {
                Assert.AreEqual(value, customFields.RootElement.GetProperty("audience_note")
                    .GetProperty("value").GetString());
            }
        }
    }

    [TestMethod]
    public async Task StaffEditRejectsOmittedRequiredCustomField()
    {
        const int libraryId = 99043;
        var actor = await ReadConfiguredSuperAdminAsync();
        await using var scope = factory!.Services.CreateAsyncScope();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var seed = await contexts.CreateDbContextAsync();
        var formatId = await seed.MaterialFormats.Where(item => item.Code == "book")
            .Select(item => item.Id).FirstAsync();
        seed.Organizations.Add(new Organization
        {
            Id = libraryId, DisplayName = "Required field library", Abbreviation = "RFL", IsActive = true
        });
        var field = new PatronCustomField
        {
            LibraryOrganizationId = libraryId, FieldKey = "audience_note", FieldType = "text",
            Label = "Audience note", IsEnabled = true, SortOrder = 1
        };
        seed.PatronCustomFields.Add(field);
        await seed.SaveChangesAsync();
        seed.MaterialFormatCustomFieldRules.Add(new MaterialFormatCustomFieldRule
        {
            LibraryOrganizationId = libraryId, MaterialFormatId = formatId,
            PatronCustomFieldId = field.Id, Mode = "required"
        });
        var request = new TitleRequest
        {
            LibraryOrganizationId = libraryId, Barcode = $"2{Guid.NewGuid():N}"[..14],
            Title = "Required field", MaterialFormatId = formatId, Status = "suggestion", AutoHold = true,
            CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow
        };
        seed.TitleRequests.Add(request);
        await seed.SaveChangesAsync();
        var mutation = scope.ServiceProvider.GetRequiredService<TitleRequestMutationService>();
        Assert.AreEqual("title_required", (await mutation.ActionAsync(actor, request.Id,
            new TitleRequestActionInput
            {
                Version = Convert.ToBase64String(request.RowVersion), Action = "edit", Title = " "
            }, CancellationToken.None)).Code);
        var result = await mutation.ActionAsync(actor, request.Id, new TitleRequestActionInput
        {
            Version = Convert.ToBase64String(request.RowVersion), Action = "edit",
            CustomFields = JsonSerializer.SerializeToElement(new { })
        }, CancellationToken.None);
        Assert.AreEqual("invalid_custom_fields", result.Code);
        await using var verify = await contexts.CreateDbContextAsync();
        var unchanged = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id);
        Assert.IsTrue(unchanged.RowVersion.SequenceEqual(request.RowVersion));
    }

    [TestMethod]
    public async Task StaffEditClearsFoundBibAndItsVerificationState()
    {
        const int libraryId = 99044;
        var actor = await ReadConfiguredSuperAdminAsync();
        await using var scope = factory!.Services.CreateAsyncScope();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var seed = await contexts.CreateDbContextAsync();
        var formatId = await seed.MaterialFormats.Where(item => item.Code == "book")
            .Select(item => item.Id).FirstAsync();
        seed.Organizations.Add(new Organization
        {
            Id = libraryId, DisplayName = "BIB clear library", Abbreviation = "BCL", IsActive = true
        });
        var request = new TitleRequest
        {
            LibraryOrganizationId = libraryId, Barcode = $"2{Guid.NewGuid():N}"[..14],
            Title = "Clear catalog match", MaterialFormatId = formatId, Status = "suggestion",
            Identifier = "9780000000001", BibId = "9001", IsbnCheckStatus = "found",
            IsbnCheckResult = "Matched BIB", AutoHold = true,
            CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow
        };
        seed.TitleRequests.Add(request);
        await seed.SaveChangesAsync();
        var mutation = scope.ServiceProvider.GetRequiredService<TitleRequestMutationService>();
        var result = await mutation.ActionAsync(actor, request.Id, new TitleRequestActionInput
        {
            Version = Convert.ToBase64String(request.RowVersion), Action = "edit",
            Bibid = JsonSerializer.SerializeToElement("")
        }, CancellationToken.None);
        Assert.AreEqual("updated", result.Code);
        await using var verify = await contexts.CreateDbContextAsync();
        var current = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id);
        Assert.IsNull(current.BibId);
        Assert.AreEqual("not_found", current.IsbnCheckStatus);
        StringAssert.Contains(current.IsbnCheckResult!, "cleared by staff");
    }

    [TestMethod]
    public async Task PurchasePromotionDoesNotCreateActiveSamePatronBibDuplicate()
    {
        const int libraryId = 99039;
        await using var scope = factory!.Services.CreateAsyncScope();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var seed = await contexts.CreateDbContextAsync();
        var formatId = await seed.MaterialFormats.Where(item => item.Code == "book")
            .Select(item => item.Id).FirstAsync();
        seed.Organizations.Add(new Organization
        {
            Id = libraryId, DisplayName = "Duplicate promotion library", Abbreviation = "DPL", IsActive = true
        });
        seed.WorkflowSettings.Add(new WorkflowSettings
        {
            OrganizationId = libraryId, AutoPromote = true,
            OutstandingTimeoutEnabled = false, PendingHoldTimeoutEnabled = false,
            HoldPickupTimeoutEnabled = false, AdditionalCopyTimeoutEnabled = false,
            UpdatedUtc = DateTime.UtcNow
        });
        var barcode = $"2{Guid.NewGuid():N}"[..14];
        var competitor = new TitleRequest
        {
            LibraryOrganizationId = libraryId, Barcode = barcode, Title = "Competing open BIB",
            MaterialFormatId = formatId, Status = "suggestion", BibId = "9001", AutoHold = true,
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1), UpdatedUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        var purchase = new TitleRequest
        {
            LibraryOrganizationId = libraryId, Barcode = barcode, Title = "Purchase awaiting promotion",
            MaterialFormatId = formatId, Status = "outstanding_purchase", BibId = "9001", AutoHold = true,
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1), UpdatedUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        seed.TitleRequests.AddRange(competitor, purchase);
        await seed.SaveChangesAsync();
        var initialVersion = purchase.RowVersion.ToArray();
        var processor = scope.ServiceProvider.GetRequiredService<WorkflowProcessingService>();
        var result = await processor.ProcessWorkflowAsync(libraryId, CancellationToken.None);
        Assert.AreEqual("completed", result.Code);
        Assert.AreEqual(0, result.Changed);
        await using var verify = await contexts.CreateDbContextAsync();
        var current = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == purchase.Id);
        Assert.AreEqual("outstanding_purchase", current.Status);
        Assert.IsTrue(current.RowVersion.SequenceEqual(initialVersion));
        Assert.AreEqual(0, await verify.TitleRequestEvents.CountAsync(item =>
            item.TitleRequestId == purchase.Id && item.EventType == "promoted"));
        var progress = await verify.QueueProgress.AsNoTracking()
            .SingleAsync(item => item.QueueName == QueueNames.PurchasePromotion &&
                                 item.ScopeOrganizationId == libraryId);
        Assert.AreEqual("cycle_complete", progress.LastOutcomeCode);
        Assert.AreEqual(purchase.Id, progress.LastOutcomeItemId);
    }

    [TestMethod]
    public async Task BibActionsRespectAutoHoldAndDateClearContract()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        await using var scope = factory!.Services.CreateAsyncScope();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var seed = await contexts.CreateDbContextAsync();
        var formatId = await seed.MaterialFormats.Where(item => item.Code == "book")
            .Select(item => item.Id).FirstAsync();
        var cases = new[]
        {
            (Action: "catalogFound", Source: "suggestion"),
            (Action: "catalogFound", Source: "outstanding_purchase"),
            (Action: "purchase", Source: "suggestion"),
            (Action: "alreadyOwn", Source: "suggestion")
        };
        var requests = new List<(TitleRequest Request, string Action, bool AutoHold)>();
        foreach (var entry in cases)
        {
            foreach (var autoHold in new[] { false, true })
            {
                var request = new TitleRequest
                {
                    LibraryOrganizationId = 2,
                    Barcode = $"2{Guid.NewGuid():N}"[..14],
                    Title = $"{entry.Action} {autoHold}",
                    MaterialFormatId = formatId,
                    Status = entry.Source,
                    AutoHold = autoHold,
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
                    UpdatedUtc = DateTime.UtcNow.AddMinutes(-1)
                };
                seed.TitleRequests.Add(request);
                requests.Add((request, entry.Action, autoHold));
            }
        }
        var copy = new TitleRequest
        {
            LibraryOrganizationId = 2,
            Barcode = $"2{Guid.NewGuid():N}"[..14],
            Title = "Additional-copy opt-out source",
            MaterialFormatId = formatId,
            Status = "suggestion",
            AutoHold = false,
            ExactPublicationDate = new DateOnly(2026, 10, 1),
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
            UpdatedUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        var editOutstanding = new TitleRequest
        {
            LibraryOrganizationId = 2,
            Barcode = $"2{Guid.NewGuid():N}"[..14],
            Title = "Edit outstanding opt-out",
            MaterialFormatId = formatId,
            Status = "outstanding_purchase",
            AutoHold = false,
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
            UpdatedUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        var editOutstandingRetained = new TitleRequest
        {
            LibraryOrganizationId = 2,
            Barcode = $"2{Guid.NewGuid():N}"[..14],
            Title = "Edit outstanding existing BIB opt-out",
            MaterialFormatId = formatId,
            Status = "outstanding_purchase",
            BibId = "9001",
            AutoHold = false,
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
            UpdatedUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        seed.TitleRequests.AddRange(copy, editOutstanding, editOutstandingRetained);
        await seed.SaveChangesAsync();

        var mutations = scope.ServiceProvider.GetRequiredService<TitleRequestMutationService>();
        foreach (var entry in requests)
        {
            var result = await mutations.ActionAsync(actor, entry.Request.Id, new TitleRequestActionInput
            {
                Version = StaffVersion.Encode(entry.Request.RowVersion),
                Action = entry.Action,
                Status = "pending_hold",
                Bibid = JsonSerializer.SerializeToElement("9001")
            }, CancellationToken.None);
            Assert.AreEqual("updated", result.Code, entry.Request.Title);
            await using var verify = await contexts.CreateDbContextAsync();
            var current = await verify.TitleRequests.AsNoTracking()
                .SingleAsync(item => item.Id == entry.Request.Id);
            Assert.AreEqual(entry.AutoHold ? "pending_hold" : "closed", current.Status, entry.Request.Title);
            Assert.AreEqual(entry.AutoHold ? null : "purchased_no_hold", current.CloseReason);
            Assert.AreEqual("9001", current.BibId);
            Assert.AreEqual(0, await verify.HoldPlacementOperations.CountAsync(item =>
                item.TitleRequestId == entry.Request.Id));
            if (!entry.AutoHold)
            {
                Assert.IsTrue(await verify.TitleRequestEvents.AnyAsync(item =>
                    item.TitleRequestId == entry.Request.Id && item.EventType == "autohold_opt_out"));
            }
        }

        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, editOutstanding.Id, new TitleRequestActionInput
        {
            Version = StaffVersion.Encode(editOutstanding.RowVersion), Action = "edit",
            Status = "outstanding_purchase", Bibid = JsonSerializer.SerializeToElement("9001")
        }, CancellationToken.None)).Code);
        await using (var verify = await contexts.CreateDbContextAsync())
        {
            var current = await verify.TitleRequests.AsNoTracking()
                .SingleAsync(item => item.Id == editOutstanding.Id);
            Assert.AreEqual("closed", current.Status);
            Assert.AreEqual("purchased_no_hold", current.CloseReason);
        }
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, editOutstandingRetained.Id,
            new TitleRequestActionInput
            {
                Version = StaffVersion.Encode(editOutstandingRetained.RowVersion), Action = "edit",
                Status = "outstanding_purchase", Bibid = JsonSerializer.SerializeToElement("9001")
            }, CancellationToken.None)).Code);
        await using (var verify = await contexts.CreateDbContextAsync())
        {
            var current = await verify.TitleRequests.AsNoTracking()
                .SingleAsync(item => item.Id == editOutstandingRetained.Id);
            Assert.AreEqual("closed", current.Status);
            Assert.AreEqual("purchased_no_hold", current.CloseReason);
            Assert.AreEqual("9001", current.BibId);
        }

        var pendingToOptOut = requests.First(entry => entry.AutoHold).Request;
        await using (var verify = await contexts.CreateDbContextAsync())
        {
            pendingToOptOut = await verify.TitleRequests.AsNoTracking()
                .SingleAsync(item => item.Id == pendingToOptOut.Id);
        }
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, pendingToOptOut.Id,
            new TitleRequestActionInput
            {
                Version = StaffVersion.Encode(pendingToOptOut.RowVersion), Action = "edit",
                Status = "pending_hold", Autohold = JsonSerializer.SerializeToElement(false)
            }, CancellationToken.None)).Code);
        await using (var verify = await contexts.CreateDbContextAsync())
        {
            var current = await verify.TitleRequests.AsNoTracking()
                .SingleAsync(item => item.Id == pendingToOptOut.Id);
            Assert.AreEqual("closed", current.Status);
            Assert.IsFalse(current.AutoHold);
            Assert.AreEqual("purchased_no_hold", current.CloseReason);
        }

        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, copy.Id, new TitleRequestActionInput
        {
            Version = StaffVersion.Encode(copy.RowVersion),
            Action = "additionalCopy",
            Status = "pending_hold",
            Bibid = JsonSerializer.SerializeToElement("9001"),
            ExactPublicationDate = JsonSerializer.SerializeToElement((string?)null)
        }, CancellationToken.None)).Code);
        await using (var verify = await contexts.CreateDbContextAsync())
        {
            var current = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == copy.Id);
            Assert.AreEqual("pending_hold", current.Status);
            Assert.IsTrue(current.AutoHold);
            Assert.AreEqual(new DateOnly(2026, 10, 1), current.ExactPublicationDate);
            Assert.AreEqual(1, await verify.AdditionalCopyRequests.CountAsync(item =>
                item.SourceTitleRequestId == copy.Id));
        }

        var dateRow = requests[0].Request;
        await using (var verify = await contexts.CreateDbContextAsync())
        {
            dateRow = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == dateRow.Id);
        }
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, dateRow.Id, new TitleRequestActionInput
        {
            Version = StaffVersion.Encode(dateRow.RowVersion), Action = "reopen", Status = "suggestion"
        }, CancellationToken.None)).Code);
        await using (var verify = await contexts.CreateDbContextAsync())
        {
            dateRow = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == dateRow.Id);
        }
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, dateRow.Id, new TitleRequestActionInput
        {
            Version = StaffVersion.Encode(dateRow.RowVersion), Action = "edit", Status = "suggestion",
            ExactPublicationDate = JsonSerializer.SerializeToElement("2026-10-01")
        }, CancellationToken.None)).Code);
        await using (var verify = await contexts.CreateDbContextAsync())
        {
            dateRow = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == dateRow.Id);
            Assert.AreEqual(new DateOnly(2026, 10, 1), dateRow.ExactPublicationDate);
        }
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, dateRow.Id, new TitleRequestActionInput
        {
            Version = StaffVersion.Encode(dateRow.RowVersion), Action = "edit", Status = "suggestion"
        }, CancellationToken.None)).Code);
        await using (var verify = await contexts.CreateDbContextAsync())
        {
            dateRow = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == dateRow.Id);
            Assert.AreEqual(new DateOnly(2026, 10, 1), dateRow.ExactPublicationDate);
        }
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, dateRow.Id, new TitleRequestActionInput
        {
            Version = StaffVersion.Encode(dateRow.RowVersion), Action = "edit", Status = "suggestion",
            ExactPublicationDate = JsonSerializer.SerializeToElement((string?)null)
        }, CancellationToken.None)).Code);
        await using var afterClear = await contexts.CreateDbContextAsync();
        Assert.IsNull((await afterClear.TitleRequests.AsNoTracking()
            .SingleAsync(item => item.Id == dateRow.Id)).ExactPublicationDate);

        var identifierEdit = new TitleRequest
        {
            LibraryOrganizationId = 2,
            Barcode = $"2{Guid.NewGuid():N}"[..14],
            Title = "Identifier edit with old BIB",
            MaterialFormatId = formatId,
            Status = "suggestion",
            AutoHold = true,
            Identifier = "9780000000001",
            BibId = "9002",
            IsbnCheckStatus = "found",
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
            UpdatedUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        afterClear.TitleRequests.Add(identifierEdit);
        await afterClear.SaveChangesAsync();
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, identifierEdit.Id, new TitleRequestActionInput
        {
            Version = StaffVersion.Encode(identifierEdit.RowVersion), Action = "edit", Status = "suggestion",
            Identifier = JsonSerializer.SerializeToElement("9780000000002"),
            Bibid = JsonSerializer.SerializeToElement("9002")
        }, CancellationToken.None)).Code);
        await using var afterIdentifierEdit = await contexts.CreateDbContextAsync();
        var editedIdentifier = await afterIdentifierEdit.TitleRequests.AsNoTracking()
            .SingleAsync(item => item.Id == identifierEdit.Id);
        Assert.AreEqual("9780000000002", editedIdentifier.Identifier);
        Assert.IsNull(editedIdentifier.BibId);
        Assert.AreEqual("pending", editedIdentifier.IsbnCheckStatus);
    }

    [TestMethod]
    public async Task AdditionalCopyRejectsOpenDuplicateWithoutWritingAndAllowsClosedOrRetainedBib()
    {
        using var client = factory!.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        await using var scope = factory.Services.CreateAsyncScope();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var seed = await contexts.CreateDbContextAsync();
        var formatId = await seed.MaterialFormats.Where(item => item.Code == "book")
            .Select(item => item.Id).FirstAsync();

        TitleRequest Source(string barcode, string title, string status, string? bib = null) => new()
        {
            LibraryOrganizationId = 2,
            Barcode = barcode,
            Title = title,
            MaterialFormatId = formatId,
            Status = status,
            BibId = bib,
            CloseReason = status == "closed" ? "manual" : null,
            AutoHold = false,
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
            UpdatedUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        var duplicateBarcode = $"2{Guid.NewGuid():N}"[..14];
        var source = Source(duplicateBarcode, "Duplicate source", "suggestion");
        var competing = Source(duplicateBarcode, "Existing active request", "pending_hold", "9001");
        var outstanding = Source(duplicateBarcode, "Outstanding source", "outstanding_purchase");
        var changedBib = Source(duplicateBarcode, "Changing BIB", "pending_hold", "9002");
        var closedBarcode = $"2{Guid.NewGuid():N}"[..14];
        var allowed = Source(closedBarcode, "Closed competitor source", "suggestion");
        var closed = Source(closedBarcode, "Closed competitor", "closed", "9001");
        var retained = Source($"2{Guid.NewGuid():N}"[..14], "Retained BIB", "pending_hold", "9001");
        seed.TitleRequests.AddRange(source, competing, outstanding, changedBib, allowed, closed, retained);
        await seed.SaveChangesAsync();

        TitleRequestActionInput Action(TitleRequest request, string bib = "9001") => new()
        {
            Version = StaffVersion.Encode(request.RowVersion),
            Action = "additionalCopy",
            Status = "pending_hold",
            Bibid = JsonSerializer.SerializeToElement(bib),
            EmailPurchaseReminder = true
        };

        var mutations = scope.ServiceProvider.GetRequiredService<TitleRequestMutationService>();
        var initialDispatches = dispatcher!.EnqueuedIds.Count;
        var initialOutboxes = await seed.EmailOutbox.CountAsync(item =>
            item.BusinessKey != null && item.BusinessKey.StartsWith("additional-copy-reminder:"));
        Assert.AreEqual("duplicate_open_request",
            (await mutations.ActionAsync(actor, source.Id, Action(source), CancellationToken.None)).Code);
        Assert.AreEqual("duplicate_open_request",
            (await mutations.ActionAsync(actor, outstanding.Id, Action(outstanding), CancellationToken.None)).Code);
        Assert.AreEqual("duplicate_open_request",
            (await mutations.ActionAsync(actor, changedBib.Id, Action(changedBib), CancellationToken.None)).Code);
        using var conflict = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{source.Id}/action",
            new { version = StaffVersion.Encode(source.RowVersion), action = "additionalCopy", status = "pending_hold", bibid = "9001" });
        Assert.AreEqual(HttpStatusCode.Conflict, conflict.StatusCode, await conflict.Content.ReadAsStringAsync());
        using (var body = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync()))
        {
            Assert.AreEqual("duplicate_open_request", body.RootElement.GetProperty("code").GetString());
        }

        await using (var verify = await contexts.CreateDbContextAsync())
        {
            foreach (var original in new[] { source, competing, outstanding, changedBib })
            {
                var current = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == original.Id);
                Assert.AreEqual(original.Status, current.Status);
                Assert.AreEqual(original.BibId, current.BibId);
                Assert.IsTrue(current.RowVersion.SequenceEqual(original.RowVersion));
                Assert.AreEqual(0, await verify.AdditionalCopyRequests.CountAsync(item =>
                    item.SourceTitleRequestId == original.Id));
            }
            Assert.AreEqual(initialOutboxes, await verify.EmailOutbox.CountAsync(item =>
                item.BusinessKey != null && item.BusinessKey.StartsWith("additional-copy-reminder:")));
        }
        Assert.AreEqual(initialDispatches, dispatcher!.EnqueuedIds.Count);

        using var closeDuplicate = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{source.Id}/action",
            new { version = StaffVersion.Encode(source.RowVersion), action = "closeDuplicate", status = "closed" });
        Assert.AreEqual(HttpStatusCode.OK, closeDuplicate.StatusCode,
            await closeDuplicate.Content.ReadAsStringAsync());

        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, allowed.Id, Action(allowed), CancellationToken.None)).Code);
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, retained.Id, Action(retained), CancellationToken.None)).Code);
        await using var afterSuccess = await contexts.CreateDbContextAsync();
        Assert.AreEqual("closed", (await afterSuccess.TitleRequests.AsNoTracking()
            .SingleAsync(item => item.Id == closed.Id)).Status);
        Assert.AreEqual("pending_hold", (await afterSuccess.TitleRequests.AsNoTracking()
            .SingleAsync(item => item.Id == allowed.Id)).Status);
        Assert.AreEqual(1, await afterSuccess.AdditionalCopyRequests.CountAsync(item =>
            item.SourceTitleRequestId == retained.Id));
    }

    [TestMethod]
    public async Task OrdinaryBibActionsRejectActiveDuplicatesWithoutPartialWrites()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        await using var scope = factory!.Services.CreateAsyncScope();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var seed = await contexts.CreateDbContextAsync();
        var formatId = await seed.MaterialFormats.Where(item => item.Code == "book")
            .Select(item => item.Id).FirstAsync();
        var barcode = $"2{Guid.NewGuid():N}"[..14];
        TitleRequest Source(string title, string status, string? bib = null, string? patron = null) => new()
        {
            LibraryOrganizationId = 2, Barcode = patron ?? barcode, Title = title,
            MaterialFormatId = formatId, Status = status, BibId = bib, AutoHold = true,
            CloseReason = status == "closed" ? "manual" : null,
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1), UpdatedUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        var competitor = Source("Competitor", "pending_hold", "9001");
        var catalog = Source("Catalog", "suggestion");
        var purchase = Source("Purchase", "suggestion");
        var alreadyOwn = Source("Already own", "suggestion");
        var changed = Source("Changed", "pending_hold", "9002");
        var retained = Source("Retained", "pending_hold", "9001");
        var closedPatron = $"2{Guid.NewGuid():N}"[..14];
        var closed = Source("Closed competitor", "closed", "9001", closedPatron);
        var allowed = Source("Allowed", "suggestion", patron: closedPatron);
        seed.TitleRequests.AddRange(competitor, catalog, purchase, alreadyOwn, changed, retained, closed, allowed);
        await seed.SaveChangesAsync();
        var mutations = scope.ServiceProvider.GetRequiredService<TitleRequestMutationService>();
        var initialEvents = await seed.TitleRequestEvents.CountAsync();
        var initialTags = await seed.TitleRequestWorkflowTags.CountAsync();
        var initialOperations = await seed.HoldPlacementOperations.CountAsync();
        var initialOutbox = await seed.EmailOutbox.CountAsync();
        foreach (var (request, action) in new[]
                 {
                     (catalog, "catalogFound"), (purchase, "purchase"),
                     (alreadyOwn, "alreadyOwn"), (changed, "edit")
                 })
        {
            var result = await mutations.ActionAsync(actor, request.Id, new TitleRequestActionInput
            {
                Version = StaffVersion.Encode(request.RowVersion), Action = action,
                Status = action == "edit" ? "pending_hold" : "pending_hold",
                Bibid = JsonSerializer.SerializeToElement("9001"),
                Notes = "Should roll back"
            }, CancellationToken.None);
            Assert.AreEqual("duplicate_open_request", result.Code, action);
            await using var verify = await contexts.CreateDbContextAsync();
            var current = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id);
            Assert.AreEqual(request.Status, current.Status);
            Assert.AreEqual(request.BibId, current.BibId);
            Assert.IsNull(current.Notes);
            Assert.IsTrue(current.RowVersion.SequenceEqual(request.RowVersion));
            Assert.AreEqual(initialEvents, await verify.TitleRequestEvents.CountAsync());
            Assert.AreEqual(initialTags, await verify.TitleRequestWorkflowTags.CountAsync());
            Assert.AreEqual(initialOperations, await verify.HoldPlacementOperations.CountAsync());
            Assert.AreEqual(initialOutbox, await verify.EmailOutbox.CountAsync());
        }
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, retained.Id, new TitleRequestActionInput
        {
            Version = StaffVersion.Encode(retained.RowVersion), Action = "edit", Status = "pending_hold",
            Bibid = JsonSerializer.SerializeToElement("9001")
        }, CancellationToken.None)).Code);
        Assert.AreEqual("updated", (await mutations.ActionAsync(actor, allowed.Id, new TitleRequestActionInput
        {
            Version = StaffVersion.Encode(allowed.RowVersion), Action = "catalogFound", Status = "pending_hold",
            Bibid = JsonSerializer.SerializeToElement("9001")
        }, CancellationToken.None)).Code);
    }

    [TestMethod]
    public async Task ConcurrentCatalogFoundActionsForSamePatronAndBibCannotBothCommit()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        await using var scope = factory!.Services.CreateAsyncScope();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var seed = await contexts.CreateDbContextAsync();
        var formatId = await seed.MaterialFormats.Where(item => item.Code == "book")
            .Select(item => item.Id).FirstAsync();
        var barcode = $"2{Guid.NewGuid():N}"[..14];
        TitleRequest Source(string title) => new()
        {
            LibraryOrganizationId = 2, Barcode = barcode, Title = title,
            MaterialFormatId = formatId, Status = "suggestion", AutoHold = true,
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1), UpdatedUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        var first = Source("First catalog action");
        var second = Source("Second catalog action");
        seed.TitleRequests.AddRange(first, second);
        await seed.SaveChangesAsync();
        var validationBarrier = new ConcurrentBibValidationBarrier();
        await using var racingFactory = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton<IStaffPolarisProvider>(validationBarrier);
        }));
        using var racingClient = racingFactory.CreateClient();
        await using var racingScope = racingFactory.Services.CreateAsyncScope();
        var mutations = racingScope.ServiceProvider.GetRequiredService<TitleRequestMutationService>();
        async Task<TitleRequestMutationResult> Run(TitleRequest request) =>
            await mutations.ActionAsync(actor, request.Id, new TitleRequestActionInput
            {
                Version = StaffVersion.Encode(request.RowVersion), Action = "catalogFound",
                Status = "pending_hold", Bibid = JsonSerializer.SerializeToElement("9001")
            }, CancellationToken.None);
        var results = await Task.WhenAll(Task.Run(() => Run(first)), Task.Run(() => Run(second)));
        CollectionAssert.AreEquivalent(new[] { "updated", "duplicate_open_request" },
            results.Select(item => item.Code).ToArray());
        await using var verify = await contexts.CreateDbContextAsync();
        var current = await verify.TitleRequests.AsNoTracking()
            .Where(item => item.Id == first.Id || item.Id == second.Id).ToListAsync();
        Assert.AreEqual(1, current.Count(item => item.Status == "pending_hold" && item.BibId == "9001"));
        Assert.AreEqual(1, current.Count(item => item.Status == "suggestion" && item.BibId == null));
    }

    [TestMethod]
    public async Task ConcurrentAdditionalCopyActionsForSamePatronAndBibCannotBothCommit()
    {
        using var client = factory!.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var seed = await contexts.CreateDbContextAsync();
        var formatId = await seed.MaterialFormats.Where(item => item.Code == "book")
            .Select(item => item.Id).FirstAsync();
        var barcode = $"2{Guid.NewGuid():N}"[..14];
        TitleRequest Source(string title) => new()
        {
            LibraryOrganizationId = 2,
            Barcode = barcode,
            Title = title,
            MaterialFormatId = formatId,
            Status = "suggestion",
            AutoHold = false,
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
            UpdatedUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        var first = Source("First concurrent source");
        var second = Source("Second concurrent source");
        seed.TitleRequests.AddRange(first, second);
        await seed.SaveChangesAsync();

        var validationBarrier = new ConcurrentBibValidationBarrier();
        await using var racingFactory = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton<IStaffPolarisProvider>(validationBarrier);
        }));
        using var racingClient = racingFactory.CreateClient();
        await using var racingScope = racingFactory.Services.CreateAsyncScope();
        var mutations = racingScope.ServiceProvider.GetRequiredService<TitleRequestMutationService>();
        async Task<TitleRequestMutationResult> Run(TitleRequest request)
        {
            return await mutations.ActionAsync(actor, request.Id, new TitleRequestActionInput
            {
                Version = StaffVersion.Encode(request.RowVersion),
                Action = "additionalCopy",
                Status = "pending_hold",
                Bibid = JsonSerializer.SerializeToElement("9001")
            }, CancellationToken.None);
        }
        var firstAction = Task.Run(() => Run(first));
        var secondAction = Task.Run(() => Run(second));
        var results = await Task.WhenAll(firstAction, secondAction);
        CollectionAssert.AreEquivalent(new[] { "updated", "duplicate_open_request" },
            results.Select(item => item.Code).ToArray());

        await using var verify = await contexts.CreateDbContextAsync();
        var current = await verify.TitleRequests.AsNoTracking()
            .Where(item => item.Id == first.Id || item.Id == second.Id).ToListAsync();
        Assert.AreEqual(1, current.Count(item => item.Status == "pending_hold" && item.BibId == "9001"));
        Assert.AreEqual(1, current.Count(item => item.Status == "suggestion" && item.BibId == null));
        Assert.AreEqual(1, await verify.AdditionalCopyRequests.CountAsync(item =>
            item.SourceTitleRequestId == first.Id || item.SourceTitleRequestId == second.Id));
    }

    private sealed class ConcurrentBibValidationBarrier : IStaffPolarisProvider
    {
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;

        public async Task<BibValidationResult> ValidateBibAsync(int bibId, CancellationToken cancellationToken)
        {
            Assert.AreEqual(9001, bibId);
            if (Interlocked.Increment(ref arrivals) == 2)
            {
                gate.SetResult();
            }
            await gate.Task.WaitAsync(cancellationToken);
            return new BibValidationResult(true);
        }

        public Task<IReadOnlyList<PolarisHoldSnapshot>> GetPatronHoldsAsync(
            string barcode, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<HoldProviderResult> CreateHoldAsync(
            HoldCreateCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<HoldProviderResult> ReplyToHoldAsync(
            HoldReplyCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
