using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Asap.Web.Features.Staff;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task PolarisPackageParallelCreatesAndRepliesKeepDistinctNativeRegistrationRoutes()
    {
        var commands = new[] { new HoldCreateCommand(392001, 392101, 392211, 392201, 99, 42),
            new HoldCreateCommand(392002, 392102, 392212, 392202, 99, 42) };
        using var handler = new ParallelNativePapiHandler(commands);
        var provider = await CreatePolarisProviderAsync(handler, "parallel-native-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var creates = await Task.WhenAll(commands.Select(command => provider.CreateHoldAsync(command, deadline.Token)));
        Assert.IsTrue(creates.All(result => result.Outcome == HoldProviderOutcome.ReplyRequired));
        var replies = await Task.WhenAll(creates.Select((result, index) => provider.ReplyToHoldAsync(new(
            result.RequestGuid!.Value, result.TxnGroupQualifier!, result.TxnQualifier!, commands[index].RequestingOrganizationId), deadline.Token)));
        Assert.IsTrue(replies.All(result => result.Outcome == HoldProviderOutcome.FinalSuccess));
        handler.Verify();
    }

    private sealed class ParallelNativePapiHandler : HttpMessageHandler
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
}
