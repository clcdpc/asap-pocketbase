# Test IIS Deployment Activation

Current state: `test_cd_activation: pending_runner_setup`.

The repository-side test deployment workflow is implemented, but live test deployment remains an explicit operational activation step. Keep `ASAP_TEST_DEPLOYMENT_ENABLED` unset or false until this checklist is complete. Do not mark activation active until a separately authorized task records a real successful test-IIS deployment.

## Repository Contract

The `.NET baseline` workflow keeps Release build, real-SQL tests, frontend/browser tests, publish checks, and deployment-package validation on GitHub-hosted `ubuntu-latest`. Only the final deployment job targets the dedicated Windows runner labels `self-hosted`, `windows`, `x64`, and `asap-test-iis`.

A matching test tag such as `v1.0.0-test.1` or an explicit `workflow_dispatch` builds and tests one exact commit, packages the Web publish/DACPAC/deployment script/Hangfire asset, verifies the package digest, then deploys only when repository variable `ASAP_TEST_DEPLOYMENT_ENABLED` is exactly `true`.

The deployment job always reads the canonical host configuration from:

```text
C:\ProgramData\clc-asap\config\test-deployment.json
```

## Automated Host Bootstrap

Run the bootstrap once from an **elevated PowerShell 7** session on the IIS test host:

```powershell
.\scripts\deployment\Initialize-AsapTestHost.ps1 `
    -SqlServer 'TEST-SQL' `
    -ReadinessUrl 'https://asap-test.example.org/health/ready'
```

`DatabaseName` defaults to `AsapTest`. The bootstrap uses the single canonical application root:

```text
C:\ProgramData\clc-asap
```

and creates:

```text
C:\ProgramData\clc-asap\web
C:\ProgramData\clc-asap\staging
C:\ProgramData\clc-asap\backups
C:\ProgramData\clc-asap\config
C:\ProgramData\clc-asap\keys
C:\ProgramData\clc-asap\logs
```

The deployment configuration is:

```text
C:\ProgramData\clc-asap\config\test-deployment.json
```

The external application configuration is:

```text
C:\ProgramData\clc-asap\config\test-app.json
```

Successful deployment state is written beside it as:

```text
C:\ProgramData\clc-asap\config\test-deployment-state.json
```

The bootstrap:

- discovers the installed `actions.runner.*` Windows service;
- uses that service's Windows identity for both the runner and the `ASAP-Test` IIS app pool;
- accepts `-RunnerServiceName` when more than one runner service exists;
- accepts `-ServiceAccount` only as an explicit override;
- creates the application folders and restricts their ACLs to SYSTEM, local Administrators, and the service identity;
- adds the service identity to `IIS_IUSRS`;
- by default adds the service identity to local Administrators so the deployment job can manage the IIS app pool on this dedicated test host; use `-SkipLocalAdministrator` only when equivalent narrower IIS lifecycle rights have been provisioned separately;
- locates `pwsh`, `SqlPackage`, and `sqlcmd` and reports missing tools;
- creates or reuses a LocalMachine Data Protection certificate, grants the service identity read access to its private key, and verifies that access;
- creates `test-app.json` when absent and preserves it on later runs unless `-ForceApplicationConfig` is supplied;
- validates preserved application configuration against the startup-relevant ASP.NET configuration contract, including Entra, paths, recipient domains, Hangfire shape/limits, rate limits, and SQL target/security settings;
- creates/updates `test-deployment.json` in normal mode and validates all of its effective values in `-ValidateOnly` mode;
- creates/configures the `ASAP-Test` app pool and sets its `Asap__ConfigFile` environment variable;
- validates an existing IIS site and refuses to silently repoint one with a different path or app pool;
- can create a missing HTTPS IIS site when `-HttpsCertificateThumbprint` is supplied;
- prints a `PASS` / `NEEDS ATTENTION` summary.

If the IIS pool must be changed to the detected ordinary AD service account, the bootstrap prompts once for that account's password unless `-ServiceCredential` is supplied. A gMSA name ending in `$` does not require that prompt.

