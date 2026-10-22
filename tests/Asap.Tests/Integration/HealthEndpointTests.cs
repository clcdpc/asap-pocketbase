using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

[TestClass]
public sealed class HealthEndpointTests
{
    private WebApplicationFactory<Program>? _factory;

    [TestInitialize]
    public void Initialize()
    {
        _factory = new WebApplicationFactory<Program>();
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task MissingConfigurationLeavesLivenessHealthyAndReadinessSparse()
    {
        using var client = _factory!.CreateClient();

        var live = await client.GetAsync("/health/live");
        var ready = await client.GetAsync("/health/ready");

        Assert.AreEqual(HttpStatusCode.OK, live.StatusCode);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.AreEqual("healthy", await ReadStatus(live));
        Assert.AreEqual("unhealthy", await ReadStatus(ready));
        Assert.IsTrue(ready.Headers.Contains("X-Correlation-ID"));
        Assert.IsFalse((await ready.Content.ReadAsStringAsync()).Contains("config", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task UnsafeIncomingCorrelationIdIsReplaced()
    {
        using var client = _factory!.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Correlation-ID", "unsafe value with spaces");

        var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreNotEqual(
            "unsafe value with spaces",
            response.Headers.GetValues("X-Correlation-ID").Single());
    }

    [TestMethod]
    public void InvalidConfigurationDoesNotRegisterFallbackDataProtection()
    {
        Assert.IsNull(_factory!.Services.GetService<IDataProtectionProvider>());
    }

    private static async Task<string?> ReadStatus(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.HasCount(1, document.RootElement.EnumerateObject().ToList());
        return document.RootElement.GetProperty("status").GetString();
    }
}
