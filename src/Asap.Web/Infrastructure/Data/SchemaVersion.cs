namespace Asap.Web.Infrastructure.Data;

public sealed class SchemaVersion
{
    public const int ExpectedVersion = 3;

    public byte Id { get; set; }

    public int Version { get; set; }

    public DateTime UpdatedUtc { get; set; }
}
