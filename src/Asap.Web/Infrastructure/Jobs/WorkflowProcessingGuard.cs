using Hangfire;
using Hangfire.Storage;

namespace Asap.Web.Infrastructure.Jobs;

public sealed class WorkflowProcessingGuard(JobStorage storage)
{
    public const string ResourceName = "ASAP:WorkflowProcessing";
    private static readonly TimeSpan AcquisitionTimeout = TimeSpan.FromSeconds(1);

    public IDisposable? TryAcquire(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = storage.GetConnection();
        IDisposable? handle = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            handle = connection.AcquireDistributedLock(ResourceName, AcquisitionTimeout);
            cancellationToken.ThrowIfCancellationRequested();
            return new Lease(connection, handle);
        }
        catch (DistributedLockTimeoutException)
        {
            connection.Dispose();
            return null;
        }
        catch
        {
            try
            {
                handle?.Dispose();
            }
            finally
            {
                connection.Dispose();
            }

            throw;
        }
    }

    private sealed class Lease(IDisposable connection, IDisposable handle) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                handle.Dispose();
            }
            finally
            {
                connection.Dispose();
            }
        }
    }
}
