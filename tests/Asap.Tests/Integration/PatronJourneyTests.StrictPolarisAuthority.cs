using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task MalformedPinnedPatronScopeCannotCreateSessionOrReachSuggestionIntent()
    {
        const string barcode = "20000000003991";
        var handler = new StrictPatronIdentityHandler(
            "{\"PAPIErrorCode\":0,\"PatronID\":7001,\"AccessToken\":\"token\",\"AccessSecret\":\"secret\"}",
            "{\"PAPIErrorCode\":0,\"PatronBasicData\":{\"PatronID\":7001,\"Barcode\":\"20000000003991\",\"PatronOrgID\":\"2\",\"PatronCodeID\":1}}");
        var provider = await CreatePolarisProviderAsync(handler, "patron-login-authority-" + Guid.NewGuid().ToString("N"));
        await using var scoped = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.AddSingleton<IPatronProvider>(provider);
        }));
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        int requestsBefore;
        int outboxBefore;
        await using (var before = await contexts.CreateDbContextAsync())
        {
            requestsBefore = await before.TitleRequests.CountAsync();
            outboxBefore = await before.EmailOutbox.CountAsync();
        }

        using var client = scoped.CreateClient();
        var login = await client.PostAsJsonAsync("/api/asap/patron/login", new
        {
            barcode,
            pin = "1234",
            libraryOrgId = 2
        });
        Assert.AreEqual(HttpStatusCode.InternalServerError, login.StatusCode);
        var suggestion = await client.PostAsJsonAsync("/api/asap/patron/suggestions", new
        {
            format = "book",
            title = "Must not reach pickup intent",
            author = "Test Author",
            publication = "Coming soon",
            preferredPickupBranchId = 101
        });
        Assert.AreEqual(HttpStatusCode.Unauthorized, suggestion.StatusCode);
        Assert.AreEqual(2, handler.PatronOperationCalls);
        Assert.AreEqual(0, handler.OrganizationCalls);

        await using var verify = await contexts.CreateDbContextAsync();
        Assert.AreEqual(0, await verify.PatronSessions.CountAsync(item => item.Barcode == barcode));
        Assert.AreEqual(requestsBefore, await verify.TitleRequests.CountAsync());
        Assert.AreEqual(outboxBefore, await verify.EmailOutbox.CountAsync());
    }

    [TestMethod]
    [DataRow("Barcode")]
    [DataRow("FormerID")]
    public async Task OverlongPinnedPatronBarcodeCannotCreateSessionOrReachSuggestionIntent(string invalidField)
    {
        const string submittedBarcode = "current";
        var overlongBarcode = new string('7', 51);
        var aliasField = invalidField == "Barcode"
            ? $"\"Barcode\":\"{overlongBarcode}\""
            : $"\"Barcode\":\"current\",\"FormerID\":\"{overlongBarcode}\"";
        var handler = new StrictPatronIdentityHandler(
            "{\"PAPIErrorCode\":0,\"PatronID\":7001,\"AccessToken\":\"token\",\"AccessSecret\":\"secret\"}",
            $"{{\"PAPIErrorCode\":0,\"PatronBasicData\":{{\"PatronID\":7001,{aliasField},\"PatronOrgID\":2,\"PatronCodeID\":1}}}}");
        var provider = await CreatePolarisProviderAsync(handler, "patron-long-alias-" + Guid.NewGuid().ToString("N"));
        await using var scoped = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.AddSingleton<IPatronProvider>(provider);
        }));
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        int requestsBefore;
        int outboxBefore;
        await using (var before = await contexts.CreateDbContextAsync())
        {
            requestsBefore = await before.TitleRequests.CountAsync();
            outboxBefore = await before.EmailOutbox.CountAsync();
        }

        using var client = scoped.CreateClient();
        var login = await client.PostAsJsonAsync("/api/asap/patron/login", new
        {
            barcode = submittedBarcode,
            pin = "1234",
            libraryOrgId = 2
        });
        Assert.AreEqual(HttpStatusCode.InternalServerError, login.StatusCode);
        var suggestion = await client.PostAsJsonAsync("/api/asap/patron/suggestions", new
        {
            format = "book",
            title = "Must not reach pickup intent",
            author = "Test Author",
            publication = "Coming soon",
            preferredPickupBranchId = 101
        });
        Assert.AreEqual(HttpStatusCode.Unauthorized, suggestion.StatusCode);
        Assert.AreEqual(2, handler.PatronOperationCalls);
        Assert.AreEqual(0, handler.OrganizationCalls);

        await using var verify = await contexts.CreateDbContextAsync();
        Assert.AreEqual(0, await verify.PatronSessions.CountAsync(item => item.Barcode == submittedBarcode));
        Assert.AreEqual(requestsBefore, await verify.TitleRequests.CountAsync());
        Assert.AreEqual(outboxBefore, await verify.EmailOutbox.CountAsync());
    }


}
