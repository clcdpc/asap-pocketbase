using Asap.Web.Features.Administration;

namespace Asap.Tests.Unit;

[TestClass]
public sealed class PublicPatronLinkBuilderTests
{
    [TestMethod]
    public void ActiveLibraryUsesConfiguredOriginAndServedEmbedContract()
    {
        var links = PublicPatronLinkBuilder.Build("https://library.example.org/staff/", 82, true);

        Assert.IsNotNull(links);
        Assert.AreEqual("https://library.example.org/patron/?libraryOrgId=82", links.Url);
        StringAssert.Contains(links.Iframe, "src=\"https://library.example.org/patron/?libraryOrgId=82&amp;embed=1\"");
        StringAssert.Contains(links.AutoResize, "data-asap-suggestions");
        StringAssert.Contains(links.AutoResize, "data-src=\"https://library.example.org\"");
        StringAssert.Contains(links.AutoResize, "data-library-org-id=\"82\"");
        StringAssert.Contains(links.AutoResize, "src=\"https://library.example.org/patron/embed.js\"");
    }

    [TestMethod]
    public void SystemInactiveAndUntrustedBaseDoNotProduceLinks()
    {
        Assert.IsNull(PublicPatronLinkBuilder.Build("https://library.example.org/staff/", 1, true));
        Assert.IsNull(PublicPatronLinkBuilder.Build("https://library.example.org/staff/", 82, false));
        Assert.IsNull(PublicPatronLinkBuilder.Build(null, 82, true));
        Assert.IsNull(PublicPatronLinkBuilder.Build("https://attacker.example.org@library.example.org/staff/", 82, true));
        Assert.IsNull(PublicPatronLinkBuilder.Build("https://library.example.org/staff/?next=evil", 82, true));
        Assert.IsNull(PublicPatronLinkBuilder.Build("https://library.example.org/other/", 82, true));
        Assert.IsNull(PublicPatronLinkBuilder.Build("http://library.example.org/staff/", 82, true));
    }
}
