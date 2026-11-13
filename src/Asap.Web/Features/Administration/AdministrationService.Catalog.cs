using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Administration;

public sealed partial class AdministrationService
{
    private static void ValidateCatalogPatch(CatalogSettingsPatch patch)
    {
        if (patch.PacBaseUrl.IsSupplied &&
            ((patch.PacBaseUrl.Value?.Length ?? 0) > 2048 || !CatalogPresentation.IsPacBaseUrlValid(patch.PacBaseUrl.Value)))
        {
            throw new AdministrationInputException("PAC base URL must be an HTTP(S) URL without credentials, query, fragment or placeholders. Leave blank to disable base-URL patterns.");
        }
        if (patch.FormatIconUrlPattern.IsSupplied && !CatalogPresentation.IsIconPatternValid(patch.FormatIconUrlPattern.Value))
        {
            throw new AdministrationInputException("Material icon pattern must be HTTP(S) or a single-slash local path. Supported tokens: {pacBaseUrl} at the start, {pacOrgId}, {id}, {id2}, {MARCTypeOfMaterialID}, {MARCTypeOfMaterialID2}, {SearchCode}. Tokens cannot form a URL host or scheme.");
        }
        if (patch.PacOrganizationId.IsSupplied && patch.PacOrganizationId.Value is <= 0)
        {
            throw new AdministrationInputException("PAC organization ID must be a positive integer or unset.");
        }
    }

    private static void ApplyCatalogPatch(SystemSettings system, LibraryCatalogSettings library, int organizationId, CatalogSettingsPatch patch)
    {
        ValidateCatalogPatch(patch);
        if (organizationId == LibraryScope.SystemOrganizationId)
        {
            if (patch.PacOrganizationId.IsSupplied)
            {
                throw new AdministrationInputException("PAC organization ID belongs to a specific library.");
            }
            Apply(patch.PacBaseUrl, value => system.PacBaseUrl = value?.Trim());
            Apply(patch.FormatIconUrlPattern, value => system.MaterialTypeIconUrlPattern = value?.Trim());
            return;
        }
        Apply(patch.PacBaseUrl, value => library.PacBaseUrl = value?.Trim());
        Apply(patch.FormatIconUrlPattern, value => library.MaterialTypeIconUrlPattern = value?.Trim());
        Apply(patch.PacOrganizationId, value => library.PacOrganizationId = value);
    }

    private static async Task ApplyCatalogSettingsAsync(
        AsapDbContext context, int organizationId, CatalogSettingsPatch patch, CancellationToken cancellationToken)
    {
        if (!patch.HasChanges)
        {
            return;
        }
        var system = await context.SystemSettings.SingleAsync(item => item.OrganizationId == 1, cancellationToken);
        var library = await context.LibraryCatalogSettings.SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken)
            ?? new LibraryCatalogSettings { OrganizationId = organizationId };
        ApplyCatalogPatch(system, library, organizationId, patch);
        if (organizationId != 1 && context.Entry(library).State == EntityState.Detached)
        {
            context.LibraryCatalogSettings.Add(library);
        }
    }

    public async Task<AdministrationResult> PreviewCatalogAsync(
        CurrentStaff actor, AdministrationSettingsCommand command, int materialTypeId, string? searchCode,
        CancellationToken cancellationToken)
    {
        if (command.BindingError is not null)
        {
            return new AdministrationResult(command.BindingError);
        }
        if (!TryResolveScope(actor, command.Scope, out var organizationId, out var failure))
        {
            return failure;
        }
        if (materialTypeId <= 0 || (searchCode?.Length ?? 0) > 256)
        {
            throw new AdministrationInputException("Preview requires a positive material-type ID and a search code of at most 256 characters.");
        }
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken);
        var locked = await LockAndRevalidateActorAsync(context, actor, organizationId, [], cancellationToken);
        if (locked.Failure is not null)
        {
            return locked.Failure;
        }
        var system = await context.SystemSettings.AsNoTracking().SingleAsync(item => item.OrganizationId == 1, cancellationToken);
        var library = await context.LibraryCatalogSettings.AsNoTracking().SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken)
            ?? new LibraryCatalogSettings { OrganizationId = organizationId };
        ApplyCatalogPatch(system, library, organizationId, command.Catalog);
        var effective = CatalogPresentation.Resolve(system, organizationId == 1 ? null : library);
        return new AdministrationResult("ok", new
        {
            orgId = organizationId == 1 ? "system" : organizationId.ToString(),
            effective,
            formatIconUrl = CatalogPresentation.IconUrl(effective, materialTypeId, searchCode),
            materialTypeId,
            materialTypeSearchCode = searchCode
        });
    }
}
