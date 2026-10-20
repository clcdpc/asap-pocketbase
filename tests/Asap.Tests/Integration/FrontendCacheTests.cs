using System.Net;
using Asap.Tests.Assets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Asap.Tests.Integration;

[TestClass]
[DoNotParallelize]
public sealed class FrontendCacheTests
{
    private static string repositoryRoot = null!;
    private static DirectoryInfo published = null!;
    private static WebApplicationFactory<Program> application = null!;
    private static HttpClient client = null!;

    [ClassInitialize]
    public static async Task Initialize(TestContext context)
    {
        repositoryRoot = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
        published = Directory.CreateTempSubdirectory("asap-cache-published-");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await FrontendPublishTests.RunAsync(repositoryRoot, timeout.Token, "dotnet", "publish",
            "src/Asap.Web/Asap.Web.csproj", "-c", "Release", "--no-build", "--no-restore", "-o", published.FullName);
        await FrontendPublishTests.VerifyAsync(repositoryRoot, published.FullName, timeout.Token);
        application = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseContentRoot(published.FullName);
            builder.UseWebRoot(Path.Combine(published.FullName, "wwwroot"));
            builder.UseSetting("Asap:ConfigFile", Path.Combine(published.FullName, "missing-configuration.json"));
        });
        application.UseKestrel(0);
        client = application.CreateClient();
        context.WriteLine($"Serving published frontend from {published.FullName} at {client.BaseAddress}");
    }

    [ClassCleanup]
    public static async Task Cleanup()
    {
        client?.Dispose();
        if (application is not null)
        {
            await application.DisposeAsync();
        }
        if (published is not null)
        {
            published.Delete(recursive: true);
        }
    }

    [TestMethod]
    [DataRow("/staff/")]
    [DataRow("/staff/index.html")]
    [DataRow("/staff/app.js")]
    [DataRow("/staff/app.js?v=previous-deployment")]
    [DataRow("/staff/js/settings.js")]
    [DataRow("/staff/js/workflow.js")]
    [DataRow("/staff/styles.css")]
    [DataRow("/patron/?libraryOrgId=2&embed=1")]
    [DataRow("/patron/app.js")]
    [DataRow("/patron/js/bootstrap.js")]
    [DataRow("/patron/js/api.js")]
    [DataRow("/patron/embed.js")]
    [DataRow("/shared/http.js")]
    public async Task PublishedFirstPartyFilesRequireRevalidationAndRetainConditionalResponses(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await client.GetAsync(path, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, path);
        AssertRevalidation(response);
        Assert.IsNotNull(response.Headers.ETag);
        Assert.IsNotNull(response.Content.Headers.LastModified);
        var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
        Assert.IsGreaterThan(0, bytes.Length);

        using var etagRequest = new HttpRequestMessage(HttpMethod.Get, path);
        etagRequest.Headers.IfNoneMatch.Add(response.Headers.ETag);
        using var etagResponse = await client.SendAsync(etagRequest, timeout.Token);
        Assert.AreEqual(HttpStatusCode.NotModified, etagResponse.StatusCode);
        AssertRevalidation(etagResponse);
        Assert.AreEqual(0, (await etagResponse.Content.ReadAsByteArrayAsync(timeout.Token)).Length);

        using var dateRequest = new HttpRequestMessage(HttpMethod.Get, path);
        dateRequest.Headers.IfModifiedSince = response.Content.Headers.LastModified;
        using var dateResponse = await client.SendAsync(dateRequest, timeout.Token);
        Assert.AreEqual(HttpStatusCode.NotModified, dateResponse.StatusCode);
        AssertRevalidation(dateResponse);
    }

    [TestMethod]
    [DataRow("/vendor/gridjs/6.2.0/gridjs.umd.js", true)]
    [DataRow("/vendor/bootstrap/4.1.3/css/bootstrap.min.css", true)]
    [DataRow("/vendor/font-awesome/4.7.0/fonts/fontawesome-webfont.woff2", true)]
    [DataRow("/jpl.png", false)]
    [DataRow("/health/live", false)]
    public async Task VendorAssetsStayCacheableAndOtherResponsesRetainTheirPolicy(string path, bool immutable)
    {
        using var response = await client.GetAsync(path);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        if (immutable)
        {
            Assert.AreEqual("public, max-age=31536000, immutable", response.Headers.CacheControl?.ToString());
        }
        else
        {
            Assert.IsNull(response.Headers.CacheControl);
        }
    }

    [TestMethod]
    public async Task NormalNavigationRefreshesPublishedEntryAndNestedModulesWithRealBrowserCaching()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        // Control host reproduces the old UseDefaultFiles/UseStaticFiles contract.
        // The corrected host above runs Program against the actual published files.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = published.FullName,
            WebRootPath = Path.Combine(published.FullName, "wwwroot"),
            EnvironmentName = "Testing"
        });
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        await using var previousContract = builder.Build();
        previousContract.UseDefaultFiles();
        previousContract.UseStaticFiles();
        await previousContract.StartAsync(timeout.Token);
        var artifacts = Path.Combine(repositoryRoot, ".artifacts", "browser", "frontend-cache");
        Directory.CreateDirectory(artifacts);
        await FrontendPublishTests.RunAsync(repositoryRoot, timeout.Token, "node",
            Path.Combine(repositoryRoot, "tests", "browser", "frontend-cache.cjs"),
            client.BaseAddress!.GetLeftPart(UriPartial.Authority), artifacts,
            Path.Combine(published.FullName, "wwwroot"), previousContract.Urls.Single());
        await previousContract.StopAsync(timeout.Token);
    }

    private static void AssertRevalidation(HttpResponseMessage response)
    {
        var cache = response.Headers.CacheControl;
        Assert.IsNotNull(cache);
        Assert.IsTrue(cache.NoCache);
        Assert.AreEqual(TimeSpan.Zero, cache.MaxAge);
        Assert.IsTrue(cache.MustRevalidate);
        Assert.IsFalse(cache.NoStore);
    }
}
