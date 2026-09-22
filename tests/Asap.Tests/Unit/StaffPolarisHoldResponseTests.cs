using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;

namespace Asap.Tests.Unit;

[TestClass]
public sealed class StaffPolarisHoldResponseTests
{
    [TestMethod]
    public void CreateReplyRequiredUsesRawQualifierAliasWithoutInventingFinalId()
    {
        var requestGuid = Guid.NewGuid();
        var result = PolarisPatronProvider.NormalizeHoldResponse(
            httpSucceeded: true,
            $$"""
            {
              "PAPIErrorCode": 0,
              "StatusType": 3,
              "StatusValue": 5,
              "RequestGUID": "{{requestGuid}}",
              "TxnGroupQualifer": "group-from-provider",
              "TxnQualifier": "qualifier-from-provider"
            }
            """,
            typedStatusType: null,
            typedStatusValue: null,
            typedRequestGuid: null,
            typedTxnGroupQualifier: null,
            typedTxnQualifier: null,
            isReply: false);

        Assert.AreEqual(HoldProviderOutcome.ReplyRequired, result.Outcome);
        Assert.AreEqual(requestGuid.ToString(), result.RequestGuid);
        Assert.IsNull(result.HoldRequestId);
        Assert.AreEqual("group-from-provider", result.TxnGroupQualifier);
        Assert.AreEqual("qualifier-from-provider", result.TxnQualifier);
    }

    [TestMethod]
    public void SuccessfulReplyKeepsFinalHoldIdSeparateFromRequestGuid()
    {
        var requestGuid = Guid.NewGuid();
        var result = PolarisPatronProvider.NormalizeHoldResponse(
            httpSucceeded: true,
            $$"""
            {
              "PAPIErrorCode": 0,
              "StatusType": 2,
              "StatusValue": 0,
              "RequestGuid": "{{requestGuid}}",
              "HoldRequestID": 78123
            }
            """,
            typedStatusType: null,
            typedStatusValue: null,
            typedRequestGuid: null,
            typedTxnGroupQualifier: "typed-group",
            typedTxnQualifier: "typed-qualifier",
            isReply: true);

        Assert.AreEqual(HoldProviderOutcome.FinalSuccess, result.Outcome);
        Assert.AreEqual(requestGuid.ToString(), result.RequestGuid);
        Assert.AreEqual("78123", result.HoldRequestId);
        Assert.AreEqual("typed-group", result.TxnGroupQualifier);
        Assert.AreEqual("typed-qualifier", result.TxnQualifier);
    }

    [TestMethod]
    public void DocumentedCreateRejectionIsDefinitiveNoEffect()
    {
        var result = PolarisPatronProvider.NormalizeHoldResponse(
            httpSucceeded: true,
            """{"PAPIErrorCode":0,"StatusType":1,"StatusValue":-4006}""",
            typedStatusType: null,
            typedStatusValue: null,
            typedRequestGuid: null,
            typedTxnGroupQualifier: null,
            typedTxnQualifier: null,
            isReply: false);

        Assert.AreEqual(HoldProviderOutcome.DefinitiveNoEffect, result.Outcome);
        Assert.AreEqual("provider_status_-4006", result.SafeErrorCode);
    }

    [TestMethod]
    public void UnknownOrMalformedResponseIsAmbiguous()
    {
        var unclassified = PolarisPatronProvider.NormalizeHoldResponse(
            httpSucceeded: true,
            """{"PAPIErrorCode":0,"StatusType":9,"StatusValue":99}""",
            typedStatusType: null,
            typedStatusValue: null,
            typedRequestGuid: null,
            typedTxnGroupQualifier: null,
            typedTxnQualifier: null,
            isReply: false);
        var malformed = PolarisPatronProvider.NormalizeHoldResponse(
            httpSucceeded: true,
            "not-json",
            typedStatusType: null,
            typedStatusValue: null,
            typedRequestGuid: null,
            typedTxnGroupQualifier: null,
            typedTxnQualifier: null,
            isReply: true);

        Assert.AreEqual(HoldProviderOutcome.Ambiguous, unclassified.Outcome);
        Assert.AreEqual("provider_status_unclassified", unclassified.SafeErrorCode);
        Assert.AreEqual(HoldProviderOutcome.Ambiguous, malformed.Outcome);
        Assert.AreEqual("provider_protocol_error", malformed.SafeErrorCode);
    }
}
