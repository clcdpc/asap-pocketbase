using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;

namespace Asap.Tests.Unit;

[TestClass]
public sealed class RejectionTemplatePolicyTests
{
    private const long LargeId = 9007199254740993;

    [TestMethod]
    public void ChoicesUseEffectiveLibraryContentAndExactIdentity()
    {
        var rows = new[]
        {
            Template(LargeId, 1, "rejection:standard", "System", "Subject", "Body"),
            Template(LargeId + 1, 2, "rejection:standard", "Library", "Library subject", "Library body", LargeId),
            Template(LargeId + 2, 2, "rejection:custom", "Custom", "Custom subject", "Custom body", custom: true),
            Template(LargeId + 3, 3, "rejection:foreign", "Foreign", "Foreign subject", "Foreign body", custom: true),
            Template(LargeId + 4, 2, "rejection:local", "Local", "Local subject", "Local body")
        };

        var choices = RejectionTemplatePolicy.Choices(rows, 2);
        Assert.AreEqual(3, choices.Count);
        Assert.AreEqual("9007199254740993", choices[0].Id);
        Assert.AreEqual("Library", choices[0].Name);
        Assert.AreEqual("9007199254740995", choices[1].Id);
        Assert.AreEqual("9007199254740997", choices[2].Id);
        Assert.AreEqual("Library subject", RejectionTemplatePolicy.Resolve(rows, 2, LargeId)!.Subject);
        Assert.AreEqual("Local subject", RejectionTemplatePolicy.Resolve(rows, 2, LargeId + 4)!.Subject);
        Assert.IsNull(RejectionTemplatePolicy.Resolve(rows, 2, LargeId + 3));
    }

    [TestMethod]
    public void MissingHiddenAndUnusableSelectionsNeverFallback()
    {
        var rows = new[]
        {
            Template(10, 1, "rejection:standard", "Standard", "Subject", "Body"),
            Template(11, 2, "rejection:standard", "Hidden", null, null, 10, hidden: true),
            Template(12, 2, "rejection:custom", "Empty", "Subject", " ", custom: true),
            Template(13, 2, "other:template", "Other", "Subject", "Body", custom: true)
        };

        Assert.IsNull(RejectionTemplatePolicy.Resolve(rows, 2, 10));
        Assert.IsNull(RejectionTemplatePolicy.Resolve(rows, 2, 11));
        Assert.IsNull(RejectionTemplatePolicy.Resolve(rows, 2, 12));
        Assert.IsNull(RejectionTemplatePolicy.Resolve(rows, 2, 13));
        Assert.IsNull(RejectionTemplatePolicy.Resolve(rows, 2, 999));
        Assert.AreEqual(0, RejectionTemplatePolicy.Choices(rows, 2).Count);
    }

    [TestMethod]
    public void EmptySelectionUsesEffectiveDefaultRejectionEmail()
    {
        var fallback = RejectionTemplatePolicy.Default([], 2);
        Assert.AreEqual("Default rejection email", fallback.Name);
        Assert.IsFalse(fallback.IsHidden);
        Assert.IsTrue(fallback.Body.Contains("{{title}}", StringComparison.Ordinal));

        var rows = new[]
        {
            Template(20, 1, "rejected", "System", "System subject", "System body"),
            Template(21, 2, "rejected", "Library", "Library subject", "Library body", 20)
        };
        var effective = RejectionTemplatePolicy.Default(rows, 2);
        Assert.AreEqual("Library subject", effective.Subject);
        Assert.AreEqual("Library body", effective.Body);
        rows[1].IsHidden = true;
        Assert.IsTrue(RejectionTemplatePolicy.Default(rows, 2).IsHidden);
    }

    private static EmailTemplate Template(long id, int organizationId, string key,
        string name, string? subject, string? body, long? sourceId = null,
        bool custom = false, bool hidden = false) => new()
    {
        Id = id,
        OrganizationId = organizationId,
        TemplateKey = key,
        SourceTemplateId = sourceId,
        DisplayName = name,
        SubjectTemplate = subject,
        BodyTemplate = body,
        IsCustom = custom,
        IsHidden = hidden
    };
}
