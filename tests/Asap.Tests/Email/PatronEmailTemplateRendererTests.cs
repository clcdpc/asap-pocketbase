using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;

namespace Asap.Tests.Email;

[TestClass]
public sealed class PatronEmailTemplateRendererTests
{
    [TestMethod]
    public void AdvertisedPlaceholdersAreResolvedByTheRenderer()
    {
        var placeholders = PatronEmailTemplateRenderer.SupportedPlaceholders;
        CollectionAssert.AreEquivalent(
            new[] { "name", "firstName", "lastName", "title", "author", "format", "barcode" },
            placeholders.ToArray());
        var text = string.Join(" ", placeholders.Select(key => $"{{{{{key}}}}}"));
        var patron = new PatronSnapshot(7, "20000000000007", "reader@example.org", "Ada", "Reader",
            null, null, 2, 2, "Library Two", null);
        var template = new EffectiveEmailTemplate("rejected", text, text + " {{unsupported}}");

        var rendered = PatronEmailTemplateRenderer.Render(template, patron,
            "Book", "Author", "Print", patron.Barcode);

        foreach (var key in placeholders)
        {
            Assert.IsFalse(rendered.Subject.Contains($"{{{{{key}}}}}", StringComparison.Ordinal));
            Assert.IsFalse(rendered.BodyText.Contains($"{{{{{key}}}}}", StringComparison.Ordinal));
        }
        StringAssert.Contains(rendered.BodyText, "{{unsupported}}");
    }
}
