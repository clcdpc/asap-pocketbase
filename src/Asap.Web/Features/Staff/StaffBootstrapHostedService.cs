using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Development;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Staff;

public sealed class StaffBootstrapHostedService(
    IDbContextFactory<AsapDbContext> contextFactory,
    ExternalConfiguration configuration,
    StaffEligibilityService eligibility,
    RuntimeInitializationState initializationState,
    ILogger<StaffBootstrapHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!initializationState.IsHealthy)
        {
            return;
        }

        try
        {
            await BootstrapEmptyTableAsync(cancellationToken);
            if (!await eligibility.HasUsableSuperAdminAsync(cancellationToken))
            {
                initializationState.MarkFailed("usable_super_admin_required");
                logger.LogError("Staff startup gate found no usable super administrator for the loaded tenant policy.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            initializationState.MarkFailed("staff_startup_validation_failed");
            logger.LogError(exception, "Staff bootstrap/current-policy validation failed.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task BootstrapEmptyTableAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "EXEC sp_getapplock @Resource=N'ASAP:StaffBootstrap', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=10000;",
            cancellationToken);
        if (await context.StaffUsers.AnyAsync(cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var options = configuration.Authentication.Entra.InitialSuperAdmin;
        var tenantId = Guid.Parse(options.TenantId!);
        var objectId = Guid.Parse(options.ObjectId!);
        context.StaffUsers.Add(new StaffUser
        {
            EntraTenantId = tenantId,
            EntraObjectId = objectId,
            UserPrincipalName = options.UserPrincipalName!.Trim(),
            NormalizedUserPrincipalName = options.UserPrincipalName.Trim().ToUpperInvariant(),
            DisplayName = options.DisplayName!.Trim(),
            NotificationEmail = NormalizeEmail(options.NotificationEmail),
            Role = "super_admin",
            OrganizationId = 1,
            IsActive = true,
            LastLoginUtc = null
        });
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Created the configured initial super administrator because StaffUser was empty.");
    }

    private static string? NormalizeEmail(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}
