namespace Asap.Web.Infrastructure.Development;

public sealed class RuntimeInitializationState
{
    private readonly object _sync = new();
    private readonly SortedSet<string> _errors = new(StringComparer.Ordinal);

    public bool IsHealthy
    {
        get
        {
            lock (_sync)
            {
                return _errors.Count == 0;
            }
        }
    }

    public string? ErrorCode
    {
        get
        {
            lock (_sync)
            {
                return _errors.FirstOrDefault();
            }
        }
    }

    public void MarkFailed(string errorCode)
    {
        lock (_sync)
        {
            _errors.Add(errorCode);
        }
    }
}
