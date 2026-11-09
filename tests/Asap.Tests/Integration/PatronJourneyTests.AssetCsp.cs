using System.Data.Common;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Asap.Web.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [TestCategory(TestCategories.Browser)]
    public async Task ConfiguredPatronAssetsSkipOriginSqlWhileDocumentsRevalidateCurrentCsp()
    {
        var probe = new EmbedOriginSqlProbe();
        await using var scoped = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContextFactory<AsapDbContext>(options => options.AddInterceptors(probe))));
        using var client = scoped.CreateClient();
        async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, int expectedCommands)
        {
            probe.Reset();
            var response = await client.SendAsync(request);
            var count = probe.Count;
            TestContext.WriteLine($"Embed-origin SQL: {request.Method} {request.RequestUri} => {(int)response.StatusCode}: {count}");
            Assert.AreEqual(expectedCommands, count, request.RequestUri?.ToString());
            return response;
        }
        static void AssertCache(HttpResponseMessage response)
        {
            var cache = response.Headers.CacheControl;
            Assert.IsNotNull(cache);
            Assert.IsTrue(cache.NoCache);
            Assert.IsTrue(cache.MustRevalidate);
            Assert.AreEqual(TimeSpan.Zero, cache.MaxAge);
            Assert.IsFalse(cache.NoStore);
            Assert.IsNotNull(response.Headers.ETag);
            Assert.IsNotNull(response.Content.Headers.LastModified);
        }
        static string Policy(string origin) => "default-src 'self'; script-src 'self'; " +
            "style-src 'self' 'unsafe-inline'; font-src 'self'; img-src 'self' data:; " +
            $"connect-src 'self'; frame-ancestors 'self' {origin}; base-uri 'self'; form-action 'self'";
        async Task SetOriginAsync(string origin)
        {
            await ExecuteNonQueryAsync("""
                DELETE FROM [asap].[PatronEmbedAllowedOrigin] WHERE [OrganizationId]=1;
                INSERT INTO [asap].[PatronEmbedAllowedOrigin] ([OrganizationId],[Origin],[NormalizedOrigin],[CreatedUtc])
                VALUES (1,@origin,@origin,SYSUTCDATETIME());
                """, ("@origin", origin));
        }
        try
        {
            foreach (var path in new[] { "/patron/app.js", "/patron/js/bootstrap.js?v=previous", "/patron/js/api.js", "/patron/embed.js?v=2" })
            {
                using var get = new HttpRequestMessage(HttpMethod.Get, path);
                // Client declarations cannot determine whether a resource is a document.
                get.Headers.Accept.ParseAdd("text/html");
                using var ordinary = await SendAsync(get, 0);
                Assert.AreEqual(HttpStatusCode.OK, ordinary.StatusCode);
                AssertCache(ordinary);
                Assert.IsGreaterThan(0, (await ordinary.Content.ReadAsByteArrayAsync()).Length);
                foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
                {
                    using var etag = new HttpRequestMessage(method, path);
                    etag.Headers.IfNoneMatch.Add(ordinary.Headers.ETag!);
                    using var conditional = await SendAsync(etag, 0);
                    Assert.AreEqual(HttpStatusCode.NotModified, conditional.StatusCode);
                    Assert.AreEqual(ordinary.Headers.CacheControl?.ToString(), conditional.Headers.CacheControl?.ToString());
                    Assert.AreEqual(ordinary.Headers.ETag, conditional.Headers.ETag);
                    Assert.AreEqual(0, (await conditional.Content.ReadAsByteArrayAsync()).Length);
                    using var date = new HttpRequestMessage(method, path);
                    date.Headers.IfModifiedSince = ordinary.Content.Headers.LastModified;
                    using var dated = await SendAsync(date, 0);
                    Assert.AreEqual(HttpStatusCode.NotModified, dated.StatusCode);
                    Assert.AreEqual(ordinary.Headers.CacheControl?.ToString(), dated.Headers.CacheControl?.ToString());
                }
                using var head = new HttpRequestMessage(HttpMethod.Head, path);
                using var headResponse = await SendAsync(head, 0);
                Assert.AreEqual(HttpStatusCode.OK, headResponse.StatusCode);
                AssertCache(headResponse);
                Assert.AreEqual(0, (await headResponse.Content.ReadAsByteArrayAsync()).Length);
            }
            foreach (var path in new[] { "/patron?libraryOrgId=2", "/patron/?libraryOrgId=2", "/patron/index.html?embed=1" })
            {
                await SetOriginAsync("https://before.example.org");
                using var get = new HttpRequestMessage(HttpMethod.Get, path);
                get.Headers.Accept.ParseAdd("application/javascript");
                using var document = await SendAsync(get, 1);
                Assert.AreEqual(HttpStatusCode.OK, document.StatusCode);
                Assert.AreEqual(path.StartsWith("/patron?") ? "/patron/" : path.Split('?')[0],
                    document.RequestMessage!.RequestUri!.AbsolutePath);
                Assert.AreEqual(new Uri(client.BaseAddress!, path).Query, document.RequestMessage.RequestUri.Query);
                Assert.AreEqual(Policy("https://before.example.org"), document.Headers.GetValues("Content-Security-Policy").Single());
                AssertCache(document);
                StringAssert.Contains(await document.Content.ReadAsStringAsync(), "<title>Material Suggestion</title>");
                await SetOriginAsync("https://after.example.org");
                using var etag = new HttpRequestMessage(HttpMethod.Get, path);
                etag.Headers.IfNoneMatch.Add(document.Headers.ETag!);
                using var revalidated = await SendAsync(etag, 1);
                Assert.AreEqual(HttpStatusCode.NotModified, revalidated.StatusCode);
                Assert.AreEqual(Policy("https://after.example.org"), revalidated.Headers.GetValues("Content-Security-Policy").Single());
                using var date = new HttpRequestMessage(HttpMethod.Head, path);
                date.Headers.IfModifiedSince = document.Content.Headers.LastModified;
                using var dated = await SendAsync(date, 1);
                Assert.AreEqual(HttpStatusCode.NotModified, dated.StatusCode);
                Assert.AreEqual(Policy("https://after.example.org"), dated.Headers.GetValues("Content-Security-Policy").Single());
            }
        }
        finally
        {
            await ExecuteNonQueryAsync("DELETE FROM [asap].[PatronEmbedAllowedOrigin] WHERE [OrganizationId]=1;");
        }
    }

    private sealed class EmbedOriginSqlProbe : DbCommandInterceptor
    {
        private int count;
        public int Count => Volatile.Read(ref count);
        public void Reset() => Interlocked.Exchange(ref count, 0);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[asap].[PatronEmbedAllowedOrigin]", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref count);
            }
            return ValueTask.FromResult(result);
        }
    }
}