Use `-ValidateOnly` to audit the host without creating directories, changing ACLs/groups/IIS, creating certificates, or writing configuration files. Validation mode inspects the existing root ACL, Data Protection private-key access, application configuration, and deployment configuration; it does not report those areas as PASS merely because files/certificates exist.

## Application Configuration

The bootstrap can populate the Entra values at creation time:

```powershell
$entraSecret = Read-Host 'Entra client secret' -AsSecureString

.\scripts\deployment\Initialize-AsapTestHost.ps1 `
    -SqlServer 'TEST-SQL' `
    -ReadinessUrl 'https://asap-test.example.org/health/ready' `
    -EntraClientId '<client-guid>' `
    -EntraTenantId '<tenant-guid>' `
    -EntraObjectId '<initial-admin-object-guid>' `
    -AdminEmail 'admin@example.org' `
    -EntraClientSecret $entraSecret
```

If those values are omitted, `test-app.json` is created with `REPLACE-LOCALLY` placeholders and the bootstrap reports `NEEDS ATTENTION`. Complete those values before enabling deployment. Do not place the client secret directly on the command line.

The generated SQL connection strings use Windows Integrated Security and encryption. `TrustServerCertificate` defaults to false; use `-TrustServerCertificate` only if the test SQL endpoint intentionally requires it.

## SQL Authorization Boundary

The host bootstrap intentionally does **not** grant SQL Server privileges. Grant the detected service identity only the test-environment rights needed for:

1. the deployment path to publish the ASAP DACPAC with `SqlPackage`;
2. the deployment path to create/upgrade Hangfire schema 9 when necessary;
3. the running application to access the ASAP/Hangfire database.

Confirm those permissions by running under the service identity or by performing the first controlled deployment. Do not embed a SQL login/password in the host configuration.

## IIS / TLS

The IIS site defaults to `ASAP-Test`, with physical path:

```text
C:\ProgramData\clc-asap\web
```

The app pool also defaults to `ASAP-Test` and runs as the same Windows identity as the GitHub Actions runner service.

If the site already exists, the bootstrap verifies its physical path and pool and will not repoint it automatically. If it does not exist, either create the HTTPS site/binding normally or pass the LocalMachine certificate thumbprint:

```powershell
.\scripts\deployment\Initialize-AsapTestHost.ps1 `
    -SqlServer 'TEST-SQL' `
    -ReadinessUrl 'https://asap-test.example.org/health/ready' `
    -HttpsCertificateThumbprint '<thumbprint>'
```

The readiness URL must be HTTPS and end in `/health/ready`.

## One-Time Activation Checklist

1. Confirm the repository runner is online and has labels `self-hosted`, `windows`, `x64`, and `asap-test-iis`.
2. Run `Initialize-AsapTestHost.ps1` and resolve every `NEEDS ATTENTION` item that blocks startup/deployment.
3. Complete real Entra configuration in `test-app.json`.
4. Confirm the HTTPS IIS binding/certificate and DNS are correct.
5. Grant/verify SQL permissions for the shared test service identity.
6. Re-run the bootstrap with `-ValidateOnly` as a final host check and require zero blocking `NEEDS ATTENTION` results.
7. Set repository variable `ASAP_TEST_DEPLOYMENT_ENABLED=true`.
8. Dispatch one exact test tag, for example `gh workflow run dotnet.yml --repo clcdpc/asap-pocketbase --ref v1.0.0-test.1`.
9. Confirm the deployment job validates the package before mutation, `/health/ready` returns HTTP 200 with JSON `status: healthy`, and `test-deployment-state.json` records the expected commit and ZIP/DACPAC hashes.
10. Record the first live deployment evidence. Only then should `test_cd_activation` be considered active.

The test deployment path does not perform automatic SQL backup/rollback or production deployment behavior. It backs up the previous web payload, applies changed DACPAC/Hangfire schema work as required, replaces the staged web payload, starts the app pool, and writes deployment state only after readiness succeeds.

Do not commit runner tokens, PATs, database passwords, Entra secrets, certificates/private keys, Data Protection keys, or host-local configuration files.
