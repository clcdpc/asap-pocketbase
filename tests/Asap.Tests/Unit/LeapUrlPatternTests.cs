using Asap.Web.Features.Staff;

namespace Asap.Tests.Unit;

[TestClass]
public sealed class LeapUrlPatternTests
{
    [TestMethod]
    public void BibPatternValidationRequiresHttpAndCanonicalPlaceholder()
    {
        Assert.IsNull(LeapUrlPattern.ValidateBibPattern(null));
        Assert.IsNull(LeapUrlPattern.ValidateBibPattern("  "));
        Assert.IsNull(LeapUrlPattern.ValidateBibPattern("https://leap.example/bib/{{bibid}}"));
        Assert.IsNull(LeapUrlPattern.ValidateBibPattern("http://leap.example/bib/{{bibid}}"));
        Assert.IsNotNull(LeapUrlPattern.ValidateBibPattern("javascript:alert(1)/{{bibid}}"));
        Assert.IsNotNull(LeapUrlPattern.ValidateBibPattern("https://leap.example/bib/42"));
        Assert.IsNotNull(LeapUrlPattern.ValidateBibPattern("https://{{bibid}}"));
    }

    [TestMethod]
    public void PatronPatternValidationAcceptsCompatibilityPlaceholder()
    {
        Assert.IsNull(LeapUrlPattern.ValidatePatronPattern("https://leap.example/patron/{{patron-id}}"));
        Assert.IsNull(LeapUrlPattern.ValidatePatronPattern("https://leap.example/patron/{{patronId}}"));
        Assert.IsNotNull(LeapUrlPattern.ValidatePatronPattern("https://leap.example/patron/{{barcode}}"));
        Assert.IsNotNull(LeapUrlPattern.ValidatePatronPattern("data:text/html,{{patron-id}}"));
    }

    [TestMethod]
    public void BuildersReplaceEveryPlaceholderAndEncodeTheActualId()
    {
        Assert.AreEqual(
            "https://leap.example/bib/BIB%2F42%207/BIB%2F42%207",
            LeapUrlPattern.BuildBibUrl(
                "https://leap.example/bib/{{bibid}}/{{bibid}}",
                "BIB/42 7"));
        Assert.AreEqual(
            "https://leap.example/patron/9123?again=9123",
            LeapUrlPattern.BuildPatronUrl(
                "https://leap.example/patron/{{patron-id}}?again={{patronId}}",
                "9123"));
    }

    [TestMethod]
    public void BuildersReturnNoLinkForMissingIdsOrInvalidPatterns()
    {
        Assert.IsNull(LeapUrlPattern.BuildBibUrl("https://leap.example/bib/{{bibid}}", null));
        Assert.IsNull(LeapUrlPattern.BuildBibUrl("https://leap.example/bib/{{bibid}}", "  "));
        Assert.IsNull(LeapUrlPattern.BuildBibUrl("javascript:alert(1)/{{bibid}}", "42"));
        Assert.IsNull(LeapUrlPattern.BuildPatronUrl("https://leap.example/patron/{{patron-id}}", null));
        Assert.IsNull(LeapUrlPattern.BuildPatronUrl("https://leap.example/patron/{{barcode}}", "9123"));
    }
}
