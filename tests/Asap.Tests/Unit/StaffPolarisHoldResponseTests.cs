using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Clc.Polaris.Api.Models;

namespace Asap.Tests.Unit;

[TestClass]
public sealed class StaffPolarisHoldResponseTests
{
    private static readonly JsonSerializerOptions PackageJson = new() { PropertyNameCaseInsensitive = true };

    [TestMethod]
    public async Task TestingHoldIdentityIsIndependentOfBibAndDoesNotOverflowInt32()
    {
        var provider = new Asap.Web.Infrastructure.Testing.DeterministicTestingPatronProvider();
        var command = new HoldCreateCommand(7001, int.MaxValue, 101, 2, 99, 42);
        provider.ExpectCreate(command, new(HoldProviderOutcome.FinalSuccess, Guid.Parse("3e07e5a9-4b5e-45dc-8ad7-bca22a68ea5c"), 100001, null, null, 2, 1, "testing_success"));
        provider.ExpectCreate(command, new(HoldProviderOutcome.FinalSuccess, Guid.Parse("599d28e8-3f5f-4d37-8325-e217b7f4cadf"), 100002, null, null, 2, 1, "testing_success"));
        var first = await provider.CreateHoldAsync(command, CancellationToken.None);
        var second = await provider.CreateHoldAsync(command, CancellationToken.None);
        Assert.IsTrue(first.HoldRequestId is > 0);
        Assert.IsTrue(second.HoldRequestId is > 0);
        Assert.AreNotEqual(first.HoldRequestId, second.HoldRequestId);
        Assert.IsTrue(first.RequestGuid.HasValue);
        Assert.AreEqual(int.MaxValue, command.BibId);
    }

    [TestMethod]
    public void CreateReplyRequiredMapsTypedConversationWithoutInventingFinalIdentity()
    {
        var guid = Guid.Parse("f782725d-1201-4390-a89a-1c047b2fa865");
        var json = $$"""{"PAPIErrorCode":0,"StatusType":3,"StatusValue":5,"RequestGUID":"{{guid}}","TxnGroupQualifer":"group","TxnQualifier":"qualifier","QueuePosition":7}""";
        var data = JsonSerializer.Deserialize<HoldRequestCreateResult>(json, PackageJson)!;
        var result = PolarisPatronProvider.NormalizeHoldResponse(true, json, data, false);
        Assert.AreEqual(HoldProviderOutcome.ReplyRequired, result.Outcome);
        Assert.AreEqual(guid, result.RequestGuid);
        Assert.IsNull(result.HoldRequestId);
        Assert.AreEqual("group", result.TxnGroupQualifier);
        Assert.AreEqual("qualifier", result.TxnQualifier);
        Assert.AreEqual(3, result.StatusType);
        Assert.AreEqual(5, result.StatusValue);
        Assert.AreEqual(7, data.QueuePosition);
    }

    [TestMethod]
    [DataRow(false, 0, HoldProviderOutcome.DefinitiveNoEffect)]
    [DataRow(true, 0, HoldProviderOutcome.DefinitiveNoEffect)]
    [DataRow(false, 1, HoldProviderOutcome.FinalSuccess)]
    [DataRow(true, 1, HoldProviderOutcome.FinalSuccess)]
    public void StatusTypeTwoUsesOneForPlacementSuccess(bool reply, int value, HoldProviderOutcome expected)
    {
        var guid = Guid.Parse("f782725d-1201-4390-a89a-1c047b2fa865");
        var json = $$"""{"PAPIErrorCode":0,"StatusType":2,"StatusValue":{{value}},"RequestGUID":"{{guid}}","HoldRequestID":78123}""";
        var data = JsonSerializer.Deserialize<HoldRequestReplyResult>(json, PackageJson)!;
        var result = PolarisPatronProvider.NormalizeHoldResponse(true, json, data, reply,
            reply ? new HoldReplyCommand(guid, "group", "qualifier", 2) : null);
        Assert.AreEqual(expected, result.Outcome);
        Assert.AreEqual(guid, result.RequestGuid);
        Assert.IsNull(result.HoldRequestId, "An undocumented response field cannot establish final hold identity.");
        if (reply)
        {
            Assert.AreEqual("group", result.TxnGroupQualifier);
        }
    }

    [TestMethod]
    public void DocumentedCreateRejectionIsDefinitiveNoEffect()
    {
        const string json = """{"PAPIErrorCode":0,"StatusType":1,"StatusValue":-4006}""";
        var result = PolarisPatronProvider.NormalizeHoldResponse(true, json,
            JsonSerializer.Deserialize<HoldRequestCreateResult>(json, PackageJson), false);
        Assert.AreEqual(HoldProviderOutcome.DefinitiveNoEffect, result.Outcome);
        Assert.AreEqual("provider_status_-4006", result.SafeErrorCode);
    }

    [TestMethod]
    [DataRow("not-json")]
    [DataRow("""{"PAPIErrorCode":0,"StatusType":2}""")]
    [DataRow("""{"PAPIErrorCode":0,"StatusType":2,"StatusValue":1,"statusvalue":0}""")]
    [DataRow("""{"PAPIErrorCode":0,"StatusType":2,"StatusValue":"1"}""")]
    public void MissingDuplicateOrMalformedOutcomeCannotBecomeSuccess(string json)
    {
        var result = PolarisPatronProvider.NormalizeHoldResponse(true, json,
            new HoldRequestCreateResult { StatusType = 2, StatusValue = 1 }, false);
        Assert.AreEqual(HoldProviderOutcome.Ambiguous, result.Outcome);
        Assert.AreEqual("provider_protocol_error", result.SafeErrorCode);
    }

    [TestMethod]
    public void TypedAndRawOutcomeMustAgree()
    {
        const string json = """{"PAPIErrorCode":0,"StatusType":2,"StatusValue":0}""";
        var result = PolarisPatronProvider.NormalizeHoldResponse(true, json,
            new HoldRequestCreateResult { StatusType = 2, StatusValue = 1 }, true);
        Assert.AreEqual(HoldProviderOutcome.Ambiguous, result.Outcome);
    }

    [TestMethod]
    public void UnknownStatusIsAmbiguousAndOutboundQualifierUsesCorrectSpelling()
    {
        const string json = """{"PAPIErrorCode":0,"StatusType":9,"StatusValue":99}""";
        var result = PolarisPatronProvider.NormalizeHoldResponse(true, json,
            JsonSerializer.Deserialize<HoldRequestCreateResult>(json, PackageJson), false);
        Assert.AreEqual(HoldProviderOutcome.Ambiguous, result.Outcome);
        Assert.AreEqual("provider_status_unclassified", result.SafeErrorCode);
        using var outbound = JsonDocument.Parse(JsonSerializer.Serialize(new HoldRequestReplyData { TxnGroupQualifier = "group" }));
        Assert.AreEqual("group", outbound.RootElement.GetProperty("TxnGroupQualifier").GetString());
        Assert.IsFalse(outbound.RootElement.TryGetProperty("TxnGroupQualifer", out _));
    }
}
