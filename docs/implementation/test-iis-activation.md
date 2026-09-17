# Test IIS Deployment Activation

This is a later one-time operational task for the reduced Slice 8 repository
implementation. It is intentionally separate from Slice 8 acceptance.

Current state: `test_cd_activation: pending_runner_setup`. No runner has been
installed or registered by this repository task, no host-local configuration is
claimed as live evidence, and no IIS deployment is claimed.

## Repository contract

The hosted workflow builds, tests, publishes and packages one exact commit on
`ubuntu-latest`. A matching test tag such as `v1.0.0-test.1` or an explicit
`workflow_dispatch` produces an artifact containing the published Web files,
DACPAC, deployment script and exact Hangfire schema-9 asset. The isolated IIS
job consumes that artifact only when repository variable
`ASAP_TEST_DEPLOYMENT_ENABLED` is exactly `true`; it does not check out or
build source on the IIS host.

The deploy script's default host config is:

```text
C:\ProgramData\clc-asap\config\test-deployment.json
```

The deployment state is adjacent at
`C:\ProgramData\clc-asap\config\test-deployment-state.json`. The deployed
web tree is replaced from staging; external configuration, keys, logs and
backups remain outside that tree.

## Bootstrap invocation

Run PowerShell 7 elevated on the dedicated Windows host. The normal invocation
only needs the SQL Server and the real HTTPS readiness URL when defaults are
otherwise suitable:

```powershell
pwsh -NoProfile -File .\Initialize-AsapTestHost.ps1 `
  -SqlServer 'TEST-SQL' `
  -ReadinessUrl 'https://test.example.org/health/ready'
```

The script discovers services named `actions.runner.*`. Exactly one matching
service is selected automatically. If more than one exists, pass
`-RunnerServiceName` with the exact service name. The selected runner identity
is the default identity for the filesystem ACL, IIS app pool and certificate
private-key permission. Built-in identities such as LocalSystem, LocalService
and NetworkService are rejected. `-ServiceAccount` is an explicit override for
an operator who has already arranged an equivalent effective runner identity;
it must not be used to hide an identity mismatch.

For a missing IIS site, pass the thumbprint of an existing appropriate
LocalMachine HTTPS certificate:

```powershell
  -HttpsCertificateThumbprint '0123456789ABCDEF0123456789ABCDEF01234567'
```

An ordinary domain service account may need `-ServiceAccountPassword` supplied
as a `SecureString`; the bootstrap prompts securely when an IIS pool must be
created or its account changed. A gMSA ending in `$` is supported without a
password parameter. The bootstrap never changes runner registration.

## Canonical host layout

The root is fixed and is not configurable:

```text
C:\ProgramData\clc-asap\web
C:\ProgramData\clc-asap\staging
C:\ProgramData\clc-asap\backups
C:\ProgramData\clc-asap\config
C:\ProgramData\clc-asap\keys
C:\ProgramData\clc-asap\logs
```

The host deployment config is
`C:\ProgramData\clc-asap\config\test-deployment.json` and the ASP.NET
external config is
`C:\ProgramData\clc-asap\config\test-app.json`. The bootstrap creates or
updates the deployment config, creates the application config when absent,
and preserves an existing operator-edited application config unless `-Force`
is supplied. A forced regeneration may still contain Entra placeholders and
therefore is not activation-ready until completed.

The generated application and deployment connection strings use the same
requested server and database (`AsapTest` by default), Windows Integrated
Security, `Encrypt=True`, and `TrustServerCertificate=False` unless the
operator explicitly passes `-TrustServerCertificate`. SQL permissions are an
operator boundary: the bootstrap does not create databases, logins, users,
roles or grants. Authorize the selected runtime/deployment identities in SQL
separately, with runtime rights kept distinct from DACPAC and Hangfire schema
rights.

## Runtime and IIS prerequisites

