using Asap.Web.Infrastructure.Security;

namespace Asap.Tests.Unit;

[TestClass]
public sealed class RecipientDomainPolicyTests
{
    [DataRow("patron@example.org", true)]
    [DataRow("patron@Example.Org", true)]
    [DataRow("patron@other.example", false)]
    [DataRow("patron@staff.example.org", false)]
    [TestMethod]
    public void NonProductionUsesCaseInsensitiveExactDomainMatching(string recipient, bool expected)
    {
        var policy = new RecipientDomainPolicy(
            TestConfigurationFactory.Create(allowedDomains: ["EXAMPLE.ORG"]));

        Assert.AreEqual(expected, policy.IsAllowed(recipient));
    }

    [TestMethod]
    public void ExplicitSubdomainIsAllowed()
    {
        var policy = new RecipientDomainPolicy(
            TestConfigurationFactory.Create(allowedDomains: ["staff.example.org"]));

        Assert.IsTrue(policy.IsAllowed("patron@staff.example.org"));
    }

    [TestMethod]
    public void MissingOrEmptyListAllowsNobodyInNonProduction()
    {
        var missing = new RecipientDomainPolicy(TestConfigurationFactory.Create(allowedDomains: null));
        var empty = new RecipientDomainPolicy(TestConfigurationFactory.Create(allowedDomains: []));

        Assert.IsFalse(missing.IsAllowed("patron@example.org"));
        Assert.IsFalse(empty.IsAllowed("patron@example.org"));
    }

    [TestMethod]
    public void ProductionDoesNotApplyDomainRestriction()
    {
        var policy = new RecipientDomainPolicy(
            TestConfigurationFactory.Create(isNonProduction: false, allowedDomains: []));

        Assert.IsTrue(policy.IsAllowed("patron@outside.example"));
    }
}
