using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Infrastructure.Data;

public sealed class AsapDbContext(DbContextOptions<AsapDbContext> options) : DbContext(options)
{
    public DbSet<SchemaVersion> SchemaVersions => Set<SchemaVersion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SchemaVersion>(entity =>
        {
            entity.ToTable("SchemaVersion", "asap");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Id).ValueGeneratedNever();
            entity.Property(value => value.UpdatedUtc).HasColumnType("datetime2(7)");
        });
    }
}
