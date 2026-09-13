using Asap.Web.Features.Staff;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;

namespace Asap.Tests.Unit;

[TestClass]
public sealed class StaffAuthenticationTests
{
    [TestMethod]
    [DataRow(null, "/staff/")]
    [DataRow("", "/staff/")]
    [DataRow(" /staff/?status=suggestion#request-42 ", "/staff/?status=suggestion#request-42")]
    [DataRow("/", "/")]
    [DataRow("https://example.org/staff/", "/staff/")]
    [DataRow("//example.org/staff/", "/staff/")]
    [DataRow("/\\example.org/staff/", "/staff/")]
    [DataRow("javascript:alert(1)", "/staff/")]
    [DataRow("/staff/\r\nLocation: https://example.org", "/staff/")]
    public void StaffReturnUrlAllowsOnlyLocalPaths(string? value, string expected)
    {
        Assert.AreEqual(expected, StaffAuthenticationEndpoints.LocalReturnUrl(value));
    }

    [TestMethod]
    public void EntraIssuerMustBeAllowedAndMatchTheTenantClaim()
    {
        var allowedTenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var allowedIssuer = $"https://login.microsoftonline.com/{allowedTenant:D}/v2.0";
        var allowed = new HashSet<Guid> { allowedTenant };

        Assert.AreEqual(
            allowedIssuer + "/",
            StaffAuthenticationRegistration.ValidateIssuer(allowedIssuer + "/", allowed));
        Assert.IsTrue(StaffAuthenticationRegistration.IssuerMatchesTenant(allowedIssuer, allowedTenant));
        Assert.IsFalse(StaffAuthenticationRegistration.IssuerMatchesTenant(allowedIssuer, otherTenant));
        Assert.ThrowsExactly<SecurityTokenInvalidIssuerException>(() =>
            StaffAuthenticationRegistration.ValidateIssuer(
                $"https://login.microsoftonline.com/{otherTenant:D}/v2.0",
                allowed));
        Assert.ThrowsExactly<SecurityTokenInvalidIssuerException>(() =>
            StaffAuthenticationRegistration.ValidateIssuer(
                $"https://login.microsoftonline.com/{allowedTenant:D}/v1.0",
                allowed));
    }

    [TestMethod]
    public async Task HeaderBasedStaffAuthenticationSchemeExistsOnlyInTesting()
    {
        foreach (var (environmentName, expected) in new[]
                 {
                     ("Testing", true),
                     ("Development", false),
                     ("Production", false)
                 })
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddStaffAuthentication(
                TestConfigurationFactory.Create(),
                new StubWebHostEnvironment(environmentName));
            await using var provider = services.BuildServiceProvider();
            var schemes = await provider.GetRequiredService<IAuthenticationSchemeProvider>()
                .GetAllSchemesAsync();

            Assert.AreEqual(
                expected,
                schemes.Any(item => item.Name == TestingStaffAuthenticationHandler.SchemeName),
                environmentName);
        }
    }

    private sealed class StubWebHostEnvironment(string environmentName) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Asap.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = environmentName;
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
