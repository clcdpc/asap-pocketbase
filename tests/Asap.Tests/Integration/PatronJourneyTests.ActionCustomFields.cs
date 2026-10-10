using System.Text.Json;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task StaffCustomFieldEditValidatesCanonicalizesAndPreservesHistory()
    {
        const int libraryId = 92914;
        var actor = await ReadConfiguredSuperAdminAsync();
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var seed = await contexts.CreateDbContextAsync();
        var bookId = await seed.MaterialFormats.Where(item => item.OwnerOrganizationId == 1 && item.Code == "book")
            .Select(item => item.Id).SingleAsync();
        var dvdId = await seed.MaterialFormats.Where(item => item.OwnerOrganizationId == 1 && item.Code == "dvd")
            .Select(item => item.Id).SingleAsync();
        seed.Organizations.Add(new Organization
        {
            Id = libraryId, DisplayName = "Action field library", Abbreviation = "AFL",
            OrganizationCodeId = 2, ParentOrganizationId = 1, IsActive = true
        });
        var note = new PatronCustomField
        {
            LibraryOrganizationId = libraryId, FieldKey = "audience_note", FieldType = "text",
            Label = "Audience note", IsEnabled = true, SortOrder = 10
        };
        var selection = new PatronCustomField
        {
            LibraryOrganizationId = libraryId, FieldKey = "audience_code", FieldType = "select",
            Label = "Audience", IsEnabled = true, SortOrder = 20
        };
        var dvdNote = new PatronCustomField
        {
            LibraryOrganizationId = libraryId, FieldKey = "dvd_note", FieldType = "text",
            Label = "DVD note", IsEnabled = true, SortOrder = 30
        };
        seed.PatronCustomFields.AddRange(note, selection, dvdNote);
        await seed.SaveChangesAsync();
        seed.PatronCustomFieldOptions.Add(new PatronCustomFieldOption
        {
            PatronCustomFieldId = selection.Id, OptionKey = "youth", Label = "Youth", IsEnabled = true, SortOrder = 10
        });
        seed.MaterialFormatCustomFieldRules.AddRange(
            new MaterialFormatCustomFieldRule
            {
                LibraryOrganizationId = libraryId, MaterialFormatId = bookId,
                PatronCustomFieldId = note.Id, Mode = "optional"
            },
            new MaterialFormatCustomFieldRule
            {
                LibraryOrganizationId = libraryId, MaterialFormatId = bookId,
                PatronCustomFieldId = selection.Id, Mode = "required"
            },
            new MaterialFormatCustomFieldRule
            {
                LibraryOrganizationId = libraryId, MaterialFormatId = dvdId,
                PatronCustomFieldId = selection.Id, Mode = "required"
            },
            new MaterialFormatCustomFieldRule
            {
                LibraryOrganizationId = libraryId, MaterialFormatId = dvdId,
                PatronCustomFieldId = dvdNote.Id, Mode = "required"
            });
        var request = new TitleRequest
        {
            LibraryOrganizationId = libraryId, Barcode = $"2{Guid.NewGuid():N}"[..14],
            Title = "Custom field edit", MaterialFormatId = bookId, Status = "suggestion", AutoHold = true,
            CustomFieldsJson = """
                {"audience_note":{"label":"Audience note","type":"text","value":"Old"},
                 "retired_note":{"label":"Retired note","type":"text","value":"Keep"}}
                """,
            CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime, UpdatedUtc = timeProvider!.GetUtcNow().UtcDateTime
        };
        seed.TitleRequests.Add(request);
        await seed.SaveChangesAsync();
        try
        {
            var mutations = factory.Services.GetRequiredService<TitleRequestMutationService>();
            var valid = await mutations.ActionAsync(actor, request.Id, new TitleRequestActionInput
            {
                Version = StaffVersion.Encode(request.RowVersion), Action = "edit",
                CustomFields = JsonSerializer.SerializeToElement(new
                {
                    audience_note = new { value = "  Updated  " },
                    audience_code = new { value = "Youth" },
                    injected = new { value = "Ignore" }
                })
            }.ToCommand(), CancellationToken.None);
            Assert.AreEqual("updated", valid.Code);
            await using var afterValid = await contexts.CreateDbContextAsync();
            var current = await afterValid.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id);
            using (var fields = JsonDocument.Parse(current.CustomFieldsJson!))
            {
                Assert.AreEqual("Updated", fields.RootElement.GetProperty("audience_note").GetProperty("value").GetString());
                Assert.AreEqual("youth", fields.RootElement.GetProperty("audience_code").GetProperty("value").GetString());
                Assert.AreEqual("Youth", fields.RootElement.GetProperty("audience_code").GetProperty("displayValue").GetString());
                Assert.AreEqual("Keep", fields.RootElement.GetProperty("retired_note").GetProperty("value").GetString());
                Assert.IsFalse(fields.RootElement.TryGetProperty("injected", out _));
            }

            var beforeEvents = await afterValid.TitleRequestEvents.CountAsync(item => item.TitleRequestId == request.Id);
            foreach (var rejectedFields in new[]
            {
                JsonSerializer.SerializeToElement(new { audience_note = "No required selection" }),
                JsonSerializer.SerializeToElement(new { audience_code = new { value = "invalid" } }),
                JsonSerializer.SerializeToElement(new { audience_code = new { value = 123 } })
            })
            {
                var rejected = await mutations.ActionAsync(actor, request.Id, new TitleRequestActionInput
                {
                    Version = StaffVersion.Encode(current.RowVersion), Action = "edit",
                    Title = "Must not persist", CustomFields = rejectedFields
                }.ToCommand(), CancellationToken.None);
                Assert.AreEqual("invalid_custom_fields", rejected.Code);
                await using var unchangedContext = await contexts.CreateDbContextAsync();
                var unchanged = await unchangedContext.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id);
                CollectionAssert.AreEqual(current.RowVersion, unchanged.RowVersion);
                Assert.AreEqual("Custom field edit", unchanged.Title);
                Assert.AreEqual(beforeEvents, await unchangedContext.TitleRequestEvents.CountAsync(item => item.TitleRequestId == request.Id));
            }
            var changedFormatRejected = await mutations.ActionAsync(actor, request.Id, new TitleRequestActionInput
            {
                Version = StaffVersion.Encode(current.RowVersion), Action = "edit", Format = "dvd",
                CustomFields = JsonSerializer.SerializeToElement(new { })
            }.ToCommand(), CancellationToken.None);
            Assert.AreEqual("invalid_custom_fields", changedFormatRejected.Code);
            await using (var unchangedFormat = await contexts.CreateDbContextAsync())
            {
                var unchanged = await unchangedFormat.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id);
                Assert.AreEqual(bookId, unchanged.MaterialFormatId);
                CollectionAssert.AreEqual(current.RowVersion, unchanged.RowVersion);
            }
            var omittedPayloadRejected = await mutations.ActionAsync(actor, request.Id, new TitleRequestActionInput
            {
                Version = StaffVersion.Encode(current.RowVersion), Action = "edit", Format = "dvd"
            }.ToCommand(), CancellationToken.None);
            Assert.AreEqual("invalid_custom_fields", omittedPayloadRejected.Code);
            await using (var unchangedFormat = await contexts.CreateDbContextAsync())
            {
                var unchanged = await unchangedFormat.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id);
                Assert.AreEqual(bookId, unchanged.MaterialFormatId);
                CollectionAssert.AreEqual(current.RowVersion, unchanged.RowVersion);
                Assert.AreEqual(beforeEvents, await unchangedFormat.TitleRequestEvents.CountAsync(item => item.TitleRequestId == request.Id));
            }

            var optionalCleared = await mutations.ActionAsync(actor, request.Id, new TitleRequestActionInput
            {
                Version = StaffVersion.Encode(current.RowVersion), Action = "edit",
                CustomFields = JsonSerializer.SerializeToElement(new
                {
                    audience_note = " ", audience_code = "youth"
                })
            }.ToCommand(), CancellationToken.None);
            Assert.AreEqual("updated", optionalCleared.Code);
            await using var afterClear = await contexts.CreateDbContextAsync();
            var cleared = await afterClear.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id);
            using var clearedFields = JsonDocument.Parse(cleared.CustomFieldsJson!);
            Assert.IsFalse(clearedFields.RootElement.TryGetProperty("audience_note", out _));
            Assert.AreEqual("Keep", clearedFields.RootElement.GetProperty("retired_note").GetProperty("value").GetString());
        }
        finally
        {
            await ExecuteNonQueryAsync(
                "DELETE FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] = @requestId; " +
                "DELETE FROM [asap].[TitleRequest] WHERE [Id] = @requestId; " +
                "DELETE FROM [asap].[MaterialFormatCustomFieldRule] WHERE [LibraryOrganizationId] = @libraryId; " +
                "DELETE FROM [asap].[PatronCustomFieldOption] WHERE [PatronCustomFieldId] IN (@noteId, @selectionId, @dvdNoteId); " +
                "DELETE FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = @libraryId; " +
                "DELETE FROM [asap].[Organization] WHERE [Id] = @libraryId;",
                ("@requestId", request.Id), ("@libraryId", libraryId),
                ("@noteId", note.Id), ("@selectionId", selection.Id), ("@dvdNoteId", dvdNote.Id));
        }
    }
}
