using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Email;

public sealed record EmailServerTokenResolution(string? Token, string? Code)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Token);
}

public interface IEmailServerTokenResolver
{
    Task<EmailServerTokenResolution> ResolveAsync(
        int organizationId,
        CancellationToken cancellationToken);
}

public sealed class EmailServerTokenResolver(
    IDbContextFactory<AsapDbContext> contextFactory,
    IntegrationCredentialProtector credentialProtector) : IEmailServerTokenResolver
{
    public async Task<EmailServerTokenResolution> ResolveAsync(
        int organizationId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var system = await context.EmailSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == 1, cancellationToken);
        var library = organizationId == 1
            ? null
            : await context.EmailSettings.AsNoTracking()
                .SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);

        // EmailSettings is a system default with a library override. A blank library
        // token means "inherit"; a non-empty but undecryptable override is invalid and
        // must not silently fall back to the system credential.
        var protectedToken = Clean(library?.ProtectedServerToken) ?? Clean(system?.ProtectedServerToken);
        if (protectedToken is null)
        {
            return new EmailServerTokenResolution(null, "mail_credentials_missing");
        }

        try
        {
            var token = credentialProtector.Unprotect(protectedToken);
            return string.IsNullOrWhiteSpace(token)
                ? new EmailServerTokenResolution(null, "mail_credentials_invalid")
                : new EmailServerTokenResolution(token.Trim(), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new EmailServerTokenResolution(null, "mail_credentials_invalid");
        }
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
