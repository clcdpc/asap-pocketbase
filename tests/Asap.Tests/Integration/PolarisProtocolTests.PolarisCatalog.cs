using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PolarisProtocolTests
{
    [TestMethod]
    [DataRow("outage")]
    [DataRow("timeout")]
    [DataRow("malformed")]
    public async Task PolarisCatalogMaterialReferenceFailuresKeepTrustworthyResultsAndBoundRetries(string failure)
    {
        var handler = new CatalogMaterialHandler(failure);
        var provider = await CreatePolarisProviderAsync(handler);
        var result = await provider.SearchBibsAsync("title", "Title", "", "", 2, CancellationToken.None);
        var again = await provider.SearchBibsAsync("title", "Title", "", "", 2, CancellationToken.None);
        Assert.AreEqual(3, result.Results.Count);
        Assert.AreEqual(2951, again.TotalMatches);
        Assert.AreEqual("Unknown", result.Results.Single(item => item.BibId == 9001).Format);
        Assert.IsNull(result.Results[0].FormatIconUrl);
        Assert.AreEqual(1, handler.ReferenceCalls);
    }

    [TestMethod]
    public async Task PolarisCatalogMaterialCacheIsSharedBySearchesButHonorsCancellation()
    {
        var handler = new CatalogMaterialHandler();
        var provider = await CreatePolarisProviderAsync(handler);
        await provider.SearchBibsAsync("title", "Title", "", "", 2, CancellationToken.None);
        await provider.SearchBibsAsync("author", "Author", "", "", 2, CancellationToken.None);
        Assert.AreEqual(1, handler.ReferenceCalls);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            provider.SearchBibsAsync("title", "Title", "", "", 2, canceled.Token));
        Assert.AreEqual(1, handler.ReferenceCalls);
    }

}
