using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Testing;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Authentication;

[assembly: HostingStartup(typeof(Asap.ReviewProviders.ReviewStartup))]

namespace Asap.ReviewProviders;

// Loaded only by the disposable acceptance harness. This assembly is never published
// with ASAP. Unknown provider inputs retain the application's strict failure.
public sealed class ReviewStartup : IHostingStartup
{
    public void Configure(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddHostedService<ReviewFixtures>();
            services.AddTransient<IStartupFilter, ReviewEvidenceEndpoint>();
            services.AddTransient<IStartupFilter, ReviewAuthenticationEndpoint>();
            if (Environment.GetEnvironmentVariable("REVIEW_AUTH_MODE") == "Local")
            {
                services.PostConfigure<AuthenticationOptions>(options =>
                    options.DefaultChallengeScheme = StaffAuthenticationRegistration.CookieScheme);
            }
        });
    }
}

internal sealed class ReviewFixtures(
    IHostEnvironment environment, DeterministicTestingPatronProvider provider,
    IDbContextFactory<AsapDbContext> contexts, IntegrationCredentialProtector protector) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!environment.IsEnvironment("Testing") ||
            Environment.GetEnvironmentVariable("Testing__UseDeterministicPatronProvider") != "true")
        {
            throw new InvalidOperationException("Review fixtures require the deterministic Testing runtime.");
        }
        // Readiness validates configured Polaris credentials even with a deterministic
        // provider. Populate synthetic values through the normal protection contract.
        await using var database = await contexts.CreateDbContextAsync(cancellationToken);
        var settings = await database.PolarisSettings.SingleAsync(item => item.OrganizationId == 1, cancellationToken);
        settings.Host = "https://polaris.invalid";
        settings.AccessId = "acceptance";
        settings.ProtectedApiKey = protector.Protect("synthetic-api-key");
        settings.StaffDomain = "acceptance";
        settings.AdminUser = "synthetic";
        settings.ProtectedAdminPassword = protector.Protect("synthetic-password");
        settings.WorkstationId = 1;
        settings.SystemPolarisUserId = 1;
        await database.SaveChangesAsync(cancellationToken);
        provider.SetReferenceData(
            [new(1, "System", null, 0, null), new(2, "Review Library", "REVIEW", 2, 1)],
            [new(1, "Adult"), new(17, "Branch use card"), new(27, "Consortium eCard")]);
        for (var number = 41001; number <= 41011; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            provider.AddPatron(new(number, $"ACCEPTANCE{number}", "acceptance@example.org",
                "Synthetic", "Reviewer", 1, "Adult", 101, 2, "Review Library", 101),
                [new(101, "Main Library")], 2);
        }
        for (var number = 1001; number <= 1005; number++)
        {
            provider.AddPatron(new(number, $"REVIEW{number}", "patron@example.org",
                "Review", "Patron", 1, "Adult", 101, 2, "Review Library", 101),
                [new(101, "Main Library"), new(102, "North Branch")], 2);
        }
        foreach (var bib in new[] { 941003, 941004, 941010, 941012 })
        {
            provider.SetBib(bib, 2, new(true, $"Acceptance BIB {bib}", "Synthetic author", "2026", "Book"),
                new(0, 0, 0, true, true));
        }
        // Purchase routing is the acceptance boundary. Keep its Pending hold
        // result stable if a scheduled worker runs during acceptance.
        // This declared pre-dispatch failure cannot place an external hold.
        provider.SetFailure(new(TestingPolarisOperation.PatronHolds, 2, "ACCEPTANCE41003"),
            new PolarisOperationalException("review_hold_deferred", "Hold dispatch is deferred in this purchase-routing fixture."));
        await File.WriteAllTextAsync("/tmp/asap-review-provider-ready", "pr410-v1", cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
