using System.Net;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow("identifier")]
    [DataRow("bibs")]
    [DataRow("patrons")]
    [DataRow("organizations")]
    [DataRow("codes")]
    [DataRow("refresh")]
    [DataRow("identity")]
    [DataRow("holds")]
    [DataRow("checkouts")]
    [DataRow("holdings")]
    [DataRow("bib")]
    public async Task PolarisReadBoundaryPropagatesCapturedProgrammingDefects(string operation)
    {
        var defect = new ArgumentException("Broken request or adapter code.");
        var handler = new ProviderBoundaryHandler((_, _) => Task.FromException<HttpResponseMessage>(defect));
        var provider = await CreatePolarisProviderAsync(handler);
        var actual = await Assert.ThrowsExactlyAsync<ArgumentException>(() => operation switch
        {
            "identifier" => provider.LookupIdentifierAsync("9780000000001", 2, CancellationToken.None),
            "bibs" => provider.SearchBibsAsync("title", "Title", "", "", 2, CancellationToken.None),
            "patrons" => provider.SearchPatronsAsync("Name", 2, CancellationToken.None),
            "organizations" => provider.GetOrganizationsAsync(CancellationToken.None),
            "codes" => provider.GetPatronCodesAsync(CancellationToken.None),
            "refresh" => provider.RefreshAsync("20000000000001", 2, CancellationToken.None),
            "identity" => provider.GetPatronIdAsync("20000000000001", 2, CancellationToken.None),
            "holds" => provider.GetPatronHoldsAsync("20000000000001", 2, CancellationToken.None),
            "checkouts" => provider.GetPatronCheckoutsAsync("20000000000001", 2, CancellationToken.None),
            "holdings" => provider.GetBibHoldingsAsync(9001, 2, CancellationToken.None),
            "bib" => provider.ValidateBibAsync(9001, 2, CancellationToken.None),
            _ => throw new AssertFailedException("Unknown test operation.")
        });
        Assert.AreSame(defect, actual);
        Assert.AreEqual(1, handler.OperationCalls);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PolarisOptionalBibEnrichmentIgnoresOnlyExpectedFailures(bool programmingDefect)
    {
        var handler = new ProviderBoundaryHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/search/bibs/", StringComparison.Ordinal))
            {
                return Task.FromResult(BoundaryResponse("""{"PAPIErrorCode":1,"TotalRecordsFound":1,"BibSearchRows":[{"ControlNumber":9001,"Title":"Authoritative search title"}]}"""));
            }
            Assert.IsTrue(request.RequestUri.AbsolutePath.EndsWith("/bib/9001", StringComparison.Ordinal));
            return Task.FromException<HttpResponseMessage>(programmingDefect
                ? new ArgumentException("Broken enrichment") : new HttpRequestException("Detail transport unavailable"));
        });
        var provider = await CreatePolarisProviderAsync(handler);
        if (programmingDefect)
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.LookupIdentifierAsync("9780000000001", 2, CancellationToken.None));
        }
        else
        {
            var result = await provider.LookupIdentifierAsync("9780000000001", 2, CancellationToken.None);
            Assert.AreEqual(IdentifierLookupOutcome.Found, result.Outcome);
            Assert.AreEqual(9001, result.BibId);
            Assert.AreEqual("Authoritative search title", result.CatalogTitle);
        }
        Assert.AreEqual(4, handler.OperationCalls);
    }

    [TestMethod]
    [DataRow("create", "transport")]
    [DataRow("reply", "transport")]
    [DataRow("create", "malformed")]
    [DataRow("reply", "malformed")]
    [DataRow("create", "http")]
    [DataRow("reply", "http")]
    public async Task PolarisHoldBoundaryRetainsUncertaintyAfterPossibleDispatch(string operation, string failure)
    {
        var handler = new ProviderBoundaryHandler((_, _) => failure == "transport"
            ? Task.FromException<HttpResponseMessage>(new HttpRequestException("Disconnected after send"))
            : Task.FromResult(BoundaryResponse("not-json", failure == "http" ? HttpStatusCode.BadGateway : HttpStatusCode.OK)));
        var provider = await CreatePolarisProviderAsync(handler);
        var result = operation == "create"
            ? await provider.CreateHoldAsync(BoundaryCreate(), CancellationToken.None)
            : await provider.ReplyToHoldAsync(BoundaryReply(), CancellationToken.None);
        Assert.AreEqual(HoldProviderOutcome.Ambiguous, result.Outcome);
        Assert.AreEqual(1, handler.OperationCalls);
    }

    [TestMethod]
    [DataRow("create", false)]
    [DataRow("reply", false)]
    [DataRow("pickup", false)]
    [DataRow("create", true)]
    [DataRow("reply", true)]
    [DataRow("pickup", true)]
    public async Task PolarisMutationBoundaryPropagatesProgrammingDefectsAndCallerCancellation(string operation, bool invalidOperation)
    {
        Exception defect = invalidOperation ? new InvalidOperationException("Broken mutation code") : new ArgumentException("Broken mutation code");
        var handler = new ProviderBoundaryHandler((_, _) => Task.FromException<HttpResponseMessage>(defect));
        var provider = await CreatePolarisProviderAsync(handler);
        var actual = await Assert.ThrowsAsync<Exception>(() => BoundaryMutationAsync(provider, operation, CancellationToken.None));
        Assert.AreSame(defect, actual);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => BoundaryMutationAsync(provider, operation, cancelled.Token));
        Assert.AreEqual(1, handler.OperationCalls, "Pre-cancelled calls must not dispatch another request.");
    }

    [TestMethod]
    [DataRow("create")]
    [DataRow("reply")]
    [DataRow("pickup")]
    public async Task PolarisMutationCallerCancellationDuringSendPropagates(string operation)
    {
        using var cancelled = new CancellationTokenSource();
        var handler = new ProviderBoundaryHandler(async (_, token) =>
        {
            cancelled.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            throw new AssertFailedException("Cancelled transport cannot return a response.");
        });
        var provider = await CreatePolarisProviderAsync(handler);
        await Assert.ThrowsAsync<OperationCanceledException>(() => BoundaryMutationAsync(provider, operation, cancelled.Token));
        Assert.AreEqual(1, handler.OperationCalls);
    }

    [TestMethod]
    [DataRow("patrons", "transport")]
    [DataRow("identity", "transport")]
    [DataRow("pickup", "transport")]
    [DataRow("patrons", "http")]
    [DataRow("identity", "http")]
    [DataRow("pickup", "http")]
    [DataRow("patrons", "malformed")]
    [DataRow("identity", "malformed")]
    [DataRow("pickup", "malformed")]
    [DataRow("patrons", "unusable")]
    [DataRow("identity", "unusable")]
    [DataRow("pickup", "unusable")]
    [DataRow("patrons", "programming")]
    [DataRow("identity", "programming")]
    [DataRow("pickup", "programming")]
    public async Task PolarisStaffAuthenticationFailurePreservesItsCauseBeforeTheOperation(string operation, string failure)
    {
        var defect = new ArgumentException("Broken staff authentication code");
        var handler = new ProviderBoundaryHandler((_, _) => throw new AssertFailedException("Failed authentication must not dispatch the mutation."))
        {
            Authenticate = (_, _) => failure switch
            {
                "programming" => Task.FromException<HttpResponseMessage>(defect),
                "transport" => Task.FromException<HttpResponseMessage>(new HttpRequestException("Staff authentication disconnected")),
                "http" => Task.FromResult(BoundaryResponse("{}", HttpStatusCode.Unauthorized)),
                "malformed" => Task.FromResult(BoundaryResponse("not-json")),
                "unusable" => Task.FromResult(BoundaryResponse("""{"PAPIErrorCode":0,"AccessToken":"","AccessSecret":""}""")),
                _ => throw new AssertFailedException("Unknown authentication failure.")
            }
        };
        // The package caches protected tokens across client instances. Use distinct
        // deterministic credentials so a prior valid fixture cannot skip this send.
        var provider = await CreatePolarisProviderAsync(handler, $"authentication-boundary-{operation}-{failure}");
        Task Call() => operation switch
        {
            "pickup" => BoundaryMutationAsync(provider, operation, CancellationToken.None),
            "patrons" => provider.SearchPatronsAsync("Name", 2, CancellationToken.None),
            "identity" => provider.GetPatronIdAsync("20000000000001", 2, CancellationToken.None),
            _ => throw new AssertFailedException("Unknown authenticated operation.")
        };
        var error = await Assert.ThrowsAsync<Exception>(Call);
        Exception? cause = error;
        if (operation == "pickup")
        {
            Assert.IsInstanceOfType<PolarisMutationNotDispatchedException>(error);
            cause = error.InnerException;
        }
        if (failure == "programming")
        {
            Assert.AreSame(defect, cause);
        }
        else
        {
            Assert.IsInstanceOfType<PolarisOperationalException>(cause);
        }
        Assert.AreEqual(0, handler.OperationCalls);
        Assert.AreEqual(1, handler.AuthenticationCalls);
    }

    [TestMethod]
    [DataRow("\"RequestPickupBranchID\":-1")]
    [DataRow("\"RequestPickupBranchID\":null")]
    [DataRow("\"RequestPickupBranchID\":\"200\"")]
    [DataRow("\"RequestPickupBranchID\":0,\"requestpickupbranchid\":200")]
    public async Task PolarisPreferredPickupRejectsUndocumentedOrAmbiguousValues(string field)
    {
        var provider = await CreatePolarisProviderAsync(new ProviderBoundaryHandler((request, _) =>
            Task.FromResult(BoundaryResponse(request.RequestUri!.AbsolutePath.Contains("/organizations/", StringComparison.Ordinal)
                ? """{"PAPIErrorCode":1,"OrganizationsGetRows":[{"OrganizationID":300,"OrganizationCodeID":2,"DisplayName":"Registered Branch"}]}"""
                : "{\"PAPIErrorCode\":0,\"PatronBasicData\":{\"PatronID\":123,\"Barcode\":\"20000000000001\",\"PatronOrgID\":300," + field + "}}"))));
        var failure = await Assert.ThrowsExactlyAsync<PolarisOperationalException>(() => provider.RefreshAsync("20000000000001", 2, CancellationToken.None));
        Assert.IsTrue(failure.Code is "polaris_pickup_preference_invalid" or "polaris_patron_protocol_failed", failure.Code);
    }

    [TestMethod]
    [DataRow("create", 1)]
    [DataRow("create", 0)]
    [DataRow("reply", 1)]
    [DataRow("reply", 0)]
    public async Task PolarisHoldLocalContextFailureHasPositiveNoDispatchEvidence(string operation, int organizationId)
    {
        var handler = new ProviderBoundaryHandler((_, _) => throw new AssertFailedException("Invalid context must not reach HTTP."));
        var provider = await CreatePolarisProviderAsync(handler);
        var failure = await Assert.ThrowsExactlyAsync<PolarisMutationNotDispatchedException>(() => operation == "create"
            ? provider.CreateHoldAsync(BoundaryCreate() with { RequestingOrganizationId = organizationId }, CancellationToken.None)
            : provider.ReplyToHoldAsync(BoundaryReply() with { RequestingOrganizationId = organizationId }, CancellationToken.None));
        Assert.IsInstanceOfType<PolarisOperationalException>(failure.InnerException);
        Assert.AreEqual(0, handler.OperationCalls);
    }

    [TestMethod]
    public async Task PolarisHoldRejectsMagicPickupAndEmptyReplyIdentityBeforeDispatch()
    {
        var handler = new ProviderBoundaryHandler((_, _) => throw new AssertFailedException("Invalid identity must not reach HTTP."));
        var provider = await CreatePolarisProviderAsync(handler);
        foreach (var command in new[] { BoundaryCreate() with { PickupBranchId = 0 }, BoundaryCreate() with { PickupBranchId = 1 }, BoundaryCreate() with { WorkstationId = 0 } })
        {
            var error = await Assert.ThrowsExactlyAsync<PolarisMutationNotDispatchedException>(() => provider.CreateHoldAsync(command, CancellationToken.None));
            Assert.IsInstanceOfType<ArgumentException>(error.InnerException);
        }
        var reply = await Assert.ThrowsExactlyAsync<PolarisMutationNotDispatchedException>(() => provider.ReplyToHoldAsync(BoundaryReply() with { RequestGuid = Guid.Empty }, CancellationToken.None));
        Assert.IsInstanceOfType<ArgumentException>(reply.InnerException);
        Assert.AreEqual(0, handler.OperationCalls);
    }

    [TestMethod]
    [DataRow(-3000)]
    [DataRow(-3622)]
    [DataRow(-3400)]
    public async Task PolarisPickupBoundaryDistinguishesDocumentedValidationFromUncertainFailure(int code)
    {
        var provider = await CreatePolarisProviderAsync(new ProviderBoundaryHandler((_, _) =>
            Task.FromResult(BoundaryResponse(JsonSerializer.Serialize(new { PAPIErrorCode = code })))));
        if (code is -3000 or -3622)
        {
            var rejected = await Assert.ThrowsExactlyAsync<PolarisPickupRejectedException>(() => provider.UpdatePreferredPickupBranchAsync("20000000000001", 101, 2, CancellationToken.None));
            Assert.AreEqual(code, rejected.PapiErrorCode);
        }
        else
        {
            await Assert.ThrowsExactlyAsync<PolarisOperationalException>(() => provider.UpdatePreferredPickupBranchAsync("20000000000001", 101, 2, CancellationToken.None));
        }
    }

    [TestMethod]
    [DataRow("create", false)]
    [DataRow("reply", false)]
    [DataRow("create", true)]
    [DataRow("reply", true)]
    public async Task HoldJournalClosesLocalNoDispatchFailureWithoutOperatorRecovery(string phase, bool programmingDefect)
    {
        var patron = new PickupJournalProvider(3485);
        var holds = ScriptedHoldProvider.ReplyRequiredThenSuccess();
        Exception cause = programmingDefect ? new ArgumentException("Broken hold arguments")
            : new PolarisOperationalException("polaris_configuration_incomplete", "Credentials changed before dispatch.");
        var failure = new PolarisMutationNotDispatchedException(cause);
        if (phase == "create")
        {
            holds.CreateException = failure;
        }
        else
        {
            holds.ReplyException = failure;
        }
        await using var scoped = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton<IPatronProvider>(patron);
            services.AddSingleton<IStaffPolarisProvider>(holds);
        }));
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var request = await SeedPickupJournalRequestAsync(contexts, patron);
        try
        {
            var actor = await ReadConfiguredSuperAdminAsync();
            var service = scoped.Services.GetRequiredService<HoldPlacementService>();
            Task<HoldPlacementResult> Place() => service.PlaceAsync(actor, request.Id, new VersionInput(StaffVersion.Encode(request.RowVersion)), CancellationToken.None);
            if (programmingDefect)
            {
                Assert.AreSame(cause, await Assert.ThrowsExactlyAsync<ArgumentException>(Place));
            }
            else
            {
                Assert.AreEqual("hold_provider_error", (await Place()).Code);
            }
            await using var verify = await contexts.CreateDbContextAsync();
            var operation = await verify.HoldPlacementOperations.AsNoTracking().SingleAsync(row => row.TitleRequestId == request.Id);
            Assert.AreEqual("failed", operation.State);
            Assert.AreEqual(phase + "_started", operation.Phase);
            Assert.AreEqual("local_not_dispatched", operation.ResultCode);
            Assert.AreEqual("provider_transport_not_entered", operation.OutcomeEvidenceKind);
            Assert.IsNotNull(operation.CreateStartedUtc, "Durable intent must not be deleted or rewritten as never marked.");
            Assert.AreEqual(phase == "reply", operation.ReplyStartedUtc.HasValue);
            Assert.IsNotNull(operation.CompletedUtc);
            Assert.IsNull(operation.OwnerToken);
            Assert.IsNull(operation.LeaseExpiresUtc);
            Assert.AreEqual("pending_hold", (await verify.TitleRequests.SingleAsync(row => row.Id == request.Id)).Status);
            await service.RecoverBackgroundOperationAsync(operation.Id, patron.OrganizationId, CancellationToken.None);
            Assert.AreEqual(1, holds.CreateCount);
            Assert.AreEqual(phase == "reply" ? 1 : 0, holds.ReplyCount, "Recovery must not replay the failed operation.");
        }
        finally
        {
            await CleanupPickupRemediationAsync(patron.OrganizationId);
        }
    }

    private static HoldCreateCommand BoundaryCreate() => new(7001, 9001, 101, 2, 99, 42);
    private static HoldReplyCommand BoundaryReply() => new(Guid.Parse("bc3ed7d8-cc9b-40e0-a285-8bb0f688fa10"), "group", "qualifier", 2);
    private static Task BoundaryMutationAsync(PolarisPatronProvider provider, string operation, CancellationToken token) => operation switch
    {
        "create" => provider.CreateHoldAsync(BoundaryCreate(), token),
        "reply" => provider.ReplyToHoldAsync(BoundaryReply(), token),
        "pickup" => provider.UpdatePreferredPickupBranchAsync("20000000000001", 101, 2, token),
        _ => throw new AssertFailedException("Unknown test operation.")
    };
    private static HttpResponseMessage BoundaryResponse(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json) };

    private sealed class ProviderBoundaryHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int OperationCalls { get; private set; }
        public int AuthenticationCalls { get; private set; }
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Authenticate { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri!.AbsolutePath.Contains("/authenticator/staff", StringComparison.Ordinal))
            {
                AuthenticationCalls++;
                if (Authenticate is not null)
                {
                    return Authenticate(request, cancellationToken);
                }
                return Task.FromResult(BoundaryResponse("""{"PAPIErrorCode":0,"AccessToken":"boundary-token","AccessSecret":"boundary-secret","AuthExpDate":"2030-01-01T00:00:00Z"}"""));
            }
            OperationCalls++;
            return respond(request, cancellationToken);
        }
    }
}
