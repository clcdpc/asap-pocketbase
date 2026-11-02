using System.Net;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PolarisProtocolTests
{
    [TestMethod]
    public async Task PolarisPatronIdRejectsAContradictoryBarcodeAndAcceptsAFormerBarcodeAlias()
    {
        const string requestedBarcode = "20000000001991";
        var mismatchHandler = new ProtectedSequenceResponseHandler(
            (HttpStatusCode.OK,
             "{\"PAPIErrorCode\":0,\"PatronBasicData\":{\"PatronID\":810021," +
             "\"Barcode\":\"20000000001992\",\"PatronOrgID\":2}}"));
        var mismatchProvider = await CreatePolarisProviderAsync(mismatchHandler,
            "patron-id-mismatch-" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(() => mismatchProvider.GetPatronIdAsync(
            requestedBarcode,
            2,
            CancellationToken.None));

        var aliasHandler = new ProtectedSequenceResponseHandler(
            (HttpStatusCode.OK,
             "{\"PAPIErrorCode\":0,\"PatronBasicData\":{\"PatronID\":810022," +
             "\"Barcode\":\"20000000001993\",\"FormerID\":\"20000000001991\",\"PatronOrgID\":2}}"));
        var aliasProvider = await CreatePolarisProviderAsync(aliasHandler,
            "patron-id-former-alias-" + Guid.NewGuid().ToString("N"));

        Assert.AreEqual<int?>(810022, await aliasProvider.GetPatronIdAsync(
            requestedBarcode,
            2,
            CancellationToken.None));
        Assert.AreEqual(1, aliasHandler.RequestUris.Count(uri =>
            uri.AbsolutePath.EndsWith("/basicdata", StringComparison.Ordinal)));
        Assert.IsFalse(aliasHandler.RequestUris.Any(uri =>
            uri.AbsolutePath.Contains("/organizations/", StringComparison.OrdinalIgnoreCase)));

        var projectedHandler = new ProtectedSequenceResponseHandler(
            (HttpStatusCode.OK,
             "{\"PAPIErrorCode\":0,\"PatronBasicData\":{\"PatronID\":810023}}"));
        var projectedProvider = await CreatePolarisProviderAsync(projectedHandler,
            "patron-id-projection-" + Guid.NewGuid().ToString("N"));
        Assert.AreEqual<int?>(810023, await projectedProvider.GetPatronIdAsync(
            requestedBarcode,
            2,
            CancellationToken.None),
            "The identity-only PAPI projection remains valid when it supplies no barcode fields.");
    }

}
