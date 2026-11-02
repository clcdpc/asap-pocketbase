using System.Net;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PolarisProtocolTests
{
    [TestMethod]
    public async Task PolarisPatronIdReadUsesOnlyTheBasicPatronOperation()
    {
        const string barcode = "20000000000036";
        var handler = new ProtectedSequenceResponseHandler(
            (HttpStatusCode.OK,
             """{"PAPIErrorCode":0,"PatronBasicData":{"PatronID":123,"Barcode":"20000000000036","PatronOrgID":300}}"""));
        var provider = await CreatePolarisProviderAsync(handler);

        var patronId = await provider.GetPatronIdAsync(barcode, 2, CancellationToken.None);

        Assert.AreEqual<int?>(123, patronId);
        var patronReads = handler.RequestUris.Count(uri =>
            uri.AbsolutePath.Contains($"/patron/{barcode}", StringComparison.Ordinal));
        var staffAuthentications = handler.RequestUris.Count(uri =>
            uri.AbsolutePath.Contains("/authenticator/staff", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(1, patronReads, "Exactly one basic patron-data read should resolve the patron ID.");
        Assert.IsTrue(staffAuthentications <= 1, "At most one protected-token handshake should be needed.");
        Assert.AreEqual(patronReads + staffAuthentications, handler.RequestCount,
            "The patron-ID operation should not make unrelated Polaris calls.");
        Assert.IsFalse(handler.RequestUris.Any(uri =>
            uri.AbsolutePath.Contains("/organizations", StringComparison.OrdinalIgnoreCase)));
    }

}
