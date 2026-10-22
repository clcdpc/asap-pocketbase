namespace Asap.Web.Infrastructure.Configuration;

public sealed record ConfigurationLoadResult(
    ExternalConfiguration? Value,
    string? SourcePath,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Value is not null && Errors.Count == 0;

    public static ConfigurationLoadResult Invalid(params string[] errors) =>
        new(null, null, errors);
}
