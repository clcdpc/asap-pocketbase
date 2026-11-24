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
        var result = await provider.SearchBibsAsync("title", "Title", "", "", 2, 1, CancellationToken.None);
        var again = await provider.SearchBibsAsync("title", "Title", "", "", 2, 1, CancellationToken.None);
        Assert.AreEqual(3, result.Results.Count);
        Assert.AreEqual(2951, again.TotalMatches);
        Assert.AreEqual("Unknown", result.Results.Single(item => item.BibId == 9001).Format);
        Assert.AreEqual(53, result.Results.Single(item => item.BibId == 9001).MaterialTypeId);
        Assert.AreEqual(1, handler.ReferenceCalls);
    }

    [TestMethod]
    public async Task PolarisCatalogMaterialCacheIsSharedBySearchesButHonorsCancellation()
    {
        var handler = new CatalogMaterialHandler();
        var provider = await CreatePolarisProviderAsync(handler);
        var result = await provider.SearchBibsAsync("title", "Title", "", "", 2, 1, CancellationToken.None);
        var row = result.Results.Single(item => item.BibId == 9001);
        Assert.AreEqual("Large print", row.Format);
        Assert.AreEqual(53, row.MaterialTypeId);
        Assert.AreEqual("Book", result.Results.Single(item => item.BibId == 9002).Format);
        Assert.AreEqual("Unknown", result.Results.Single(item => item.BibId == 9003).Format);
        Assert.AreEqual(2951, result.TotalMatches);
        var exact = await provider.ValidateBibAsync(9001, 2, CancellationToken.None);
        Assert.AreEqual(53, exact.MaterialTypeId);
        Assert.AreEqual("Large print", exact.Format);
        await provider.SearchBibsAsync("author", "Author", "", "", 2, 1, CancellationToken.None);
        Assert.AreEqual(1, handler.ReferenceCalls);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            provider.SearchBibsAsync("title", "Title", "", "", 2, 1, canceled.Token));
        Assert.AreEqual(1, handler.ReferenceCalls);
    }

}
