using Asap.Web.Features.Staff;

namespace Asap.Tests.Unit;

[TestClass]
public sealed class StaffBibLookupRequestIdTests
{
    [TestMethod]
    public void ParsesBigintRequestIdentityWithoutPrecisionLoss()
    {
        Assert.IsTrue(TitleRequestEndpoints.TryParseRequestId("9007199254740993", out var parsed));
        Assert.AreEqual(9007199254740993L, parsed);
    }

    [TestMethod]
    public void AllowsMissingRequestIdentityForLibraryScopedResearch()
    {
        Assert.IsTrue(TitleRequestEndpoints.TryParseRequestId(null, out var parsed));
        Assert.IsNull(parsed);
    }

    [TestMethod]
    public void RejectsInvalidRequestIdentityValues()
    {
        foreach (var value in new[] { "", "0", "-1", "not-a-number", "9223372036854775808", " 9" })
        {
            Assert.IsFalse(TitleRequestEndpoints.TryParseRequestId(value, out var parsed), value);
            Assert.IsNull(parsed, value);
        }
    }
}
