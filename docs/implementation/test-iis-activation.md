# Test IIS Deployment Setup

This guide prepares a clean Windows Server as the nonproduction/test IIS host
for ASAP. Activation remains `test_cd_activation: pending_runner_setup` until a
real deployment completes and its evidence is recorded.

The responsibility boundary is deliberate:

- ASAP bootstrap owns ASAP directories and initial host configuration.
- The operator owns Windows, IIS, identities, certificates, permissions, SQL,
  Entra ID, and the GitHub Actions runner.
- Deployment operates against the already-provisioned host and verifies the
  running application.

## Step 1 - Copy The Provisioning Scripts

Copy `Initialize-AsapTestHost.ps1` and
`New-AsapDataProtectionCertificate.ps1` from the reviewed repository to the
host. A repository clone and the repository directory structure are not
required on the IIS server.

## Step 2 - Initialize

From an elevated PowerShell 7 session in the directory containing the copied
script, run:

```powershell
pwsh -File .\Initialize-AsapTestHost.ps1 -Initialize
```

The script creates the ASAP-owned structure and safe configuration templates:

```text
C:\ProgramData\clc-asap\
    Config\
        application.json
        deployment.json
    DataProtection-Keys\
    Logs\
    Staging\
    Backups\
```

The generated `application.json` comes from the readable operational template
embedded in the bootstrap. The script substitutes the two paths derived from
`RootPath` before writing the file.

It does not create the IIS deployment directory, IIS resources, identities,
certificates, ACLs, SQL objects, Entra applications, or the runner, and it does
not invoke the certificate-provisioning helper. It never overwrites an
existing `application.json` or `deployment.json`.
`deployment-state.json` is absent initially; deployment writes it only after a
successful readiness check.

## Step 3 - Manually Configure Host Infrastructure

Complete all work in this section before validation.

### Install prerequisites and identities

Use two dedicated, environment-specific domain identities:

- **ASAP runtime identity:** the `ASAP` IIS app-pool identity. It runs the web
  application and connects to SQL with Windows Integrated Security.
- **ASAP deployment identity:** the GitHub Actions runner service identity. It
  controls the app pool, stages and replaces files, and performs deployment SQL
  operations.

Do not grant deployment, schema, or backup rights to the runtime identity.
Production must use separate identities.

Install and configure:

- IIS, its management tools, and the `WebAdministration` PowerShell module;
- the .NET 10 ASP.NET Core Hosting Bundle;
- PowerShell 7 (`pwsh`);
- `SqlPackage.exe` from the SQL Server DAC tooling;
- `sqlcmd.exe`, either on `PATH` or at the path in `deployment.json`;
- DNS and a trusted TLS certificate for the test hostname;
- a dedicated GitHub Actions self-hosted runner;
- network access to GitHub, SQL Server, Entra ID, and required providers.

Create and configure the `ASAP` app pool with its dedicated runtime identity
before provisioning the Data Protection certificate, so Windows can resolve
the account to its SID. From an elevated PowerShell 7 session, run:

```powershell
pwsh -File .\New-AsapDataProtectionCertificate.ps1 `
    -RuntimeIdentity 'DOMAIN\asap-test' `
    -PfxBackupPath 'X:\SecureBackup\asap-test-data-protection.pfx'
```

The backup directory must already exist and be secured by the operator. The
helper prompts twice for the PFX password as a secure string; it does not
accept, print, or persist that password. It creates an exportable,
environment-specific RSA certificate in `LocalMachine\My`, grants the resolved
runtime SID read access to its private key, verifies the PFX contains that
private key, and atomically replaces only the bootstrap certificate-thumbprint
placeholder in `application.json`.

If a real thumbprint is already configured, the helper retains that
certificate, requires it to resolve with a compatible private key, ensures the
requested runtime SID has read access, and exports a recovery PFX to the new
operator-selected path. It never silently rebinds the configuration or
overwrites an existing PFX. Securely retain the PFX and its separately managed
password before continuing.

### Apply filesystem and certificate permissions

Create the operator-owned deployment directory `D:\Sites\ASAP` while creating
the IIS site. Use narrow ACLs rather than broad inherited access.

| Resource | Runtime identity | Deployment identity |
| --- | --- | --- |
| `D:\Sites\ASAP` | Read and execute | Modify |
| `Config\application.json` | Read | Read |
| `Config\deployment.json` | None | Read |
| `Config\deployment-state.json` | None | Create and modify |
| `DataProtection-Keys` | Read, create, and modify | Read/traverse for preflight |
| `Logs` | Create and modify | As required for diagnostics |
| `Staging` | None | Create, modify, and delete |
| `Backups` | None | Create and modify |

