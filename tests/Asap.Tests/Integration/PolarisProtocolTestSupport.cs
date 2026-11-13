using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Net.Http.Headers;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Microsoft.Extensions.Logging;
namespace Asap.Tests.Integration;
public abstract class PolarisProtocolTestSupport
{
    protected static void AssertResponseCleanupDiagnostic(
        CapturingProviderCleanupLogger logger,
        Exception expectedCause)
    {
        Assert.AreEqual(1, logger.Entries.Count,
            "One completed mutation response-content cleanup fault produces exactly one diagnostic.");
        var entry = logger.Entries[0];
        Assert.AreEqual(LogLevel.Warning, entry.Level);
        Assert.AreEqual(36801, entry.EventId.Id);
        Assert.AreEqual("PolarisMutationResponseContentCleanupFailed", entry.EventId.Name);
        Assert.IsNull(entry.Exception,
            "The original exception must not be passed to the logger exception slot because provider messages may contain secrets.");
        var diagnostic = entry.State as PolarisMutationResponseCleanupDiagnostic
            ?? throw new AssertFailedException("The cleanup diagnostic must retain its exact safe state type.");
        Assert.AreSame(expectedCause, diagnostic.GetOriginalException(),
            "The in-process diagnostic keeps the exact original cleanup exception for authorized inspection.");
        Assert.AreEqual(diagnostic.ToString(), entry.FormattedMessage);
        Assert.IsFalse(entry.FormattedMessage.Contains(expectedCause.Message, StringComparison.Ordinal));
        Assert.IsFalse(entry.FormattedMessage.Contains("cleanup-secret", StringComparison.Ordinal));
        Assert.IsFalse(entry.FormattedMessage.Contains("polaris.invalid", StringComparison.OrdinalIgnoreCase));
    }


    protected static HoldCreateCommand BoundaryCreate() => new(7001, 9001, 101, 2, 99, 42);

    protected static Task BoundaryMutationAsync(PolarisPatronProvider provider, string operation, CancellationToken token) => operation switch
    {
        "create" => provider.CreateHoldAsync(BoundaryCreate(), token),
        "reply" => provider.ReplyToHoldAsync(BoundaryReply(), token),
        "pickup" => provider.UpdatePreferredPickupBranchAsync("20000000000001", 101, 2, token),
        _ => throw new AssertFailedException("Unknown test operation.")
    };

    protected static HoldReplyCommand BoundaryReply() => new(Guid.Parse("bc3ed7d8-cc9b-40e0-a285-8bb0f688fa10"), "group", "qualifier", 2);