Before activation, confirm the bootstrap reports PASS for PowerShell 7,
`SqlPackage`, `sqlcmd`, the .NET 10 `Microsoft.AspNetCore.App` runtime, and
ASP.NET Core Module V2/IIS Hosting Bundle support. The script reports missing
prerequisites but does not download or install software.

The bootstrap puts the selected identity in `IIS_IUSRS` and, for the reduced
dedicated-host contract, local `Administrators`, unless it is already a
member. It configures the pool as No Managed Code, Integrated,
AlwaysRunning and SpecificUser, and sets the exact pool environment variable:

```text
Asap__ConfigFile=C:\ProgramData\clc-asap\config\test-app.json
```

An existing site is validated for its physical path, app pool, HTTPS binding
and certificate; it is never silently repointed. A missing site is
created only when `-HttpsCertificateThumbprint` identifies a LocalMachine
certificate with a private key. Confirm DNS and the real HTTPS certificate
binding with the operator before enabling deployment.

## Data Protection and Entra completion

The bootstrap creates or reuses a LocalMachine X.509 certificate named for the
test Data Protection ring, grants the selected identity read access to its
private key, and stores the certificate thumbprint in `test-app.json`. It
never exports a private key. The persistent key ring is
`C:\ProgramData\clc-asap\keys`; logs use
`C:\ProgramData\clc-asap\logs`.

The generated external config uses the repository's exact
`ExternalConfiguration` shape, schedule key set, queue key set and processing
limits. Supply real Entra client ID, secure client secret, tenant ID(s),
initial admin object ID/UPN/display name/notification email, and allowed test
recipient domains, or edit the preserved file manually. Placeholder or
invalid values produce NEEDS ATTENTION and are not ready for activation.

## Read-only validation

Run the final audit with the same effective arguments and `-ValidateOnly`:

```powershell
pwsh -NoProfile -File .\Initialize-AsapTestHost.ps1 `
  -SqlServer 'TEST-SQL' `
  -ReadinessUrl 'https://test.example.org/health/ready' `
  -ValidateOnly
```

If multiple runner services exist, include `-RunnerServiceName`; include the
HTTPS thumbprint when the existing binding is expected to use a specific
certificate. ValidateOnly checks the actual effective files, ACL inheritance
through the managed tree, local-group membership, IIS settings and certificate
private-key sign/verify access. It does not create directories or certificates,
rewrite JSON, change ACLs, alter local groups, modify IIS, or grant SQL rights.
Every missing or mismatched item is reported as PASS, NEEDS ATTENTION or INFO;
do not treat file existence alone as a pass.

## First controlled deployment

After the host audit and SQL authorization are complete:

1. Install/register the dedicated GitHub Actions runner as a Windows service
   under the selected service identity, assign `self-hosted`, `windows`, `x64`
   and `asap-test-iis`, and restrict it to the deployment workflow.
2. Confirm DNS, the real HTTPS binding/certificate, Entra values, key/private
   key access, SQL connectivity and the `test-app.json` readiness result.
3. Confirm `-ValidateOnly` is clean apart from intentional INFO boundaries.
4. Set `ASAP_TEST_DEPLOYMENT_ENABLED=true`.
5. Dispatch one exact test tag, for example:

   ```text
   gh workflow run dotnet.yml --repo clcdpc/asap-pocketbase --ref v1.0.0-test.1
   ```

6. Confirm the IIS job verifies the hosted ZIP digest and manifest before
   stopping the pool, then confirm HTTPS `/health/ready` returns HTTP 200 with
   JSON `status: healthy`.
7. Record the exact commit, ZIP/DACPAC hashes, retained web backup and
   `test-deployment-state.json` as live activation evidence.

The deployment script keeps Hangfire schema preparation operator-owned and
checks for schema version 9. Application runtime startup does not prepare the
Hangfire schema. There is no automatic SQL grant, SQL backup/rollback, or
production deployment in this test path.

Do not commit runner tokens, PATs, database credentials, certificates, private
keys or host-local JSON. Do not change `test_cd_activation` to `active` until
the first real controlled IIS deployment has succeeded and been recorded.