Restrict configuration-file modification to administrators. Grant the
deployment identity permission to query the `ASAP` site and app pool and to
stop/start the pool during deployment.

Install the trusted TLS certificate and configure its IIS HTTPS binding. These
are operator tasks; the bootstrap only validates their presence.

### Prepare SQL and Entra ID

1. Create the test ASAP and Hangfire databases. They may initially be the same
   database, but retain separate connection strings.
2. Grant the runtime identity only required application DML/EXECUTE rights and
   minimum runtime rights over existing Hangfire objects. Do not grant
   `db_owner`, schema deployment, or backup rights.
3. Grant the deployment identity rights to publish the ASAP DACPAC and install
   or upgrade the packaged Hangfire schema.
4. Verify both identities can connect to the intended databases with Windows
   Integrated Security, and that the runtime identity cannot perform schema or
   deployment operations.
5. Register the nonproduction Entra application with a Web platform. Configure
   `https://<test-host>/signin-oidc` as its redirect URI, enable ID tokens under
   the web authentication implicit/hybrid settings, record the client ID and
   allowed tenant IDs, and identify the initial super-admin. ASAP uses the
   signed ID token only and issues its own staff cookie. No Entra client secret
   or client certificate is required by ASAP.

The deployment script does not create SQL databases or take SQL backups.

### Complete application.json

Edit the template at:

```text
C:\ProgramData\clc-asap\Config\application.json
```

Replace every remaining `REPLACE-*` placeholder. The certificate helper has
already replaced the Data Protection certificate-thumbprint placeholder. Keep:

- a nonproduction environment with `IsNonProduction: true` and a visible
  banner;
- separate runtime `AsapDatabase` and `HangfireDatabase` Integrated Security
  connection strings;
- Entra client ID, allowed tenants, and initial super-admin values;
- `DataProtectionKeysPath` as
  `C:\ProgramData\clc-asap\DataProtection-Keys`;
- the Data Protection certificate thumbprint;
- `LogPath` as `C:\ProgramData\clc-asap\Logs`;
- nonproduction recipient-domain restrictions;
- the `PatronLoginRateLimit` section with the template defaults unless a
  reviewed environment-specific policy requires different values;
- all Hangfire schedules and processing-limit keys in the template.

This host-owned file may contain sensitive operational configuration. Never
commit it or place it under `D:\Sites\ASAP`.
Published `appsettings.json` already points `Asap:ConfigFile` to this exact
path. Do not add an `Asap__ConfigFile` IIS environment variable.

The Data Protection certificate above remains required and is unrelated to
Entra authentication credentials. ASAP's Entra login requires no client secret
or client certificate. The Data Protection certificate encrypts the persistent
ASP.NET Core key ring used for ASAP cookies, antiforgery, and protected values;
removing the Entra credential requirement does not remove that certificate or
its backup and recovery obligations.

The helper does not create Data Protection key files. On first successful
startup, ASP.NET Core generates the initial key ring under
`C:\ProgramData\clc-asap\DataProtection-Keys`, encrypts it with the configured
certificate, and owns later rotation. The recovery PFX/private key and the
generated `DataProtection-Keys` directory are both required disaster-recovery
material. Back them up securely and separately from ordinary deployment files,
along with the separately retained PFX password.

### Complete deployment.json

Edit the template at:

```text
C:\ProgramData\clc-asap\Config\deployment.json
```

Its final shape is:

```json
{
  "IisSiteName": "ASAP",
  "IisAppPoolName": "ASAP",
  "DeploymentPath": "D:\\Sites\\ASAP",
  "StagingRoot": "C:\\ProgramData\\clc-asap\\Staging",
  "BackupRoot": "C:\\ProgramData\\clc-asap\\Backups",
  "ExternalApplicationConfigPath": "C:\\ProgramData\\clc-asap\\Config\\application.json",
  "ReadinessUrl": "https://test.example.invalid/health/ready",
  "AsapDatabaseConnectionString": "Server=TEST-SQL;Database=AsapTest;Integrated Security=True;Encrypt=True;TrustServerCertificate=False",
  "HangfireDatabaseConnectionString": "Server=TEST-SQL;Database=AsapTest;Integrated Security=True;Encrypt=True;TrustServerCertificate=False",
  "SqlPackagePath": "C:\\Program Files\\Microsoft SQL Server\\DAC\\170\\SqlPackage.exe",
  "SqlCmdPath": "sqlcmd.exe"
}
```

The deployment SQL connections run as the deployment identity and use Windows
Integrated Security. The application and deployment pointers intentionally
repeat the same `application.json` path. Deployment preflight compares the
normalized value with the exact staged web payload.

