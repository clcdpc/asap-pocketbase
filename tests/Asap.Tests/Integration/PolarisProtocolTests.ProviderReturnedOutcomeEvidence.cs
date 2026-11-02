using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Testing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Asap.Tests.Integration;

public sealed partial class PolarisProtocolTests
{
    [TestMethod]
    [DataRow("create", "success", "operation-canceled")]
    [DataRow("create", "no-effect", "http-request")]
    [DataRow("create", "reply-required", "argument")]
    [DataRow("reply", "success", "http-request")]
    [DataRow("reply", "no-effect", "operation-canceled")]
    public async Task PolarisParsedHoldOutcomeSurvivesPostParseDisposeFault(
        string operation, string outcome, string faultKind)
    {
        var response = HoldOutcomeResponse(outcome);
        var mutationMethod = operation == "create" ? HttpMethod.Post : HttpMethod.Put;
        var baselineCleanupFault = CreateResponseDisposeFault(faultKind);
        var baselineCleanupLogger = new CapturingProviderCleanupLogger();
        var baselineHandler = new DisposeBoundaryHoldHandler(
            mutationMethod, response, throwOnResponseDispose: baselineCleanupFault);
        var baselineProvider = await CreatePolarisProviderAsync(
            baselineHandler, $"hold-return-control-{operation}-{outcome}-" + Guid.NewGuid().ToString("N"),
            baselineCleanupLogger);
        var baseline = await CallHoldOutcomeAsync(baselineProvider, operation, CancellationToken.None);

        Assert.AreEqual(1, baselineHandler.MutationRequestCount,
            "The uncanceled control must issue one hold mutation.");
        Assert.AreEqual(mutationMethod, baselineHandler.MutationRequestMethod);
        Assert.IsFalse(string.IsNullOrWhiteSpace(baselineHandler.MutationRequestBody));
        Assert.IsTrue(baselineHandler.MutationResponseContentDisposed);
        Assert.AreEqual(1, baselineHandler.MutationResponseContent?.DisposeCount ?? 0);
        AssertResponseCleanupDiagnostic(baselineCleanupLogger, baselineCleanupFault);

        using var cancellation = new CancellationTokenSource();
        var canceledCleanupFault = CreateResponseDisposeFault(faultKind, cancellation.Token);
        var canceledCleanupLogger = new CapturingProviderCleanupLogger();
        var canceledHandler = new DisposeBoundaryHoldHandler(
            mutationMethod, response, cancellation, canceledCleanupFault);
        var canceledProvider = await CreatePolarisProviderAsync(
            canceledHandler, $"hold-return-canceled-{operation}-{outcome}-" + Guid.NewGuid().ToString("N"),
            canceledCleanupLogger);
        var actual = await CallHoldOutcomeAsync(canceledProvider, operation, cancellation.Token);

        Assert.IsTrue(cancellation.IsCancellationRequested,
            "The token must be canceled only after the PAPI body has been parsed and its response content disposed.");
        Assert.IsTrue(canceledHandler.MutationResponseContentDisposed);
        Assert.AreEqual(1, canceledHandler.MutationResponseContent?.DisposeCount ?? 0);
        AssertResponseCleanupDiagnostic(canceledCleanupLogger, canceledCleanupFault);
        Assert.AreEqual(1, canceledHandler.MutationRequestCount,
            "The parsed outcome is evidence for the one dispatched mutation, not permission to repeat it.");
        Assert.AreEqual(mutationMethod, canceledHandler.MutationRequestMethod);
        Assert.IsFalse(string.IsNullOrWhiteSpace(canceledHandler.MutationRequestBody));
        Assert.AreEqual(baseline, actual,
            "The strict public hold classifier must see the same typed and raw response with or without local cancellation.");
        Assert.AreEqual(outcome switch
        {
            "success" => HoldProviderOutcome.FinalSuccess,
            "no-effect" => HoldProviderOutcome.DefinitiveNoEffect,
            "reply-required" => HoldProviderOutcome.ReplyRequired,
            _ => throw new AssertFailedException("Unknown test outcome.")
        }, actual.Outcome);
        Assert.AreEqual((Guid?)BoundaryReply().RequestGuid, actual.RequestGuid);
        Assert.AreEqual("group", actual.TxnGroupQualifier);
        Assert.AreEqual("qualifier", actual.TxnQualifier);
    }

