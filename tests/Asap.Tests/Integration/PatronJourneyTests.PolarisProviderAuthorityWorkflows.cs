using System.Net;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task PinnedCheckoutProtocolFailureCannotFulfillTheMatchingRequest()
    {
        const int bibId = 9909;
        const int organizationId = 3898;
        var barcode = $"2000000000{Random.Shared.Next(100000, 999999)}";
        var handler = new WorkflowCheckoutProtocolHandler(
            $"{{\"PAPIErrorCode\":0,\"PatronItemsOutGetRows\":[{{\"BibID\":\"{bibId}\"}}]}}");
        var provider = await CreatePolarisProviderAsync(handler, "checkout-workflow-" + Guid.NewGuid().ToString("N"));
        await using var scoped = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton<IStaffPolarisProvider>(provider);
        }));
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await UpsertTestOrganizationAsync(organizationId, "Strict checkout library", "SCL");
        var seeded = await SeedCompletedHoldIdentityAsync(
            "strict-checkout-evidence",
            barcode,
            bibId,
            holdRequestId: 99091,
            organizationId: organizationId);
        try
        {
            await PrepareSingleItemCycleAsync(QueueNames.FulfillmentTracking, organizationId, seeded.RequestId);
            int eventCountBefore;
            int outboxCountBefore;
            await using (var before = await contexts.CreateDbContextAsync())
            {
                eventCountBefore = await before.TitleRequestEvents.CountAsync(item =>
                    item.TitleRequestId == seeded.RequestId);
                outboxCountBefore = await before.EmailOutbox.CountAsync();
            }
            var result = await scoped.Services.GetRequiredService<WorkflowProcessingService>()
                .ProcessWorkflowAsync(organizationId, CancellationToken.None);

            Assert.AreEqual(1, handler.CheckoutCalls,
                "The production pinned serializer must be the checkout transport used by the workflow.");
            Assert.AreEqual(0, handler.HoldCalls,
                "Malformed checkout identity must stop fulfillment before later provider evidence is read.");
            await using var verify = await contexts.CreateDbContextAsync();
            Assert.AreEqual(RequestStatus.HoldPlaced,
                (await verify.TitleRequests.SingleAsync(item => item.Id == seeded.RequestId)).Status);
            Assert.AreEqual(0, await verify.TitleRequestEvents.CountAsync(item =>
                item.TitleRequestId == seeded.RequestId && item.EventType == "fulfilled"));
            Assert.AreEqual(eventCountBefore, await verify.TitleRequestEvents.CountAsync(item =>
                item.TitleRequestId == seeded.RequestId),
                "Malformed checkout identity must not append any fulfillment event.");
            Assert.AreEqual(outboxCountBefore, await verify.EmailOutbox.CountAsync(),
                "Malformed checkout identity must not create follow-up email work.");
            var operation = await verify.HoldPlacementOperations.SingleAsync(item => item.Id == seeded.OperationId);
            Assert.AreEqual("hold_tracking_provider_error", operation.LastErrorCode);
            Assert.AreEqual("operational_failure", result.Code);
        }
        finally
        {
            await DeleteRequestAsync(seeded.RequestId);
            await using var cleanup = await contexts.CreateDbContextAsync();
            await cleanup.QueueProgress.Where(item => item.ScopeOrganizationId == organizationId).ExecuteDeleteAsync();
            await cleanup.Organizations.Where(item => item.Id == organizationId).ExecuteDeleteAsync();
        }
    }

    [TestMethod]
    public async Task PinnedHoldQualifierAliasFlowsIntoTheExactReplyAndDurableJournalSnapshot()
    {
        var seeded = await SeedPendingHoldRequestAsync("pinned-provider-conversation");
        var barcode = await ReadProviderRequestBarcodeAsync(seeded.RequestId);
        var handler = new HoldConversationPapiHandler(aliasesAgree: true, barcode: barcode);
        var provider = await CreatePolarisProviderAsync(handler, "hold-conversation-" + Guid.NewGuid().ToString("N"));
        await using var scoped = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton<IPatronProvider>(provider);
            services.AddSingleton<IStaffPolarisProvider>(provider);
        }));
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var actor = await ReadConfiguredSuperAdminAsync();
        int eventCountBefore;
        await using (var before = await contexts.CreateDbContextAsync())
        {
            eventCountBefore = await before.TitleRequestEvents.CountAsync(item =>
                item.TitleRequestId == seeded.RequestId);
        }
        try
        {
            var placed = await scoped.Services.GetRequiredService<HoldPlacementService>().PlaceAsync(
                actor,
                seeded.RequestId,
                new VersionInput(StaffVersion.Encode(seeded.RowVersion)),
                CancellationToken.None);

            Assert.AreEqual("updated", placed.Code);
            Assert.AreEqual(1, handler.CreateCalls);
            Assert.AreEqual(1, handler.ReplyCalls);
            Assert.IsNotNull(handler.ReplyBody, "The create response's exact qualifier must be sent in the reply.");
            using (var replyBody = JsonDocument.Parse(handler.ReplyBody!))
            {
                Assert.AreEqual(HoldConversationPapiHandler.GroupQualifier,
                    replyBody.RootElement.GetProperty("TxnGroupQualifier").GetString());
                Assert.IsFalse(replyBody.RootElement.TryGetProperty("TxnGroupQualifer", out _),
                    "The pinned reply serializer emits the correctly spelled property for the outgoing DTO.");
                Assert.AreEqual(HoldConversationPapiHandler.TransactionQualifier,
                    GetJsonStringPropertyIgnoreCase(replyBody.RootElement, "TxnQualifier"));
                Assert.IsFalse(replyBody.RootElement.TryGetProperty("RequestGUID", out _),
                    "The pinned reply serializer carries the request GUID in the URL, not the body.");
                Assert.AreEqual(2, replyBody.RootElement.GetProperty("RequestingOrgID").GetInt32());
                Assert.AreEqual(1, replyBody.RootElement.GetProperty("Answer").GetInt32());
                Assert.AreEqual(3, replyBody.RootElement.GetProperty("State").GetInt32());
            }
            Assert.IsNotNull(handler.ReplyUri);
            Assert.IsTrue(Uri.TryCreate(handler.ReplyUri, UriKind.Absolute, out var replyUri));
            Assert.IsTrue(replyUri!.AbsolutePath.EndsWith(
                "/holdrequest/" + HoldConversationPapiHandler.Conversation,
                StringComparison.OrdinalIgnoreCase),
                "The exact conversation GUID must be bound to the reply request URI.");

            await using var verify = await contexts.CreateDbContextAsync();
            var operation = await verify.HoldPlacementOperations.SingleAsync(item =>
                item.TitleRequestId == seeded.RequestId);
            Assert.AreEqual(HoldConversationPapiHandler.Conversation, operation.PolarisRequestGuid);
            Assert.AreEqual(HoldConversationPapiHandler.GroupQualifier, operation.TxnGroupQualifier);
            Assert.AreEqual(HoldConversationPapiHandler.TransactionQualifier, operation.TxnQualifier);
            Assert.AreEqual(RequestStatus.HoldPlaced,
                (await verify.TitleRequests.SingleAsync(item => item.Id == seeded.RequestId)).Status);
            Assert.AreEqual(eventCountBefore + 1, await verify.TitleRequestEvents.CountAsync(item =>
                item.TitleRequestId == seeded.RequestId));
        }
        finally
        {
            await DeleteRequestAsync(seeded.RequestId);
        }
    }

    [TestMethod]
    public async Task PinnedHoldQualifierAliasContradictionCannotDispatchAReplyOrPersistAChosenQualifier()
    {
        var seeded = await SeedPendingHoldRequestAsync("pinned-provider-conversation-conflict");
        var barcode = await ReadProviderRequestBarcodeAsync(seeded.RequestId);
        var handler = new HoldConversationPapiHandler(aliasesAgree: false, barcode: barcode);
        var provider = await CreatePolarisProviderAsync(handler, "hold-conversation-conflict-" + Guid.NewGuid().ToString("N"));
        await using var scoped = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton<IPatronProvider>(provider);
            services.AddSingleton<IStaffPolarisProvider>(provider);
        }));
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var actor = await ReadConfiguredSuperAdminAsync();
        int eventCountBefore;
        int outboxCountBefore;
        await using (var before = await contexts.CreateDbContextAsync())
        {
            eventCountBefore = await before.TitleRequestEvents.CountAsync(item =>
                item.TitleRequestId == seeded.RequestId);
            outboxCountBefore = await before.EmailOutbox.CountAsync();
        }
        try
        {
            var placed = await scoped.Services.GetRequiredService<HoldPlacementService>().PlaceAsync(
                actor,
                seeded.RequestId,
                new VersionInput(StaffVersion.Encode(seeded.RowVersion)),
                CancellationToken.None);

            Assert.AreEqual("hold_operator_required", placed.Code);
            Assert.AreEqual(1, handler.CreateCalls);
            Assert.AreEqual(0, handler.ReplyCalls,
                "Contradictory create response aliases must not dispatch a reply.");
            Assert.IsNull(handler.ReplyBody, "Contradictory source fields must not choose a different qualifier for reply.");
            await using var verify = await contexts.CreateDbContextAsync();
            var operation = await verify.HoldPlacementOperations.SingleAsync(item =>
                item.TitleRequestId == seeded.RequestId);
            Assert.IsNull(operation.PolarisRequestGuid);
            Assert.IsNull(operation.TxnGroupQualifier);
            Assert.IsNull(operation.TxnQualifier);
            Assert.AreEqual(HoldOperationPhase.CreateStarted, operation.Phase);
            Assert.AreEqual("hold_identity_ambiguous", operation.LastErrorCode);
            Assert.AreEqual(RequestStatus.PendingHold,
                (await verify.TitleRequests.SingleAsync(item => item.Id == seeded.RequestId)).Status);
            Assert.AreEqual(0, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == seeded.RequestId));
            Assert.AreEqual(eventCountBefore, await verify.TitleRequestEvents.CountAsync(item =>
                item.TitleRequestId == seeded.RequestId));
            Assert.AreEqual(outboxCountBefore, await verify.EmailOutbox.CountAsync());
        }
        finally
        {
            await DeleteRequestAsync(seeded.RequestId);
        }
    }

    [TestMethod]
    [DataRow("{\"PAPIErrorCode\":[],\"StatusType\":3,\"StatusValue\":5,\"RequestGUID\":\"c2f29019-c3ea-43dc-9b21-a04389ed00a1\",\"TxnGroupQualifer\":\"durable-group\",\"TxnQualifier\":\"durable-transaction\"}")]
    [DataRow("{\"PAPIErrorCode\":0,\"StatusType\":{},\"StatusValue\":5,\"RequestGUID\":\"c2f29019-c3ea-43dc-9b21-a04389ed00a1\",\"TxnGroupQualifer\":\"durable-group\",\"TxnQualifier\":\"durable-transaction\"}")]
    [DataRow("{\"PAPIErrorCode\":0,\"StatusType\":3,\"StatusValue\":[],\"RequestGUID\":\"c2f29019-c3ea-43dc-9b21-a04389ed00a1\",\"TxnGroupQualifer\":\"durable-group\",\"TxnQualifier\":\"durable-transaction\"}")]
    public async Task PinnedMalformedCreateResponseRequiresOperatorAndPreventsReplyOrDuplicatePut(string malformedResponse)
    {
        var seeded = await SeedPendingHoldRequestAsync("pinned-malformed-create-response");
        var barcode = await ReadProviderRequestBarcodeAsync(seeded.RequestId);
        var handler = new HoldConversationPapiHandler(
            aliasesAgree: true, barcode: barcode, createResponseOverride: malformedResponse);
        var provider = await CreatePolarisProviderAsync(handler, "hold-malformed-create-" + Guid.NewGuid().ToString("N"));
        await using var scoped = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton<IPatronProvider>(provider);
            services.AddSingleton<IStaffPolarisProvider>(provider);
        }));
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var actor = await ReadConfiguredSuperAdminAsync();
        int eventCountBefore;
        int outboxCountBefore;
        await using (var before = await contexts.CreateDbContextAsync())
        {
            eventCountBefore = await before.TitleRequestEvents.CountAsync(item => item.TitleRequestId == seeded.RequestId);
            outboxCountBefore = await before.EmailOutbox.CountAsync();
        }

        try
        {
            var service = scoped.Services.GetRequiredService<HoldPlacementService>();
            var first = await service.PlaceAsync(actor, seeded.RequestId,
                new VersionInput(StaffVersion.Encode(seeded.RowVersion)), CancellationToken.None);
            var retry = await service.PlaceAsync(actor, seeded.RequestId,
                new VersionInput(StaffVersion.Encode(seeded.RowVersion)), CancellationToken.None);

            Assert.AreEqual("hold_operator_required", first.Code);
            Assert.AreEqual("hold_operation_incomplete", retry.Code);
            Assert.AreEqual(first.OperationId, retry.OperationId);
            Assert.AreEqual(1, handler.CreateCalls, "The malformed post-dispatch result must prevent a second create PUT.");
            Assert.AreEqual(0, handler.ReplyCalls, "No reply may follow a malformed create response.");
            Assert.IsNull(handler.ReplyBody);
            await using var verify = await contexts.CreateDbContextAsync();
            var operation = await verify.HoldPlacementOperations.SingleAsync(item => item.TitleRequestId == seeded.RequestId);
            Assert.AreEqual(HoldOperationState.OperatorRequired, operation.State);
            Assert.AreEqual("hold_identity_ambiguous", operation.LastErrorCode);
            Assert.AreEqual(1, operation.RecoveryAttemptCount);
            Assert.AreEqual(HoldOperationPhase.CreateStarted, operation.Phase);
            Assert.IsNull(operation.PolarisRequestGuid);
            Assert.IsNull(operation.TxnGroupQualifier);
            Assert.IsNull(operation.TxnQualifier);
            Assert.AreEqual(RequestStatus.PendingHold,
                (await verify.TitleRequests.SingleAsync(item => item.Id == seeded.RequestId)).Status);
            Assert.AreEqual(eventCountBefore, await verify.TitleRequestEvents.CountAsync(item =>
                item.TitleRequestId == seeded.RequestId));
            Assert.AreEqual(outboxCountBefore, await verify.EmailOutbox.CountAsync());
        }
        finally
        {
            await DeleteRequestAsync(seeded.RequestId);
        }
    }

    [TestMethod]
    [DataRow("{\"PAPIErrorCode\":[],\"StatusType\":2,\"StatusValue\":1,\"RequestGUID\":\"c2f29019-c3ea-43dc-9b21-a04389ed00a1\",\"TxnGroupQualifer\":\"durable-group\",\"TxnQualifier\":\"durable-transaction\"}")]
    [DataRow("{\"PAPIErrorCode\":0,\"StatusType\":{},\"StatusValue\":1,\"RequestGUID\":\"c2f29019-c3ea-43dc-9b21-a04389ed00a1\",\"TxnGroupQualifer\":\"durable-group\",\"TxnQualifier\":\"durable-transaction\"}")]
    [DataRow("{\"PAPIErrorCode\":0,\"StatusType\":2,\"StatusValue\":[],\"RequestGUID\":\"c2f29019-c3ea-43dc-9b21-a04389ed00a1\",\"TxnGroupQualifer\":\"durable-group\",\"TxnQualifier\":\"durable-transaction\"}")]
    public async Task PinnedMalformedReplyResponseRequiresOperatorAndPreventsDuplicatePut(string malformedResponse)
    {
        var seeded = await SeedPendingHoldRequestAsync("pinned-malformed-reply-response");
        var barcode = await ReadProviderRequestBarcodeAsync(seeded.RequestId);
        var handler = new HoldConversationPapiHandler(
            aliasesAgree: true, barcode: barcode, replyResponseOverride: malformedResponse);
        var provider = await CreatePolarisProviderAsync(handler, "hold-malformed-reply-" + Guid.NewGuid().ToString("N"));
        await using var scoped = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton<IPatronProvider>(provider);
            services.AddSingleton<IStaffPolarisProvider>(provider);
        }));
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var actor = await ReadConfiguredSuperAdminAsync();
        int eventCountBefore;
        int outboxCountBefore;
        await using (var before = await contexts.CreateDbContextAsync())
        {
            eventCountBefore = await before.TitleRequestEvents.CountAsync(item => item.TitleRequestId == seeded.RequestId);
            outboxCountBefore = await before.EmailOutbox.CountAsync();
        }

        try
        {
            var service = scoped.Services.GetRequiredService<HoldPlacementService>();
            var first = await service.PlaceAsync(actor, seeded.RequestId,
                new VersionInput(StaffVersion.Encode(seeded.RowVersion)), CancellationToken.None);
            var retry = await service.PlaceAsync(actor, seeded.RequestId,
                new VersionInput(StaffVersion.Encode(seeded.RowVersion)), CancellationToken.None);

            Assert.AreEqual("hold_operator_required", first.Code);
            Assert.AreEqual("hold_operation_incomplete", retry.Code);
            Assert.AreEqual(first.OperationId, retry.OperationId);
            Assert.AreEqual(1, handler.CreateCalls);
            Assert.AreEqual(1, handler.ReplyCalls, "The ambiguous dispatched reply must not be repeated.");
            Assert.IsNotNull(handler.ReplyBody);
            await using var verify = await contexts.CreateDbContextAsync();
            var operation = await verify.HoldPlacementOperations.SingleAsync(item => item.TitleRequestId == seeded.RequestId);
            Assert.AreEqual(HoldOperationState.OperatorRequired, operation.State);
            Assert.AreEqual("hold_identity_ambiguous", operation.LastErrorCode);
            Assert.AreEqual(1, operation.RecoveryAttemptCount);
            Assert.AreEqual(HoldOperationPhase.ReplyStarted, operation.Phase);
            Assert.AreEqual(HoldConversationPapiHandler.Conversation, operation.PolarisRequestGuid);
            Assert.AreEqual(HoldConversationPapiHandler.GroupQualifier, operation.TxnGroupQualifier);
            Assert.AreEqual(HoldConversationPapiHandler.TransactionQualifier, operation.TxnQualifier);
            Assert.AreEqual(RequestStatus.PendingHold,
                (await verify.TitleRequests.SingleAsync(item => item.Id == seeded.RequestId)).Status);
            Assert.AreEqual(eventCountBefore, await verify.TitleRequestEvents.CountAsync(item =>
                item.TitleRequestId == seeded.RequestId));
            Assert.AreEqual(outboxCountBefore, await verify.EmailOutbox.CountAsync());
        }
        finally
        {
            await DeleteRequestAsync(seeded.RequestId);
        }
    }

    private sealed class WorkflowCheckoutProtocolHandler(string checkoutContent) : HttpMessageHandler
    {
        public int CheckoutCalls { get; private set; }
        public int HoldCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            var content = path.Contains("/authenticator/staff", StringComparison.Ordinal)
                ? "{\"PAPIErrorCode\":0,\"AccessToken\":\"checkout-token\",\"AccessSecret\":\"checkout-secret\",\"AuthExpDate\":\"2030-01-01T00:00:00Z\"}"
                : path.Contains("/itemsout/", StringComparison.Ordinal)
                    ? CheckoutResponse()
                    : path.Contains("/holdrequests/", StringComparison.Ordinal)
                        ? HoldResponse()
                        : throw new AssertFailedException("Unexpected production Polaris checkout request: " + path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content),
                RequestMessage = request
            });
        }

        private string CheckoutResponse()
        {
            CheckoutCalls++;
            return checkoutContent;
        }

        private string HoldResponse()
        {
            HoldCalls++;
            return "{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[]}";
        }
    }

    private async Task<string> ReadProviderRequestBarcodeAsync(long requestId)
    {
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
            .CreateDbContextAsync();
        return await context.TitleRequests.Where(item => item.Id == requestId)
            .Select(item => item.Barcode).SingleAsync();
    }

    private static string? GetJsonStringPropertyIgnoreCase(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.GetString();
            }
        }

        Assert.Fail($"The serialized Polaris reply body did not contain {propertyName}: {element}");
        return null;
    }

    private sealed class HoldConversationPapiHandler(
        bool aliasesAgree,
        string barcode,
        string? createResponseOverride = null,
        string? replyResponseOverride = null) : HttpMessageHandler
    {
        public static readonly Guid Conversation = Guid.Parse("c2f29019-c3ea-43dc-9b21-a04389ed00a1");
        public const string GroupQualifier = "durable-group";
        public const string TransactionQualifier = "durable-transaction";
        public string? ReplyBody { get; private set; }
        public string? ReplyUri { get; private set; }
        public int CreateCalls { get; private set; }
        public int ReplyCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            string content;
            if (path.Contains("/authenticator/staff", StringComparison.Ordinal))
            {
                content = "{\"PAPIErrorCode\":0,\"AccessToken\":\"conversation-token\",\"AccessSecret\":\"conversation-secret\",\"AuthExpDate\":\"2030-01-01T00:00:00Z\"}";
            }
            else if (path.EndsWith("/basicdata", StringComparison.Ordinal))
            {
                content = "{\"PAPIErrorCode\":0,\"PatronBasicData\":{" +
                          $"\"PatronID\":7001,\"Barcode\":\"{barcode}\",\"PatronOrgID\":2," +
                          "\"PatronCodeID\":1,\"RequestPickupBranchID\":101}}";
            }
            else if (path.Contains("/organizations/", StringComparison.Ordinal))
            {
                content = "{\"PAPIErrorCode\":0,\"OrganizationsGetRows\":[" +
                          "{\"OrganizationID\":1,\"OrganizationCodeID\":1,\"ParentOrganizationID\":null}," +
                          "{\"OrganizationID\":2,\"OrganizationCodeID\":2,\"ParentOrganizationID\":1,\"Name\":\"Test Library\"}]}";
            }
            else if (path.Contains("/holdrequests/", StringComparison.Ordinal))
            {
                content = "{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[]}";
            }
            else if (path.EndsWith("/holdrequest", StringComparison.Ordinal))
            {
                CreateCalls++;
                content = createResponseOverride ?? ("{\"PAPIErrorCode\":0,\"StatusType\":3,\"StatusValue\":5," +
                          $"\"RequestGUID\":\"{Conversation}\",\"TxnGroupQualifer\":\"{GroupQualifier}\"," +
                          $"\"TxnGroupQualifier\":\"{(aliasesAgree ? GroupQualifier : "wrong-group")}\"," +
                          $"\"TxnQualifier\":\"{TransactionQualifier}\"}}");
            }
            else if (path.EndsWith("/holdrequest/" + Conversation, StringComparison.Ordinal))
            {
                ReplyCalls++;
                ReplyUri = request.RequestUri!.ToString();
                ReplyBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                content = replyResponseOverride ?? ("{\"PAPIErrorCode\":0,\"StatusType\":2,\"StatusValue\":1," +
                          $"\"RequestGUID\":\"{Conversation}\",\"TxnGroupQualifer\":\"{GroupQualifier}\"," +
                          $"\"TxnGroupQualifier\":\"{GroupQualifier}\",\"TxnQualifier\":\"{TransactionQualifier}\"}}");
            }
            else
            {
                throw new AssertFailedException("Unexpected production Polaris conversation request: " + path);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content),
                RequestMessage = request
            };
        }
    }
}
