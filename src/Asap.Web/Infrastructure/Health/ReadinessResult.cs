namespace Asap.Web.Infrastructure.Health;

public sealed record ReadinessResult(bool IsReady, string? ErrorCode = null)
{
    public static ReadinessResult Ready { get; } = new(true);

    public static ReadinessResult NotReady(string errorCode) => new(false, errorCode);
}
