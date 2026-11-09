using System.Runtime.ExceptionServices;
using Microsoft.Data.SqlClient;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    private async Task<(TFirst First, TSecond Second)> RunObservedStaffRaceAsync<TFirst, TSecond>(
        SqlConnection blocker, SqlTransaction transaction,
        Func<CancellationToken, Task<TFirst>> startFirst, Func<CancellationToken, Task<TSecond>> startSecond)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var inFlight = new List<Task>();
        Task<TFirst>? first = null;
        Task<TSecond>? second = null;
        Exception? failure = null;
        try
        {
            first = startFirst(deadline.Token);
            inFlight.Add(first);
            var firstSession = await WaitForCorrectiveSqlBlockAsync(blocker.ServerProcessId, deadline.Token);
            second = startSecond(deadline.Token);
            inFlight.Add(second);
            var secondSession = await WaitForCorrectiveSqlBlockAsync(firstSession, deadline.Token);
            TestContext.WriteLine($"Observed SQL blocking chain: {blocker.ServerProcessId} <- {firstSession} <- {secondSession}");
            await transaction.CommitAsync(deadline.Token);
        }
        catch (Exception error)
        {
            failure = error;
            await deadline.CancelAsync();
        }
        finally
        {
            if (transaction.Connection is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await transaction.RollbackAsync(cleanup.Token);
                }
                catch (Exception error)
                {
                    if (failure is null)
                    {
                        failure = error;
                    }
                    else
                    {
                        failure.Data["BlockerCleanupFailure"] = error.ToString();
                    }
                }
            }
        }
        // Release the blocker and join every started operation even if either
        // observation failed. Cancellation is passed into both real services.
        try
        {
            await Task.WhenAll(inFlight);
        }
        catch (Exception error) when (failure is not null)
        {
            failure.Data["OperationCleanupFailure"] = error.ToString();
        }
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return (await first!, await second!);
    }
}
