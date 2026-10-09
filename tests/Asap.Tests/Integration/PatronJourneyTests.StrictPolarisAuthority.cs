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
    [DataRow("missing", "{\"PAPIErrorCode\":0}")]
    [DataRow("wrong_kind", "{\"PAPIErrorCode\":\"0\",\"PatronItemsOutGetRows\":[]}")]
    [DataRow("bad_row", "{\"PAPIErrorCode\":0,\"PatronItemsOutGetRows\":[{\"BibID\":\"9001\"}]}")]
    [DataRow("duplicate_id", "{\"PAPIErrorCode\":0,\"PatronItemsOutGetRows\":[{\"BibID\":9001,\"bibid\":9002}]}")]
    [DataRow("typed_rows_defaulted", "{\"PAPIErrorCode\":0,\"PatronItemsOutGetRows\":null}")]
    public async Task PolarisPinnedSerializerRejectsMalformedCheckoutIdentity(string _, string content)
    {
        var handler = new ProviderBoundaryHandler((_, _) => Task.FromResult(BoundaryResponse(content)));
        var provider = await CreatePolarisProviderAsync(handler, "checkout-" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(() => provider.GetPatronCheckoutsAsync(
            "20000000000001", 2, CancellationToken.None));

        Assert.AreEqual(1, handler.OperationCalls);
    }

    [TestMethod]
    public async Task PolarisPinnedSerializerAcceptsOnlyExactCheckoutModelIdentity()
    {
        var handler = new ProviderBoundaryHandler((_, _) => Task.FromResult(BoundaryResponse(
            "{\"PAPIErrorCode\":0,\"PatronItemsOutGetRows\":[{\"BibID\":9001}]}")));
        var provider = await CreatePolarisProviderAsync(handler, "checkout-valid-" + Guid.NewGuid().ToString("N"));

        var result = await provider.GetPatronCheckoutsAsync("20000000000001", 2, CancellationToken.None);

        Assert.AreEqual(9001, result.Single().BibId);
        Assert.AreEqual(1, handler.OperationCalls);
    }

    [TestMethod]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronID\":7001,\"AccessToken\":\"token\",\"AccessSecret\":\"secret\"}",
        "{\"PAPIErrorCode\":0,\"PatronBasicData\":{\"PatronID\":7002,\"Barcode\":\"current\",\"PatronOrgID\":2}}", 2)]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronID\":7001,\"patronid\":7002,\"AccessToken\":\"token\",\"AccessSecret\":\"secret\"}",
        "{\"PAPIErrorCode\":0,\"PatronBasicData\":{\"PatronID\":7001,\"Barcode\":\"current\",\"PatronOrgID\":2}}", 1)]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronID\":\"7001\",\"AccessToken\":\"token\",\"AccessSecret\":\"secret\"}",
        "{\"PAPIErrorCode\":0,\"PatronBasicData\":{\"PatronID\":7001,\"Barcode\":\"current\",\"PatronOrgID\":2}}", 1)]
    public async Task PolarisPinnedSerializerRejectsMissingOrMismatchedAuthenticatedPatronIdentity(
        string authentication,
        string basicData,
        int expectedOperationCalls)
    {
        var handler = new StrictPatronIdentityHandler(authentication, basicData);
        var provider = await CreatePolarisProviderAsync(handler, "auth-identity-" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(() => provider.AuthenticateAsync(
            "submitted-login", "1234", CancellationToken.None));

        Assert.AreEqual(expectedOperationCalls, handler.PatronOperationCalls);
        Assert.AreEqual(0, handler.OrganizationCalls, "Invalid native patron identity must fail before session context is loaded.");
    }

    [TestMethod]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronBasicData\":{\"PatronID\":7001,\"Barcode\":\"current\",\"PatronOrgID\":\"2\",\"PatronCodeID\":1}}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronBasicData\":{\"PatronID\":7001,\"Barcode\":\"current\",\"PatronOrgID\":true,\"PatronCodeID\":1}}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronBasicData\":{\"PatronID\":7001,\"Barcode\":\"current\",\"PatronOrgID\":2,\"patronorgid\":3,\"PatronCodeID\":1}}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronBasicData\":{\"PatronID\":7001,\"Barcode\":\"current\",\"PatronCodeID\":1}}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronBasicData\":{\"PatronID\":7001,\"Barcode\":\"current\",\"PatronOrgID\":2,\"PatronCodeID\":\"1\"}}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronBasicData\":{\"PatronID\":7001,\"Barcode\":\"current\",\"PatronOrgID\":2,\"PatronCodeID\":true}}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronBasicData\":{\"PatronID\":7001,\"Barcode\":\"current\",\"PatronOrgID\":2,\"PatronCodeID\":1,\"patroncodeid\":2}}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronBasicData\":{\"PatronID\":7001,\"Barcode\":\"current\",\"PatronOrgID\":2,\"PatronCodeID\":null}}")]
    public async Task PolarisPinnedSerializerRejectsCoercedOrMissingPatronScopeAndEligibilityIds(string basicData)
    {
        var handler = new StrictPatronIdentityHandler(
            "{\"PAPIErrorCode\":0,\"PatronID\":7001,\"AccessToken\":\"token\",\"AccessSecret\":\"secret\"}",
            basicData);
        var provider = await CreatePolarisProviderAsync(handler, "patron-authority-" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(() => provider.AuthenticateAsync(
            "current", "1234", CancellationToken.None));

        Assert.AreEqual(2, handler.PatronOperationCalls);
        Assert.AreEqual(0, handler.OrganizationCalls,
            "Malformed raw scope or eligibility evidence must fail before organization context is loaded.");
    }

    [TestMethod]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronBasicData\":{\"PatronID\":7001,\"Barcode\":\"current\",\"PatronOrgID\":\"2\",\"PatronCodeID\":1}}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronBasicData\":{\"PatronID\":7001,\"Barcode\":\"current\",\"PatronOrgID\":2,\"PatronCodeID\":\"1\"}}")]
    public async Task PolarisRefreshRejectsCoercedPatronScopeAndEligibilityBeforeHierarchyLoad(string basicData)
    {
        var handler = new StrictPatronIdentityHandler("{}", basicData);
        var provider = await CreatePolarisProviderAsync(handler, "patron-refresh-authority-" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(() => provider.RefreshAsync(
            "current", 2, CancellationToken.None));

        Assert.AreEqual(1, handler.PatronOperationCalls);
        Assert.AreEqual(0, handler.OrganizationCalls);
    }

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

    [TestMethod]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronBasicData\":{\"PatronID\":7001,\"Barcode\":\"current\",\"PatronOrgID\":2}}", null)]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronBasicData\":{\"PatronID\":7001,\"Barcode\":\"current\",\"PatronOrgID\":2,\"PatronCodeID\":-1}}", null)]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronBasicData\":{\"PatronID\":7001,\"Barcode\":\"current\",\"PatronOrgID\":2,\"PatronCodeID\":99}}", 99)]
    public async Task PolarisPinnedSerializerPreservesExistingOptionalPatronCodePolicy(string basicData, int? expectedCode)
    {
        var handler = new StrictPatronIdentityHandler(
            "{\"PAPIErrorCode\":0,\"PatronID\":7001,\"AccessToken\":\"token\",\"AccessSecret\":\"secret\"}",
            basicData,
            "{\"PAPIErrorCode\":0,\"OrganizationsGetRows\":[{\"OrganizationID\":1,\"OrganizationCodeID\":1},{\"OrganizationID\":2,\"OrganizationCodeID\":2}]}");
        var provider = await CreatePolarisProviderAsync(handler, "patron-code-policy-" + Guid.NewGuid().ToString("N"));

        var patron = await provider.AuthenticateAsync("current", "1234", CancellationToken.None);

        Assert.AreEqual(expectedCode, patron.PatronCodeId);
        Assert.AreEqual(1, handler.OrganizationCalls);
    }

    [TestMethod]
    public async Task PolarisRefreshAcceptsPrimaryOrDocumentedFormerBarcodeOnly()
    {
        var handler = new StrictPatronIdentityHandler(
            "{}",
            "{\"PAPIErrorCode\":0,\"PatronBasicData\":{\"PatronID\":7001,\"Barcode\":\"current\",\"FormerID\":\"former\",\"PatronOrgID\":2}}",
            "{\"PAPIErrorCode\":1,\"OrganizationsGetRows\":[{\"OrganizationID\":2,\"OrganizationCodeID\":2,\"Name\":\"Library\"}]}" );
        var provider = await CreatePolarisProviderAsync(handler, "former-barcode-" + Guid.NewGuid().ToString("N"));

        var patron = await provider.RefreshAsync("former", 2, CancellationToken.None);

        Assert.AreEqual(7001, patron.PatronId);
        Assert.AreEqual("current", patron.Barcode);
        Assert.AreEqual(1, handler.OrganizationCalls);
    }

    [TestMethod]
    public async Task PolarisRefreshRejectsBarcodeOutsidePrimaryAndFormerAliasesBeforeHierarchyLoad()
    {
        var handler = new StrictPatronIdentityHandler(
            "{}",
            "{\"PAPIErrorCode\":0,\"PatronBasicData\":{\"PatronID\":7001,\"Barcode\":\"current\",\"FormerID\":\"former\",\"PatronOrgID\":2}}",
            "{}");
        var provider = await CreatePolarisProviderAsync(handler, "bad-barcode-" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(() => provider.RefreshAsync(
            "unrelated-login", 2, CancellationToken.None));

        Assert.AreEqual(0, handler.OrganizationCalls);
    }

    [TestMethod]
    [DataRow("{\"PAPIErrorCode\":0,\"OrganizationsGetRows\":[{\"OrganizationID\":\"2\",\"OrganizationCodeID\":2}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"OrganizationsGetRows\":[{\"OrganizationID\":0,\"OrganizationCodeID\":2}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"OrganizationsGetRows\":[{\"OrganizationID\":2,\"organizationid\":2,\"OrganizationCodeID\":2}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"OrganizationsGetRows\":[{\"OrganizationID\":2,\"OrganizationCodeID\":\"2\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"OrganizationsGetRows\":[{\"OrganizationID\":2,\"OrganizationCodeID\":2,\"organizationcodeid\":3}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"OrganizationsGetRows\":[{\"OrganizationID\":2,\"OrganizationCodeID\":null}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"OrganizationsGetRows\":[{\"OrganizationID\":3,\"OrganizationCodeID\":3,\"ParentOrganizationID\":\"2\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"OrganizationsGetRows\":[{\"OrganizationID\":3,\"OrganizationCodeID\":3,\"ParentOrganizationID\":2,\"parentorganizationid\":4}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"OrganizationsGetRows\":[{\"OrganizationID\":2,\"OrganizationCodeID\":2},{\"OrganizationID\":2,\"OrganizationCodeID\":3}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"papierrorcode\":0,\"OrganizationsGetRows\":[{\"OrganizationID\":2,\"OrganizationCodeID\":2}]}")]
    public async Task PolarisPinnedSerializerRejectsMalformedOrganizationAuthorityRows(string content)
    {
        var handler = new ProviderBoundaryHandler((_, _) => Task.FromResult(BoundaryResponse(content)));
        var provider = await CreatePolarisProviderAsync(handler, "organizations-protocol-" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(() => provider.GetOrganizationsAsync(CancellationToken.None));

        Assert.AreEqual(1, handler.OperationCalls);
    }

    [TestMethod]
    public async Task PolarisPinnedSerializerPreservesSparseOrganizationNamesAndMissingAuthorityFields()
    {
        var handler = new ProviderBoundaryHandler((_, _) => Task.FromResult(BoundaryResponse(
            "{\"PAPIErrorCode\":2,\"OrganizationsGetRows\":[{\"OrganizationID\":1,\"ParentOrganizationID\":null},{\"OrganizationID\":2,\"Name\":\"Library\",\"ParentOrganizationID\":1}]}")));
        var provider = await CreatePolarisProviderAsync(handler, "organizations-sparse-" + Guid.NewGuid().ToString("N"));

        var rows = await provider.GetOrganizationsAsync(CancellationToken.None);

        Assert.AreEqual(2, rows.Count);
        Assert.IsNull(rows[0].OrganizationCodeId);
        Assert.IsNull(rows[0].ParentOrganizationId);
        Assert.AreEqual("Library", rows[1].DisplayName);
        Assert.IsNull(rows[1].OrganizationCodeId, "A missing native code must remain unclassified.");
        Assert.AreEqual(1, rows[1].ParentOrganizationId);
        Assert.AreEqual(1, handler.OperationCalls);
    }

    [TestMethod]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronCodesRows\":[{\"PatronCodeID\":\"2\",\"Description\":\"Adult\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronCodesRows\":[{\"PatronCodeID\":2,\"patroncodeid\":3,\"Description\":\"Adult\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronCodesRows\":[{\"PatronCodeID\":2,\"Description\":\"Adult\"},{\"PatronCodeID\":2,\"Description\":\"Other\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronCodesRows\":[{\"PatronCodeID\":0,\"Description\":\"Adult\"}]}")]
    public async Task PolarisPinnedSerializerRejectsMalformedPatronCodeIdentities(string content)
    {
        var handler = new ProviderBoundaryHandler((_, _) => Task.FromResult(BoundaryResponse(content)));
        var provider = await CreatePolarisProviderAsync(handler, "patron-codes-protocol-" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(() => provider.GetPatronCodesAsync(CancellationToken.None));

        Assert.AreEqual(1, handler.OperationCalls);
    }

    [TestMethod]
    [DataRow("{\"PAPIErrorCode\":0,\"StatusType\":3,\"StatusValue\":5,\"RequestGUID\":\"8a008457-62b1-4ea3-b6c2-56f24258efb4\",\"TxnGroupQualifer\":\"legacy\",\"TxnQualifier\":\"q\"}", "legacy")]
    [DataRow("{\"PAPIErrorCode\":0,\"StatusType\":3,\"StatusValue\":5,\"RequestGUID\":\"8a008457-62b1-4ea3-b6c2-56f24258efb4\",\"TxnGroupQualifier\":\"documented\",\"TxnQualifier\":\"q\"}", "documented")]
    [DataRow("{\"PAPIErrorCode\":0,\"StatusType\":3,\"StatusValue\":5,\"RequestGUID\":\"8a008457-62b1-4ea3-b6c2-56f24258efb4\",\"TxnGroupQualifer\":\"same\",\"TxnGroupQualifier\":\"same\",\"TxnQualifier\":\"q\"}", "same")]
    [DataRow("{\"PAPIErrorCode\":0,\"StatusType\":3,\"StatusValue\":5,\"RequestGUID\":\"8a008457-62b1-4ea3-b6c2-56f24258efb4\",\"TxnGroupQualifier\":\"same\",\"TxnGroupQualifer\":\"same\",\"TxnQualifier\":\"q\"}", "same")]
    public async Task PolarisPinnedSerializerAcceptsDocumentedHoldQualifierAliasesWithCanonicalPrecedence(
        string content,
        string expectedGroup)
    {
        var handler = new ProviderBoundaryHandler((_, _) => Task.FromResult(BoundaryResponse(content)));
        var provider = await CreatePolarisProviderAsync(handler, "hold-alias-" + Guid.NewGuid().ToString("N"));

        var result = await provider.CreateHoldAsync(BoundaryCreate(), CancellationToken.None);

        Assert.AreEqual(HoldProviderOutcome.ReplyRequired, result.Outcome);
        Assert.AreEqual(Guid.Parse("8a008457-62b1-4ea3-b6c2-56f24258efb4"), result.RequestGuid);
        Assert.AreEqual(expectedGroup, result.TxnGroupQualifier);
        Assert.AreEqual(1, handler.OperationCalls);
    }

    [TestMethod]
    [DataRow("{\"PAPIErrorCode\":0,\"StatusType\":3,\"StatusValue\":5,\"TxnGroupQualifer\":\"legacy\",\"TxnQualifier\":\"q\"}")]
    [DataRow("{\"PAPIErrorCode\":0,\"StatusType\":3,\"StatusValue\":5,\"RequestGUID\":\"not-a-guid\",\"TxnGroupQualifer\":\"legacy\",\"TxnQualifier\":\"q\"}")]
    [DataRow("{\"PAPIErrorCode\":0,\"StatusType\":3,\"StatusValue\":5,\"RequestGUID\":\"8a008457-62b1-4ea3-b6c2-56f24258efb4\",\"TxnGroupQualifer\":17,\"TxnQualifier\":\"q\"}")]
    [DataRow("{\"PAPIErrorCode\":0,\"StatusType\":3,\"StatusValue\":5,\"RequestGUID\":\"8a008457-62b1-4ea3-b6c2-56f24258efb4\",\"TxnGroupQualifer\":\"legacy\",\"TxnQualifier\":17}")]
    [DataRow("{\"PAPIErrorCode\":0,\"StatusType\":3,\"StatusValue\":5,\"RequestGUID\":\"8a008457-62b1-4ea3-b6c2-56f24258efb4\",\"TxnGroupQualifer\":\"legacy\",\"TxnGroupQualifer\":\"other\",\"TxnQualifier\":\"q\"}")]
    [DataRow("{\"PAPIErrorCode\":0,\"StatusType\":3,\"StatusValue\":5,\"RequestGUID\":\"8a008457-62b1-4ea3-b6c2-56f24258efb4\",\"TxnGroupQualifer\":\"model-value\",\"TxnGroupQualifier\":\"other-value\",\"TxnQualifier\":\"q\"}")]
    public async Task PolarisPinnedSerializerKeepsMalformedHoldConversationAmbiguous(string content)
    {
        var handler = new ProviderBoundaryHandler((_, _) => Task.FromResult(BoundaryResponse(content)));
        var provider = await CreatePolarisProviderAsync(handler, "hold-bad-identity-" + Guid.NewGuid().ToString("N"));

        var result = await provider.CreateHoldAsync(BoundaryCreate(), CancellationToken.None);

        Assert.AreEqual(HoldProviderOutcome.Ambiguous, result.Outcome);
        Assert.AreEqual(1, handler.OperationCalls, "A malformed post-dispatch response remains one possibly effective call.");
    }

    [TestMethod]
    [DataRow("{\"PAPIErrorCode\":\"0\"}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PAPIErrorCode\":0}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PAPIErrorCode\":\"0\"}")]
    public async Task PolarisPickupPutTreatsMalformedSuccessCodeAsAmbiguous(string content)
    {
        var handler = new ProtectedUpdateResponseHandler(content);
        var provider = await CreatePolarisProviderAsync(handler, "pickup-protocol-" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(() => provider.UpdatePreferredPickupBranchAsync(
            "20000000000001", 101, 2, CancellationToken.None));

        Assert.AreEqual(1, handler.UpdateRequestCount);
    }

    private sealed class StrictPatronIdentityHandler(
        string authentication,
        string basicData,
        string organizations = "{}") : HttpMessageHandler
    {
        private const string ProtectedToken =
            "{\"PAPIErrorCode\":0,\"AccessToken\":\"protected-token\",\"AccessSecret\":\"protected-secret\",\"AuthExpDate\":\"2030-01-01T00:00:00Z\"}";

        public int PatronOperationCalls { get; private set; }
        public int OrganizationCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            string content;
            if (path.Contains("/authenticator/staff", StringComparison.Ordinal))
            {
                content = ProtectedToken;
            }
            else if (path.Contains("/authenticator/patron", StringComparison.Ordinal))
            {
                PatronOperationCalls++;
                content = authentication;
            }
            else if (path.EndsWith("/basicdata", StringComparison.Ordinal))
            {
                PatronOperationCalls++;
                content = basicData;
            }
            else if (path.Contains("/organizations/", StringComparison.Ordinal))
            {
                OrganizationCalls++;
                content = organizations;
            }
            else
            {
                throw new AssertFailedException("Unexpected production Polaris request: " + path);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content),
                RequestMessage = request
            });
        }
    }
}
