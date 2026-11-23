using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;

namespace Asap.Tests.Email;

[TestClass]
public sealed class PatronEmailTemplateRendererTests
{
    [TestMethod]
    [DataRow("\n")]
    [DataRow("\r\n")]
    [DataRow("\r")]
    public void CustomRejectionPreservesParagraphsAndEscapesTheWholeBody(string newline)
    {
        var patron = new PatronSnapshot(7, "0007", null, null, null, null, null, 2, 2, "Library Two", null);
        var template = new EffectiveEmailTemplate("rejected", "Review closed:" + newline + "{{title}} by {{author}}",
            "<custom & \"quoted\">\\n{{name}}" + newline + newline + "{{title}} by {{author}}");
        var mail = PatronEmailTemplateRenderer.Render(template, patron, "A & B" + newline + "<end>", null, "Print", patron.Barcode);

        Assert.AreEqual("Review closed: A & B <end>", mail.Subject);
        Assert.AreEqual("<custom & \"quoted\">\nLibrary Patron\n\nA & B\n<end>", mail.BodyText);
        Assert.AreEqual("&lt;custom &amp; &quot;quoted&quot;&gt;<br>Library Patron<br><br>A &amp; B<br>&lt;end&gt;", mail.BodyHtml);
    }

    [TestMethod]
    [DataRow(" \tASAP\0 — {{title}}\u001b\u007f\u2028Notes\u2029[review]\r\n ", "ASAP — Café & 東京! Notes [review]")]
    [DataRow("ASAP — {{title}}  [review]", "ASAP — Café & 東京!  [review]")]
    public void SubjectPreservesTextAndUsesOneLine(string subjectTemplate, string expectedSubject)
    {
        var patron = new PatronSnapshot(7, "0007", null, null, null, null, null, 2, 2, "Library Two", null);
        var template = new EffectiveEmailTemplate("rejected", subjectTemplate, "Body");

        var mail = PatronEmailTemplateRenderer.Render(template, patron, "Café & 東京!", null, "Print", patron.Barcode);

        Assert.AreEqual(expectedSubject, mail.Subject);
    }

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
