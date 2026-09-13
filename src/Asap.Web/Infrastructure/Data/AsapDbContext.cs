using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Asap.Web.Infrastructure.Data;

public sealed class AsapDbContext(DbContextOptions<AsapDbContext> options) : DbContext(options)
{
    public DbSet<SchemaVersion> SchemaVersions => Set<SchemaVersion>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<StaffUser> StaffUsers => Set<StaffUser>();
    public DbSet<SystemSettings> SystemSettings => Set<SystemSettings>();
    public DbSet<PatronEmbedAllowedOrigin> PatronEmbedAllowedOrigins => Set<PatronEmbedAllowedOrigin>();
    public DbSet<PolarisSettings> PolarisSettings => Set<PolarisSettings>();
    public DbSet<WorkflowSettings> WorkflowSettings => Set<WorkflowSettings>();
    public DbSet<PatronSettings> PatronSettings => Set<PatronSettings>();
    public DbSet<EmailSettings> EmailSettings => Set<EmailSettings>();
    public DbSet<CommonCreatorSet> CommonCreatorSets => Set<CommonCreatorSet>();
    public DbSet<CommonCreatorTerm> CommonCreatorTerms => Set<CommonCreatorTerm>();
    public DbSet<PatronCodeEligibilitySet> PatronCodeEligibilitySets => Set<PatronCodeEligibilitySet>();
    public DbSet<PatronCodeEligibilityMember> PatronCodeEligibilityMembers => Set<PatronCodeEligibilityMember>();
    public DbSet<PublicationOptionSet> PublicationOptionSets => Set<PublicationOptionSet>();
    public DbSet<PublicationOption> PublicationOptions => Set<PublicationOption>();
    public DbSet<ExternalSearchProvider> ExternalSearchProviders => Set<ExternalSearchProvider>();
    public DbSet<ExternalSearchProviderOverride> ExternalSearchProviderOverrides => Set<ExternalSearchProviderOverride>();
    public DbSet<PatronCustomField> PatronCustomFields => Set<PatronCustomField>();
    public DbSet<PatronCustomFieldOption> PatronCustomFieldOptions => Set<PatronCustomFieldOption>();
    public DbSet<Branding> Branding => Set<Branding>();
    public DbSet<MaterialFormat> MaterialFormats => Set<MaterialFormat>();
    public DbSet<MaterialFormatOverride> MaterialFormatOverrides => Set<MaterialFormatOverride>();
    public DbSet<MaterialFormatCustomFieldRule> MaterialFormatCustomFieldRules => Set<MaterialFormatCustomFieldRule>();
    public DbSet<FormatAutoClaimRule> FormatAutoClaimRules => Set<FormatAutoClaimRule>();
    public DbSet<PatronSession> PatronSessions => Set<PatronSession>();
    public DbSet<TitleRequest> TitleRequests => Set<TitleRequest>();
    public DbSet<AdditionalCopyRequest> AdditionalCopyRequests => Set<AdditionalCopyRequest>();
    public DbSet<TitleRequestEvent> TitleRequestEvents => Set<TitleRequestEvent>();
    public DbSet<WorkflowTag> WorkflowTags => Set<WorkflowTag>();
    public DbSet<TitleRequestWorkflowTag> TitleRequestWorkflowTags => Set<TitleRequestWorkflowTag>();
    public DbSet<EmailTemplate> EmailTemplates => Set<EmailTemplate>();
    public DbSet<EmailOutbox> EmailOutbox => Set<EmailOutbox>();
    public DbSet<EmailDeliveryEvent> EmailDeliveryEvents => Set<EmailDeliveryEvent>();
    public DbSet<DeletedRequestAudit> DeletedRequestAudits => Set<DeletedRequestAudit>();
    public DbSet<AdministrativeAudit> AdministrativeAudits => Set<AdministrativeAudit>();
    public DbSet<HoldPlacementOperation> HoldPlacementOperations => Set<HoldPlacementOperation>();
    public DbSet<LegacyPocketBaseMapping> LegacyPocketBaseMappings => Set<LegacyPocketBaseMapping>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("asap");

        modelBuilder.Entity<SchemaVersion>(entity =>
        {
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Id).ValueGeneratedNever();
            entity.Property(value => value.UpdatedUtc).HasColumnType("datetime2(7)");
        });

        modelBuilder.Entity<Organization>().Property(value => value.Id).ValueGeneratedNever();
        modelBuilder.Entity<SystemSettings>().HasKey(value => value.OrganizationId);
        modelBuilder.Entity<PolarisSettings>().HasKey(value => value.OrganizationId);
        modelBuilder.Entity<WorkflowSettings>().HasKey(value => value.OrganizationId);
        modelBuilder.Entity<PatronSettings>().HasKey(value => value.OrganizationId);
        modelBuilder.Entity<EmailSettings>().HasKey(value => value.OrganizationId);
        modelBuilder.Entity<CommonCreatorSet>().HasKey(value => value.OrganizationId);
        modelBuilder.Entity<PatronCodeEligibilitySet>().HasKey(value => value.OrganizationId);
        modelBuilder.Entity<PatronCodeEligibilityMember>()
            .HasKey(value => new { value.OrganizationId, value.PatronCodeId });
        modelBuilder.Entity<PublicationOptionSet>().HasKey(value => value.OrganizationId);
        modelBuilder.Entity<ExternalSearchProviderOverride>()
            .HasKey(value => new { value.LibraryOrganizationId, value.ExternalSearchProviderId });
        modelBuilder.Entity<Branding>().HasKey(value => value.OrganizationId);
        modelBuilder.Entity<TitleRequestWorkflowTag>()
            .HasKey(value => new { value.TitleRequestId, value.WorkflowTagId });
        modelBuilder.Entity<LegacyPocketBaseMapping>()
            .HasKey(value => new { value.EntityType, value.PocketBaseId });

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            entityType.SetTableName(entityType.ClrType.Name);
            entityType.SetSchema("asap");

            var rowVersion = entityType.FindProperty("RowVersion");
            if (rowVersion is null)
            {
                continue;
            }

            rowVersion.IsConcurrencyToken = true;
            rowVersion.ValueGenerated = ValueGenerated.OnAddOrUpdate;
            rowVersion.SetColumnType("rowversion");
        }
    }
}
