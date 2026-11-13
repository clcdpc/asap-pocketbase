using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;

namespace Asap.Tests.Unit;

[TestClass]
public sealed class CatalogPresentationTests
{
    [TestMethod]
    [DataRow("{pacBaseUrl}/themes/shared/formats/formatid{id2}.gif", "https://catalog.example/polaris/themes/shared/formats/formatid01.gif")]
    [DataRow("{pacBaseUrl}/custom/{pacOrgId}/{MARCTypeOfMaterialID2}.gif", "https://catalog.example/polaris/custom/27/01.gif")]
    [DataRow("/icons/{id}/{MARCTypeOfMaterialID}/{SearchCode}.gif", "/icons/1/1/a%2Fb%3F%26.gif")]
    [DataRow("https://icons.example?code={SearchCode}", "https://icons.example/?code=a%2Fb%3F%26")]
    [DataRow("https://icons.example/static.gif", "https://icons.example/static.gif")]
    public void ExistingAndCatalogTokensUseOneSafeSubstitutionPath(string pattern, string expected)
    {
        var values = new CatalogPresentationValues("https://catalog.example/polaris/", pattern, 27);
        Assert.IsTrue(CatalogPresentation.IsIconPatternValid(pattern));
        Assert.AreEqual(expected, CatalogPresentation.IconUrl(values, 1, "a/b?&"));
    }

    [TestMethod]
    [DataRow("javascript:alert(1)")]
    [DataRow("//foreign.example/{id}.gif")]
    [DataRow("https://user:password@icons.example/{id}.gif")]
    [DataRow("https://{SearchCode}.example/{id}.gif")]
    [DataRow("https://icons.example/{unknown}.gif")]
    [DataRow("https://icons.example/\\evil/{id}.gif")]
    [DataRow("/icons/{{id}}.gif")]
    [DataRow("https://icons.example/?base={pacBaseUrl}")]
    [DataRow("{pacBaseUrl}/icons/{id}?base={pacBaseUrl}")]
    [DataRow("x")]
    public void UnsafeOrUnsupportedPatternsNeverProduceImages(string pattern)
    {
        Assert.IsFalse(CatalogPresentation.IsIconPatternValid(pattern));
        Assert.IsNull(CatalogPresentation.IconUrl(new(null, pattern, null), 1, "Book"));
    }

    [TestMethod]
    [DataRow("{pacBaseUrl}/icons/{id}.gif", null, 27, 1, "Book")]
    [DataRow("{pacBaseUrl}/icons/{id}.gif", "", 27, 1, "Book")]
    [DataRow("/icons/{pacOrgId}/{id}.gif", "https://catalog.example", null, 1, "Book")]
    [DataRow("/icons/{id}.gif", "https://catalog.example", 27, null, "Book")]
    [DataRow("/icons/{SearchCode}.gif", "https://catalog.example", 27, 1, null)]
    public void MissingRequiredSubstitutionsUseTheGenericFallback(
        string pattern, string? pacBaseUrl, int? pacOrganizationId, int? materialId, string? code)
    {
        Assert.IsNull(CatalogPresentation.IconUrl(new(pacBaseUrl, pattern, pacOrganizationId), materialId, code));
    }

    [TestMethod]
    public void NullInheritsButBlankDisablesWithoutInventingCatalogIdentity()
    {
        var system = new SystemSettings { PacBaseUrl = "https://system.example/polaris", MaterialTypeIconUrlPattern = "/icons/{id}.gif" };
        Assert.AreEqual(new CatalogPresentationValues(system.PacBaseUrl, system.MaterialTypeIconUrlPattern, null), CatalogPresentation.Resolve(system, null));
        var library = new LibraryCatalogSettings { OrganizationId = 932, PacOrganizationId = 27, PacBaseUrl = "", MaterialTypeIconUrlPattern = "" };
        var disabled = CatalogPresentation.Resolve(system, library);
        Assert.AreEqual("", disabled.PacBaseUrl);
        Assert.AreEqual("", disabled.FormatIconUrlPattern);
        Assert.IsNull(CatalogPresentation.IconUrl(disabled, 1, "Book"));
        library.PacBaseUrl = null;
        library.MaterialTypeIconUrlPattern = null;
        Assert.AreEqual(new CatalogPresentationValues(system.PacBaseUrl, system.MaterialTypeIconUrlPattern, 27), CatalogPresentation.Resolve(system, library));
    }
}