### Configure IIS

1. Create app pool `ASAP` with **No Managed Code**, Integrated pipeline mode,
   `AlwaysRunning`, and the ASAP runtime identity.
2. Create site `ASAP` at `D:\Sites\ASAP` and assign app pool `ASAP`.
3. Enable site/application preload.
4. Add the HTTPS binding for the test hostname and trusted TLS certificate.
5. Confirm the deployment identity can inspect the site/app pool and stop/start
   the app pool through `WebAdministration`.
6. Leave the app pool stopped until validation succeeds and deployment is
   enabled.

### Configure the GitHub runner

1. Register a dedicated repository self-hosted runner on the IIS host.
2. Install it as a Windows service using the deployment identity.
3. Assign labels `self-hosted`, `windows`, `x64`, and `asap-test-iis`.
4. Restrict it to deployment; do not use it for PR or hosted build jobs.
5. Configure the `asap-test-iis` repository environment, reviewers, and tag
   policy.
6. Confirm the runner can reach GitHub and reports online.

The deployment job downloads the exact hosted-build artifact. It does not
check out or build the repository on the IIS host.

## Step 4 - Validate

Run the bootstrap with no flags:

```powershell
pwsh -File .\Initialize-AsapTestHost.ps1
```

Validation checks the ProgramData layout and JSON, unchanged placeholders,
canonical path and pointer contracts, the Data Protection certificate in
`LocalMachine\My`, .NET 10 Hosting Bundle, PowerShell and SQL tools, IIS
site/app pool/path, and HTTPS binding. Application domain validation remains
owned by `ExternalConfigurationValidator` at startup. Validation does not
repair or create anything and exits nonzero for blocking failures.
Running with no flags is always safe and read-only. A failed validation never
triggers initialization or any other mutating setup.

The provisioning helper verifies the certificate, machine-key file, explicit
SID ACL, and exported PFX as the elevated operator. It does not impersonate the
IIS runtime identity. The application's startup Protect/Unprotect probe remains
the authoritative verification that the runtime identity can perform Data
Protection cryptographic operations.

Validation checks only that `ReadinessUrl` is configured correctly. It does
not request `/health/ready`; no application exists to answer it before the
first deployment.

## Step 5 - Enable Deployment

Only after validation succeeds, set the repository variable:

```text
ASAP_TEST_DEPLOYMENT_ENABLED=true
```

Keep the gate unset or false while any prerequisite is incomplete.

## Step 6 - Deploy And Verify

Use an exact reviewed test tag such as `v1.0.0-test.1`. Dispatch `.NET baseline`
from GitHub Actions, or run:

```text
gh workflow run dotnet.yml --repo clcdpc/asap-pocketbase --ref v1.0.0-test.1
```

The hosted job builds, tests, publishes, packages, and hashes the exact commit.
The ZIP excludes host-owned `application.json`, `deployment.json`, keys, logs,
staging data, and backups. The self-hosted job verifies the ZIP and manifest,
validates the two application-config pointers and existing IIS/SQL state, then
stops the app pool before database or live-file mutation. It publishes required
schema changes, backs up the previous web payload, replaces files, starts the
pool, and polls `/health/ready` with normal TLS validation.

### Verification Evidence

After the workflow succeeds, verify:

- `https://<test-host>/health/live` returns HTTP 200;
- `https://<test-host>/health/ready` returns HTTP 200 with
  `{"status":"healthy"}`;
- the `ASAP` site and app pool are started and resolve to `D:\Sites\ASAP`;
- logs contain current application activity without secrets;
- Data Protection keys exist and the runtime identity can use the certificate;
- the initial super-admin can complete Entra sign-in;
- `deployment-state.json` records the expected label, exact commit, ZIP hash,
  DACPAC hash, and deployment time.

That evidence supports changing `test_cd_activation` from
`pending_runner_setup` to `active`. Re-running the exact recorded artifact is
idempotent when readiness is healthy. Backups are not automatically pruned.

## Failure Behavior

- Package, configuration, path, IIS, SQL, or pointer preflight failures occur
  before the app pool is stopped and leave the live site untouched.
- A DACPAC, Hangfire, backup, or file-replacement failure after the pool stops
  may leave it stopped. Inspect actual SQL and filesystem state before restart.
- A readiness failure occurs after pool start, retains new files for
  repair-forward, and does not write successful deployment state.
- Deployment performs no automatic SQL backup, database rollback, file
  rollback, or production deployment behavior.

Never commit runner tokens, PATs, connection secrets, certificates,
private keys, Data Protection keys, or either host-local JSON file.
