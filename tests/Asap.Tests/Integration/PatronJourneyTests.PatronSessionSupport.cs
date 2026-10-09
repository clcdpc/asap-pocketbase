using Asap.Web.Features.Patron;
using Asap.Web.Infrastructure.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    private async Task<PatronSessionContext> IssueTestPatronSessionAsync(
        string barcode,
        int organizationId = 2,
        int? nativePatronId = null)
    {
        var provider = factory!.Services.GetRequiredService<DeterministicTestingPatronProvider>();
        var verifiedNativePatronId = nativePatronId ??
            await provider.GetPatronIdAsync(barcode, organizationId, CancellationToken.None);
        Assert.IsTrue(verifiedNativePatronId is > 0,
            $"The fixture provider must declare a verified positive native patron ID for {barcode} in library {organizationId}.");
        var issued = await factory!.Services.GetRequiredService<PatronSessionService>()
            .IssueAsync(barcode, verifiedNativePatronId!.Value, organizationId, organizationId, organizationId,
                CancellationToken.None);
        Assert.IsNotNull(issued, "The test patron session requires an active native library.");
        return issued.Context;
    }

    private static Task DeleteTestPatronSessionAsync(long sessionId) => ExecuteNonQueryAsync(
        "DELETE FROM [asap].[PatronSession] WHERE [Id] = @id;",
        ("@id", sessionId));
}
