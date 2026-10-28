using System.Net;
using System.Text.Json;
using Asap.ReviewProviders;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task LocalReviewSessionIssuesNormalCookieAndRechecksSqlEligibilityAndAntiforgery()
    {
        // Exercise the production cookie/SQL authorization stack and the exact
        // session-issuance method used after the separate Docker runtime gates.
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        using var unavailable = await startup.PostAsync("/_review/auth/session", null);
        Assert.AreEqual(HttpStatusCode.NotFound, unavailable.StatusCode, "Referencing review tests must not activate review authentication in an ordinary host.");
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        long reviewerId;
        await using (var db = await contexts.CreateDbContextAsync())
        {
            using var source = typeof(PatronJourneyTests).Assembly.GetManifestResourceStream("Asap.Tests.LocalReviewer.sql")!;
            using var reader = new StreamReader(source);
            var seed = await reader.ReadToEndAsync();
            await db.Database.ExecuteSqlRawAsync(seed);
            await db.Database.ExecuteSqlRawAsync(seed); // Re-identifies the same row without resetting it.
            reviewerId = await db.StaffUsers.Where(item => item.NormalizedUserPrincipalName == "REVIEWER@EXAMPLE.ORG")
                .Select(item => item.Id).SingleAsync();
        }
        try
        {
            await using var application = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.AddTransient<IStartupFilter, ReviewSessionCoreTestFilter>()));
            using var client = application.CreateClient(new WebApplicationFactoryClientOptions {
                BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false
            });
            using var login = await client.PostAsync("/__test/review-session-core", null);
            Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
            var cookie = login.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("__Host-ASAP.Staff=", StringComparison.Ordinal));
            Assert.IsTrue(cookie.Contains("; secure", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(cookie.Contains("; httponly", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(cookie.Contains("; samesite=lax", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(cookie.Contains("expires=", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(client.DefaultRequestHeaders.Any(header => header.Key.StartsWith("X-ASAP-Test-", StringComparison.OrdinalIgnoreCase)));
            using var session = await client.GetAsync("/api/asap/staff/session");
            using var body = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
            Assert.IsTrue(body.RootElement.GetProperty("authenticated").GetBoolean());
            Assert.IsTrue(body.RootElement.GetProperty("accessAllowed").GetBoolean());
            var staff = body.RootElement.GetProperty("staff");
            Assert.AreEqual(reviewerId.ToString(), staff.GetProperty("id").GetString());
            Assert.AreEqual("super_admin", staff.GetProperty("role").GetString());
            Assert.AreEqual(1, staff.GetProperty("organizationId").GetInt32());
            using var queue = await client.GetAsync("/api/asap/staff/title-requests?scope=2&status=suggestion");
            Assert.AreEqual(HttpStatusCode.OK, queue.StatusCode);
            using var queueBody = JsonDocument.Parse(await queue.Content.ReadAsStringAsync());
            Assert.AreEqual("2", queueBody.RootElement.GetProperty("scope").GetString());
            using var noAntiforgery = await client.PostAsync("/api/asap/staff/sign-out", null);
            Assert.AreEqual(HttpStatusCode.BadRequest, noAntiforgery.StatusCode);
            using var signOutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/asap/staff/sign-out");
            signOutRequest.Headers.Add("X-ASAP-Antiforgery", body.RootElement.GetProperty("antiforgeryToken").GetString());
            using var signOut = await client.SendAsync(signOutRequest);
            Assert.AreEqual(HttpStatusCode.OK, signOut.StatusCode);
            using var newLogin = await client.PostAsync("/__test/review-session-core", null);
            Assert.AreEqual(HttpStatusCode.NoContent, newLogin.StatusCode);
            await using (var db = await contexts.CreateDbContextAsync())
            {
                var reviewer = await db.StaffUsers.SingleAsync(item => item.Id == reviewerId);
                reviewer.IsActive = false;
                await db.SaveChangesAsync();
            }
            using var revoked = await client.GetAsync("/api/asap/staff/title-requests?scope=2&status=suggestion");
            Assert.AreEqual(HttpStatusCode.Unauthorized, revoked.StatusCode);
            using var cannotReissue = await client.PostAsync("/__test/review-session-core", null);
            Assert.AreEqual(HttpStatusCode.Forbidden, cannotReissue.StatusCode);
            Assert.IsFalse(cannotReissue.Headers.Contains("Set-Cookie"));
            foreach (var role in new[] { StaffRole.Staff, StaffRole.Admin })
            {
                await using var db = await contexts.CreateDbContextAsync();
                var reviewer = await db.StaffUsers.SingleAsync(item => item.Id == reviewerId);
                reviewer.IsActive = true;
                reviewer.Role = role;
                reviewer.OrganizationId = 2;
                await db.SaveChangesAsync();
                using var wrongRole = await client.PostAsync("/__test/review-session-core", null);
                Assert.AreEqual(HttpStatusCode.Forbidden, wrongRole.StatusCode);
                Assert.IsFalse(wrongRole.Headers.Contains("Set-Cookie"));
            }
        }
        finally
        {
            await using var db = await contexts.CreateDbContextAsync();
            await db.StaffUsers.Where(item => item.Id == reviewerId).ExecuteDeleteAsync();
        }
    }

    // Test-only transport adapter to the real cookie-issuing method. No mock
    // authentication handler, hand-built cookie or database stand-in is used.
    private sealed class ReviewSessionCoreTestFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, following) =>
            {
                if (context.Request.Path != "/__test/review-session-core")
                {
                    await following(context);
                    return;
                }
                await ReviewAuthenticationEndpoint.CreateSessionAsync(context,
                    context.RequestServices.GetRequiredService<ExternalConfiguration>(), context.RequestAborted);
            });
            next(app);
        };
    }
}
