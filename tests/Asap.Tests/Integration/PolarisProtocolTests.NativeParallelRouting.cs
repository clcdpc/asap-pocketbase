using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Asap.Web.Features.Staff;

namespace Asap.Tests.Integration;

public sealed partial class PolarisProtocolTests
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

}
