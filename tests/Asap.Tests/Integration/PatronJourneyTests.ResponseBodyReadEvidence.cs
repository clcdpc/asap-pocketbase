using System.Net;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow("create")]
    [DataRow("reply")]
    [DataRow("pickup")]
    public async Task PolarisMutationBodyReadFailurePreservesUnknownOutcome(string operation)
    {
        var bodyReadFailure = new HttpRequestException(
            $"Response body read failed for {operation}; this result is not complete provider evidence.");
        var content = new ThrowingResponseBodyContent(bodyReadFailure);
        var handler = new ProviderBoundaryHandler((request, _) =>
        {
            Assert.AreEqual(operation == "create" ? HttpMethod.Post : HttpMethod.Put, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        var provider = await CreatePolarisProviderAsync(
            handler,
            $"response-body-read-{operation}-{Guid.NewGuid():N}");

        if (operation is "create" or "reply")
        {
            var result = operation == "create"
                ? await provider.CreateHoldAsync(BoundaryCreate(), CancellationToken.None)
                : await provider.ReplyToHoldAsync(BoundaryReply(), CancellationToken.None);

            Assert.AreEqual(HoldProviderOutcome.Ambiguous, result.Outcome,
                "A response whose body could not be read has no parsed result and remains an unknown mutation outcome.");
            Assert.IsNull(result.RequestGuid);
            Assert.IsNull(result.HoldRequestId);
            Assert.IsNull(result.TxnGroupQualifier);
            Assert.IsNull(result.TxnQualifier);
        }
        else
        {
            var failure = await Assert.ThrowsExactlyAsync<PolarisOperationalException>(() =>
                provider.UpdatePreferredPickupBranchAsync(
                    "20000000000001", 101, 2, CancellationToken.None));
            Assert.AreEqual("polaris_pickup_update_failed", failure.Code);
            Assert.AreSame(bodyReadFailure, failure.InnerException,
                "The provider must preserve the actual body-read fault rather than synthesize success or caller cancellation.");
        }

        Assert.AreEqual(1, handler.OperationCalls,
            "A body-read failure follows one dispatched mutation and must not cause a retry.");
        Assert.AreEqual(1, content.ReadAttemptCount,
            "The fixture fault must occur while the production client reads response content.");
    }

    private sealed class ThrowingResponseBodyContent : HttpContent
    {
        private readonly HttpRequestException failure;

        public ThrowingResponseBodyContent(HttpRequestException failure)
        {
            this.failure = failure;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        public int ReadAttemptCount { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            ReadAttemptCount++;
            return Task.FromException(failure);
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            ReadAttemptCount++;
            return Task.FromException(failure);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