    protected static HttpResponseMessage BoundaryResponse(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json) };


    protected static Task<HoldProviderResult> CallHoldOutcomeAsync(
        PolarisPatronProvider provider, string operation, CancellationToken cancellationToken) => operation switch
    {
        "create" => provider.CreateHoldAsync(BoundaryCreate(), cancellationToken),
        "reply" => provider.ReplyToHoldAsync(BoundaryReply(), cancellationToken),
        _ => throw new AssertFailedException("Unknown hold operation.")
    };


    protected sealed record CapturedProviderCleanupLog(
        LogLevel Level,
        EventId EventId,
        object? State,
        Exception? Exception,
        string FormattedMessage);


    protected sealed class CapturingProviderCleanupLogger : ILogger<PolarisPatronProvider>
    {
        public List<CapturedProviderCleanupLog> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new CapturedProviderCleanupLog(
                logLevel, eventId, state, exception, formatter(state, exception)));
        }
    }


    protected sealed class CatalogMaterialHandler(string? failure = null) : HttpMessageHandler
    {
        public int ReferenceCalls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isReference = request.RequestUri!.AbsolutePath.EndsWith("/marctypeofmaterials", StringComparison.Ordinal);
            if (request.RequestUri.AbsolutePath.Contains("/bib/", StringComparison.Ordinal))
            {
                return Task.FromResult(BoundaryResponse("""{"PAPIErrorCode":0,"BibGetRows":[{"ElementID":1,"Label":"Title","Value":"Exact title"},{"ElementID":17,"Label":"Format","Value":"53"}]}"""));
            }
            if (isReference)
            {
                ReferenceCalls++;
                if (failure == "timeout")
                {
                    throw new OperationCanceledException("Reference timeout");
                }
            }
            else
            {
                StringAssert.Contains(request.RequestUri.AbsolutePath, "/search/bibs/");
            }
            var json = isReference ? failure == "malformed" ? "{}" :
                """{"PAPIErrorCode":0,"MARCTypeOfMaterialsRows":[{"MARCTypeOfMaterialID":53,"Description":"Large print","SearchCode":"lp"},{"MARCTypeOfMaterialID":1,"Description":"Book","SearchCode":"book"}]}""" :
                """{"PAPIErrorCode":0,"TotalRecordsFound":2951,"BibSearchRows":[{"ControlNumber":9001,"Title":"Title","TypeOfMaterial":"33181604","PrimaryTypeOfMaterial":53},{"ControlNumber":9002,"Title":"Other title","TypeOfMaterial":"01"},{"ControlNumber":9003,"Title":"Unknown format","TypeOfMaterial":"999"}]}""";
            return Task.FromResult(new HttpResponseMessage(isReference && failure == "outage" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
    protected sealed class ContextPapiHandler(int organizationId) : HttpMessageHandler
    {
        public int PickupBranchId => organizationId * 100 + 1;
        public int RequestCount { get; private set; }
        public HashSet<string> Operations { get; } = [];
        private bool bootstrapping = true;
        private static readonly Guid Conversation = Guid.Parse("3b3ce08d-4111-411b-9999-784d0866d94c");

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            var path = request.RequestUri!.AbsolutePath;
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var actualOrganizationId = int.Parse(segments[6], System.Globalization.CultureInfo.InvariantCulture);
            string json;
            if (path.Contains("/authenticator/staff", StringComparison.Ordinal))
            {
                json = """{"PAPIErrorCode":0,"AccessToken":"context-token","AccessSecret":"context-secret","AuthExpDate":"2030-01-01T00:00:00Z"}""";
            }
            else if (path.Contains("/authenticator/patron", StringComparison.Ordinal))
            {
                Assert.AreEqual(1, actualOrganizationId, "Only bootstrap may use system scope.");
                Operations.Add("authenticate");
                json = """{"PAPIErrorCode":0,"PatronID":7001,"AccessToken":"patron-token","AccessSecret":"patron-secret"}""";
            }
            else if (path.EndsWith("/basicdata", StringComparison.Ordinal))
            {
                Assert.AreEqual(bootstrapping ? 1 : organizationId, actualOrganizationId);
                Operations.Add("refresh");
                json = JsonSerializer.Serialize(new { PAPIErrorCode = 0, PatronBasicData = new {
                    PatronID = 7001, Barcode = "20000000003470", PatronOrgID = PickupBranchId,
                    PatronCodeID = 1, RequestPickupBranchID = PickupBranchId } });
            }
            else if (path.Contains("/organizations/", StringComparison.Ordinal))
            {
                Assert.AreEqual(bootstrapping ? 1 : organizationId, actualOrganizationId,
                    "Hierarchy reads retain their bootstrap or servicing context.");
                bootstrapping = false;
                json = JsonSerializer.Serialize(new { PAPIErrorCode = 2, OrganizationsGetRows = new[] {
                    new { OrganizationID = organizationId, OrganizationCodeID = 2, Name = "Library", ParentOrganizationID = 1 },
                    new { OrganizationID = PickupBranchId, OrganizationCodeID = 3, Name = "Branch", ParentOrganizationID = organizationId } } });
            }
            else if (path.EndsWith("/pickupbranches", StringComparison.Ordinal))
            {
                Assert.AreEqual(PickupBranchId, actualOrganizationId, "Eligibility uses the live registered branch.");
                Operations.Add("pickup-read");
                json = JsonSerializer.Serialize(new { PAPIErrorCode = 1, PickupBranchesRows = new[] { new { ID = PickupBranchId } } });
            }
            else
            {
                var holdMemberContext = path.EndsWith("/holdrequest", StringComparison.Ordinal) ||
                    path.EndsWith("/holdrequest/" + Conversation, StringComparison.Ordinal);
                Assert.AreEqual(holdMemberContext ? PickupBranchId : organizationId, actualOrganizationId, path);
                if (request.Method == HttpMethod.Put && path.Contains("/patron/", StringComparison.Ordinal))
                {
                    Operations.Add("pickup-write");
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                    Assert.AreEqual(organizationId, body.RootElement.GetProperty("LogonBranchId").GetInt32());
                    Assert.AreEqual(PickupBranchId, body.RootElement.GetProperty("RequestPickupBranchID").GetInt32());
                    Assert.AreEqual(99, body.RootElement.GetProperty("LogonWorkstationId").GetInt32());
                    Assert.AreEqual(42, body.RootElement.GetProperty("LogonUserId").GetInt32());
                    json = """{"PAPIErrorCode":0}""";
                }
                else if (path.Contains("/search/patrons/", StringComparison.Ordinal))
                {
                    Operations.Add("patron-search");
                    json = """{"PAPIErrorCode":0,"TotalRecordsFound":0,"PatronSearchRows":[]}""";
                }
                else if (path.Contains("/search/bibs/", StringComparison.Ordinal))
                {
                    Operations.Add("bib-search");
                    json = """{"PAPIErrorCode":1,"TotalRecordsFound":1,"BibSearchRows":[{"ControlNumber":9001,"Title":"Title","ISBN":"9780000000001"}]}""";
                }
                else if (path.EndsWith("/holdings", StringComparison.Ordinal))
                {
                    Operations.Add("holdings");
                    json = """{"PAPIErrorCode":0,"BibHoldingsGetRows":[]}""";
                }
                else if (path.EndsWith("/bib/9001", StringComparison.Ordinal))
                {
                    Operations.Add("bib-detail");
                    json = """{"PAPIErrorCode":0,"BibGetRows":[{"ElementID":1,"Label":"Title","Value":"Title"}]}""";
                }
                else if (path.Contains("/holdrequests/", StringComparison.Ordinal))
                {
                    Operations.Add("holds");
                    json = JsonSerializer.Serialize(new { PAPIErrorCode = 0, PatronHoldRequestsGetRows = new[] {
                        new { HoldRequestID = 8456, BibID = 9001, StatusID = 9, StatusDescription = "Active", PickupBranchID = PickupBranchId } } });
                }
                else if (path.Contains("/itemsout/", StringComparison.Ordinal))
                {
                    Operations.Add("checkouts");
                    json = """{"PAPIErrorCode":0,"PatronItemsOutGetRows":[{"BibID":9001}]}""";
                }
                else if (path.EndsWith("/holdrequest", StringComparison.Ordinal))
                {
                    Operations.Add("create");
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                    Assert.AreEqual(PickupBranchId, body.RootElement.GetProperty("RequestingOrgID").GetInt32());
                    Assert.AreEqual(PickupBranchId, body.RootElement.GetProperty("PickupOrgID").GetInt32());
                    Assert.AreEqual(7001, body.RootElement.GetProperty("PatronID").GetInt32());
                    Assert.AreEqual(9001, body.RootElement.GetProperty("BibID").GetInt32());
                    Assert.AreEqual(99, body.RootElement.GetProperty("WorkstationID").GetInt32());
                    Assert.AreEqual(42, body.RootElement.GetProperty("UserID").GetInt32());
                    json = JsonSerializer.Serialize(new { PAPIErrorCode = 0, StatusType = 3, StatusValue = 5,
                        RequestGUID = Conversation, TxnGroupQualifer = "group", TxnQualifier = "qualifier" });
                }
                else if (path.EndsWith("/holdrequest/" + Conversation, StringComparison.Ordinal))
                {
                    Operations.Add("reply");
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                    Assert.AreEqual(PickupBranchId, body.RootElement.GetProperty("RequestingOrgID").GetInt32());
                    Assert.AreEqual("group", body.RootElement.GetProperty("TxnGroupQualifier").GetString());
                    json = JsonSerializer.Serialize(new { PAPIErrorCode = 0, StatusType = 2, StatusValue = 1, RequestGUID = Conversation });
                }
                else
                {
                    throw new AssertFailedException("Unexpected package request: " + path);
                }
            }
            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(json), RequestMessage = request };
        }
    }
    protected static Exception CreateResponseDisposeFault(string kind, CancellationToken token = default) => kind switch
    {
        "operation-canceled" => new OperationCanceledException(
            "Caller cancellation after complete response parsing. cleanup-secret=https://polaris.invalid/provider-body", token),
        "http-request" => new HttpRequestException(
            "Response-content cleanup fault after complete parsing. cleanup-secret=https://polaris.invalid/provider-body"),
        "argument" => new ArgumentException(
            "Response-content cleanup defect after complete parsing. cleanup-secret=https://polaris.invalid/provider-body"),
        _ => throw new AssertFailedException("Unknown response-content disposal fault.")
    };


    protected sealed class DisposeBoundaryHoldHandler(
        HttpMethod expectedMutationMethod,
        string mutationResponseContent,
        CancellationTokenSource? cancelOnMutationResponseDispose = null,
        Exception? throwOnResponseDispose = null,
        TaskCompletionSource<bool>? mutationRequestStarted = null,
        TaskCompletionSource<bool>? mutationResponseRelease = null,
        string? expectedPreflightBarcode = null) : HttpMessageHandler
    {
        private const string StaffTokenContent =
            "{\"PAPIErrorCode\":0,\"AccessToken\":\"returned-boundary-token\",\"AccessSecret\":\"returned-boundary-secret\",\"AuthExpDate\":\"2030-01-01T00:00:00Z\"}";

        public int MutationRequestCount { get; private set; }
        public HttpMethod? MutationRequestMethod { get; private set; }
        public string MutationRequestBody { get; private set; } = string.Empty;
        public bool MutationResponseContentDisposed { get; private set; }
        public DisposeCancellingJsonContent? MutationResponseContent { get; private set; }
        public int HoldReadRequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/authenticator/staff", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(StaffTokenContent, Encoding.UTF8, "application/json"),
                    RequestMessage = request
                };
            }

            if (expectedPreflightBarcode is not null && request.Method == HttpMethod.Get &&
                path.Contains("/holdrequests/", StringComparison.OrdinalIgnoreCase))
            {
                Assert.AreEqual(0, HoldReadRequestCount, "Only the expected preflight hold-list read is in scope.");
                Assert.IsTrue(path.Contains(expectedPreflightBarcode, StringComparison.OrdinalIgnoreCase), path);
                HoldReadRequestCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[]}",
                        Encoding.UTF8, "application/json"),
                    RequestMessage = request
                };
            }

            Assert.AreEqual(expectedMutationMethod, request.Method, "Only the expected hold mutation is in scope.");
            Assert.IsTrue(path.Contains("/holdrequest", StringComparison.OrdinalIgnoreCase));
            MutationRequestCount++;
            MutationRequestMethod = request.Method;
            MutationRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            if (mutationRequestStarted is not null)
            {
                if (mutationResponseRelease is null)
                {
                    throw new InvalidOperationException("The gated mutation needs a response-release signal.");
                }
                mutationRequestStarted.TrySetResult(true);
                await mutationResponseRelease.Task.WaitAsync(cancellationToken);
            }
            var content = new DisposeCancellingJsonContent(
                mutationResponseContent, cancelOnMutationResponseDispose,
                () => MutationResponseContentDisposed = true, throwOnResponseDispose);
            MutationResponseContent = content;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request
            };
        }
    }


    protected sealed class DisposeBoundaryPickupHandler(
        CancellationTokenSource? cancelOnMutationResponseDispose = null,
        Action? onMutationResponseCreated = null,
        string mutationResponseContent = "{\"PAPIErrorCode\":0}",
        Exception? throwOnResponseDispose = null) : HttpMessageHandler
    {
        private const string StaffTokenContent =
            "{\"PAPIErrorCode\":0,\"AccessToken\":\"returned-boundary-token\",\"AccessSecret\":\"returned-boundary-secret\",\"AuthExpDate\":\"2030-01-01T00:00:00Z\"}";

        public int UpdateRequestCount { get; private set; }
        public HttpMethod? UpdateRequestMethod { get; private set; }
        public string UpdateRequestBody { get; private set; } = string.Empty;
        public bool MutationResponseContentDisposed { get; private set; }
        public DisposeCancellingJsonContent? MutationResponseContent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/authenticator/staff", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(StaffTokenContent, Encoding.UTF8, "application/json"),
                    RequestMessage = request
                };
            }

            Assert.AreEqual(HttpMethod.Put, request.Method, "Only the expected pickup mutation is in scope.");
            UpdateRequestCount++;
            UpdateRequestMethod = request.Method;
            UpdateRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            onMutationResponseCreated?.Invoke();
            var responseContent = new DisposeCancellingJsonContent(
                mutationResponseContent, cancelOnMutationResponseDispose,
                () => MutationResponseContentDisposed = true, throwOnResponseDispose);
            MutationResponseContent = responseContent;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = responseContent,
                RequestMessage = request
            };
        }
    }


    protected sealed class DisposeCancellingJsonContent(
        string content, CancellationTokenSource? cancellation,
        Action? onDisposed = null, Exception? throwOnDispose = null)
        : StringContent(content, Encoding.UTF8, "application/json")
    {
        private int disposed;
        private int disposeCount;

        public int DisposeCount => Volatile.Read(ref disposeCount);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Interlocked.Increment(ref disposeCount);
            }
            var firstDispose = disposing && Interlocked.Exchange(ref disposed, 1) == 0;
            if (firstDispose)
            {
                onDisposed?.Invoke();
                cancellation?.Cancel();
            }

            base.Dispose(disposing);
            if (firstDispose && throwOnDispose is not null)
            {
                ExceptionDispatchInfo.Capture(throwOnDispose).Throw();
            }
        }
    }
    protected static string HoldOutcomeResponse(string outcome)
    {
        var (statusType, statusValue) = outcome switch
        {
            "success" => (2, 1),
            "no-effect" => (2, 0),
            "reply-required" => (3, 5),
            _ => throw new AssertFailedException("Unknown hold outcome.")
        };
        return JsonSerializer.Serialize(new
        {
            PAPIErrorCode = 0,
            StatusType = statusType,
            StatusValue = statusValue,
            RequestGUID = BoundaryReply().RequestGuid,
            TxnGroupQualifer = "group",
            TxnQualifier = "qualifier"
        });
    }


    protected sealed class ParallelNativePapiHandler : HttpMessageHandler
    {
        private readonly HoldCreateCommand[] commands;
        private readonly ConcurrentDictionary<int, int> createCounts = new();
        private readonly ConcurrentDictionary<int, int> replyCounts = new();
        private readonly TaskCompletionSource bothCreates = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<int, Guid> conversations;
        private int started;
        public ParallelNativePapiHandler(HoldCreateCommand[] commands)
        {
            this.commands = commands;
            conversations = commands.ToDictionary(command => command.PatronId, _ => Guid.NewGuid());
        }
        public void Verify()
        {
            Assert.AreEqual(2, Volatile.Read(ref started));
            CollectionAssert.AreEquivalent(commands.Select(command => command.PatronId).ToArray(), createCounts.Keys.ToArray());
            CollectionAssert.AreEquivalent(commands.Select(command => command.PatronId).ToArray(), replyCounts.Keys.ToArray());
            Assert.IsTrue(createCounts.Values.All(count => count == 1) && replyCounts.Values.All(count => count == 1));
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            string json;
            if (path.Contains("/authenticator/staff", StringComparison.Ordinal))
            {
                json = """{"PAPIErrorCode":0,"AccessToken":"parallel-token","AccessSecret":"parallel-secret","AuthExpDate":"2030-01-01T00:00:00Z"}""";
            }
            else
            {
                var nativeRoute = int.Parse(path.Split('/', StringSplitOptions.RemoveEmptyEntries)[6], CultureInfo.InvariantCulture);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var original = commands.Single(command => command.RequestingOrganizationId == nativeRoute);
                Assert.AreEqual(original.RequestingOrganizationId, body.RootElement.GetProperty("RequestingOrgID").GetInt32());
                var conversation = conversations[original.PatronId];
                if (path.EndsWith("/holdrequest", StringComparison.Ordinal))
                {
                    Assert.AreEqual(original.PatronId, body.RootElement.GetProperty("PatronID").GetInt32());
                    Assert.AreEqual(original.BibId, body.RootElement.GetProperty("BibID").GetInt32());
                    Assert.AreEqual(original.PickupBranchId, body.RootElement.GetProperty("PickupOrgID").GetInt32());
                    Assert.AreEqual(original.WorkstationId, body.RootElement.GetProperty("WorkstationID").GetInt32());
                    Assert.AreEqual(original.PolarisUserId, body.RootElement.GetProperty("UserID").GetInt32());
                    Assert.AreEqual(1, createCounts.AddOrUpdate(original.PatronId, 1, (_, count) => count + 1));
                    if (Interlocked.Increment(ref started) == 2)
                    {
                        bothCreates.TrySetResult();
                    }
                    await bothCreates.Task.WaitAsync(cancellationToken);
                    json = JsonSerializer.Serialize(new { PAPIErrorCode = 0, StatusType = 3, StatusValue = 5,
                        RequestGUID = conversation, TxnGroupQualifer = $"group-{original.PatronId}", TxnQualifier = $"qualifier-{original.BibId}" });
                }
                else
                {
                    Assert.IsTrue(path.EndsWith("/holdrequest/" + conversation, StringComparison.Ordinal));
                    Assert.AreEqual($"group-{original.PatronId}", body.RootElement.GetProperty("TxnGroupQualifier").GetString());
                    Assert.AreEqual($"qualifier-{original.BibId}", body.RootElement.GetProperty("TxnQualifier").GetString());
                    Assert.AreEqual(1, replyCounts.AddOrUpdate(original.PatronId, 1, (_, count) => count + 1));
                    json = JsonSerializer.Serialize(new { PAPIErrorCode = 0, StatusType = 2, StatusValue = 1, RequestGUID = conversation });
                }
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json), RequestMessage = request };
        }
    }
    protected sealed class ProtectedSequenceResponseHandler(
        params (HttpStatusCode StatusCode, string Content)[] responses) : HttpMessageHandler
    {
        private const string ProtectedTokenContent =
            "{\"PAPIErrorCode\":0,\"AccessToken\":\"protected-token\",\"AccessSecret\":\"protected-secret\",\"AuthExpDate\":\"2030-01-01T00:00:00Z\"}";
        private readonly Queue<(HttpStatusCode StatusCode, string Content)> remaining = new(responses);

        public int RequestCount { get; private set; }
        public List<Uri> RequestUris { get; } = [];
        public Exception? ProtectedRequestException { get; set; }
        public Action<CancellationToken>? BeforeProtectedRequest { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            RequestUris.Add(request.RequestUri!);
            var isAuthentication = request.RequestUri!.AbsolutePath.Contains(
                "/authenticator/staff",
                StringComparison.OrdinalIgnoreCase);
            if (isAuthentication)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ProtectedTokenContent),
                    RequestMessage = request
                });
            }

            BeforeProtectedRequest?.Invoke(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (ProtectedRequestException is not null)
            {
                throw ProtectedRequestException;
            }

            if (!remaining.TryDequeue(out var response))
            {
                throw new InvalidOperationException("No fake Polaris response remains for this request.");
            }

            return Task.FromResult(new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(response.Content),
                RequestMessage = request
            });
        }
    }


    protected sealed class ProtectedUpdateResponseHandler(string updateContent) : HttpMessageHandler
    {
        private const string ProtectedTokenContent =
            "{\"PAPIErrorCode\":0,\"AccessToken\":\"protected-token\",\"AccessSecret\":\"protected-secret\",\"AuthExpDate\":\"2030-01-01T00:00:00Z\"}";

        public int UpdateRequestCount { get; private set; }
        public string UpdateRequestPath { get; private set; } = string.Empty;
        public string UpdateRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isAuthentication = request.RequestUri!.AbsolutePath.Contains(
                "/authenticator/staff",
                StringComparison.OrdinalIgnoreCase);
            if (!isAuthentication)
            {
                UpdateRequestCount++;
                UpdateRequestPath = request.RequestUri.AbsolutePath;
                UpdateRequestBody = request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(isAuthentication ? ProtectedTokenContent : updateContent),
                RequestMessage = request
            };
        }
    }

    // SQL fixture creation and application mutations share the same deterministic clock.

    protected sealed class ProviderBoundaryHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
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
    protected static string SearchPath(Uri uri)
    {
        var path = uri.AbsolutePath;
        var searchIndex = path.IndexOf("/search/bibs/", StringComparison.Ordinal);
        return searchIndex >= 0 ? path[searchIndex..] : path;
    }
    protected static string SearchResponse(params Dictionary<string, object?>[] rows) =>
        JsonSerializer.Serialize(new
        {
            PAPIErrorCode = rows.Length,
            TotalRecordsFound = rows.Length,
            BibSearchRows = rows
        });


    protected static Dictionary<string, object?> SearchRow(
        int controlNumber,
        string title,
        string? author = null,
        string? publication = null,
        string? identifierName = null,
        string? identifier = null,
        int? materialType = null)
    {
        var row = new Dictionary<string, object?>
        {
            ["ControlNumber"] = controlNumber,
            ["Title"] = title
        };
        if (author is not null) row["Author"] = author;
        if (publication is not null) row["PublicationDate"] = publication;
        if (identifierName is not null && identifier is not null) row[identifierName] = identifier;
        if (materialType is not null) row["PrimaryTypeOfMaterial"] = materialType;
        return row;
    }


    protected sealed class SequenceResponseHandler(
        params (HttpStatusCode StatusCode, string Content)[] responses) : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode StatusCode, string Content)> remaining = new(responses);

        public int RequestCount { get; private set; }
        public List<Uri> RequestUris { get; } = [];
        public Action<int, CancellationToken>? OnRequest { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            RequestUris.Add(request.RequestUri!);
            OnRequest?.Invoke(RequestCount, cancellationToken);
            if (!remaining.TryDequeue(out var response))
            {
                throw new InvalidOperationException("No fake Polaris response remains for this request.");
            }

            return Task.FromResult(new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(response.Content),
                RequestMessage = request
            });
        }
    }


    protected sealed class StaticResponseHandler(HttpStatusCode statusCode, string content) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public List<string> RequestPaths { get; } = [];
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            RequestPaths.Add(request.RequestUri!.AbsolutePath);
            RequestUris.Add(request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content),
                RequestMessage = request
            });
        }
    }


    protected sealed class StrictPatronIdentityHandler(
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
    protected sealed class ThrowingResponseBodyContent : HttpContent
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
