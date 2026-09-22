using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;

namespace Asap.Tests.Unit;

[TestClass]
public sealed class PatronEmailTemplateRendererTests
{
    [TestMethod]
    public void SupportsTheSevenCaseSensitivePatronPlaceholders()
    {
        var rendered = PatronEmailTemplateRenderer.Render(
            new EffectiveEmailTemplate(
                "suggestion_submitted",
                "{{name}}|{{title}}|{{author}}|{{format}}|{{barcode}}|{{firstName}}|{{lastName}}",
                "{{name}} {{title}} {{author}} {{format}} {{barcode}} {{firstName}} {{lastName}}"),
            new PatronSnapshot(
                7,
                "00000000000000",
                "patron@example.org",
                "Alex",
                "Reader",
                "adult",
                "Adult",
                1,
                1,
                "Test Library",
                null),
            "Example title",
            "Example creator",
            "Video Game",
            "00000000000000");

        Assert.AreEqual(
            "Alex Reader|Example title|Example creator|Video Game|00000000000000|Alex|Reader",
            rendered.Subject);
        Assert.AreEqual(
            "Alex Reader Example title Example creator Video Game 00000000000000 Alex Reader",
            rendered.BodyText);
    }

    [TestMethod]
    public void LeavesUnknownTokensUnchanged()
    {
        var rendered = PatronEmailTemplateRenderer.Render(
            new EffectiveEmailTemplate("suggestion_submitted", "{{Title}} {{creator}}", "{{pin}}"),
            new PatronSnapshot(
                7,
                "00000000000000",
                "patron@example.org",
                null,
                null,
                "adult",
                "Adult",
                1,
                1,
                "Test Library",
                null),
            "Example title",
            null,
            "Book",
            "00000000000000");

        Assert.AreEqual("{{Title}} {{creator}}", rendered.Subject);
        Assert.AreEqual("{{pin}}", rendered.BodyText);
    }
}