    [TestMethod]
    [DataRow(-3000, "operation-canceled")]
    [DataRow(-3000, "http-request")]
    [DataRow(-3000, "argument")]
    [DataRow(-3622, "operation-canceled")]
    [DataRow(-3622, "http-request")]
    [DataRow(-3622, "argument")]
    public async Task PolarisParsedPickupRejectionSurvivesPostParseDisposeFault(int papiErrorCode, string faultKind)
    {
        var body = JsonSerializer.Serialize(new { PAPIErrorCode = papiErrorCode });
        var baselineCleanupFault = CreateResponseDisposeFault(faultKind);
        var baselineCleanupLogger = new CapturingProviderCleanupLogger();
        var baselineHandler = new DisposeBoundaryPickupHandler(
            mutationResponseContent: body, throwOnResponseDispose: baselineCleanupFault);
        var baselineProvider = await CreatePolarisProviderAsync(
            baselineHandler, $"pickup-rejection-control-{papiErrorCode}-" + Guid.NewGuid().ToString("N"),
            baselineCleanupLogger);
        var baseline = await Assert.ThrowsExactlyAsync<PolarisPickupRejectedException>(() =>
            baselineProvider.UpdatePreferredPickupBranchAsync("20000000000001", 101, 2, CancellationToken.None));
        Assert.AreEqual(papiErrorCode, baseline.PapiErrorCode);
        Assert.AreEqual(1, baselineHandler.UpdateRequestCount);
        Assert.IsTrue(baselineHandler.MutationResponseContentDisposed);
        Assert.AreEqual(1, baselineHandler.MutationResponseContent?.DisposeCount ?? 0);
        AssertResponseCleanupDiagnostic(baselineCleanupLogger, baselineCleanupFault);

        using var cancellation = new CancellationTokenSource();
        var canceledCleanupFault = CreateResponseDisposeFault(faultKind, cancellation.Token);
        var canceledCleanupLogger = new CapturingProviderCleanupLogger();
        var canceledHandler = new DisposeBoundaryPickupHandler(
            cancellation, mutationResponseContent: body,
            throwOnResponseDispose: canceledCleanupFault);
        var canceledProvider = await CreatePolarisProviderAsync(
            canceledHandler, $"pickup-rejection-canceled-{papiErrorCode}-" + Guid.NewGuid().ToString("N"),
            canceledCleanupLogger);
        var actual = await Assert.ThrowsExactlyAsync<PolarisPickupRejectedException>(() =>
            canceledProvider.UpdatePreferredPickupBranchAsync("20000000000001", 101, 2, cancellation.Token));

        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.IsTrue(canceledHandler.MutationResponseContentDisposed);
        Assert.AreEqual(1, canceledHandler.MutationResponseContent?.DisposeCount ?? 0);
        AssertResponseCleanupDiagnostic(canceledCleanupLogger, canceledCleanupFault);
        Assert.AreEqual(1, canceledHandler.UpdateRequestCount);
        Assert.AreEqual(HttpMethod.Put, canceledHandler.UpdateRequestMethod);
        Assert.AreEqual(baseline.PapiErrorCode, actual.PapiErrorCode);
    }

    [TestMethod]
    [DataRow("operation-canceled")]
    [DataRow("http-request")]
    [DataRow("argument")]
    public async Task PolarisParsedPickupSuccessSurvivesPostParseDisposeFault(string faultKind)
    {
        var baselineCleanupFault = CreateResponseDisposeFault(faultKind);
        var baselineCleanupLogger = new CapturingProviderCleanupLogger();
        var baselineHandler = new DisposeBoundaryPickupHandler(
            throwOnResponseDispose: baselineCleanupFault);
        var baselineProvider = await CreatePolarisProviderAsync(
            baselineHandler, "pickup-return-control-" + Guid.NewGuid().ToString("N"), baselineCleanupLogger);
        await baselineProvider.UpdatePreferredPickupBranchAsync(
            "20000000000001", 101, 2, CancellationToken.None);

        Assert.AreEqual(1, baselineHandler.UpdateRequestCount,
            "The uncanceled control must complete exactly one serialized pickup mutation.");
        Assert.AreEqual(HttpMethod.Put, baselineHandler.UpdateRequestMethod);
        Assert.IsTrue(baselineHandler.MutationResponseContentDisposed);
        Assert.AreEqual(1, baselineHandler.MutationResponseContent?.DisposeCount ?? 0);
        AssertResponseCleanupDiagnostic(baselineCleanupLogger, baselineCleanupFault);

        using var cancellation = new CancellationTokenSource();
        var canceledCleanupFault = CreateResponseDisposeFault(faultKind, cancellation.Token);
        var canceledCleanupLogger = new CapturingProviderCleanupLogger();
        var canceledHandler = new DisposeBoundaryPickupHandler(
            cancellation, throwOnResponseDispose: canceledCleanupFault);
        var canceledProvider = await CreatePolarisProviderAsync(
            canceledHandler, "pickup-return-canceled-" + Guid.NewGuid().ToString("N"), canceledCleanupLogger);

        await canceledProvider.UpdatePreferredPickupBranchAsync(
            "20000000000001", 101, 2, cancellation.Token);

        Assert.IsTrue(cancellation.IsCancellationRequested,
            "The cancellation must originate from disposing the content after its PAPI data was parsed.");
        Assert.IsTrue(canceledHandler.MutationResponseContentDisposed);
        Assert.AreEqual(1, canceledHandler.MutationResponseContent?.DisposeCount ?? 0);
        AssertResponseCleanupDiagnostic(canceledCleanupLogger, canceledCleanupFault);
        Assert.AreEqual(1, canceledHandler.UpdateRequestCount,
            "A parsed provider response is one external PUT, not permission to retry it.");
        Assert.AreEqual(HttpMethod.Put, canceledHandler.UpdateRequestMethod);
        Assert.IsFalse(string.IsNullOrWhiteSpace(canceledHandler.UpdateRequestBody));
    }

}
