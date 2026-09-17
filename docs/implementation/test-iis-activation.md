# Test IIS Deployment Setup

This guide configures a clean Windows Server as the nonproduction/test IIS host
for ASAP. Activation remains `test_cd_activation: pending_runner_setup` until a
real deployment completes and its evidence is recorded.

The deployment workflow does not provision the server. Complete sections 1-8
before enabling or dispatching deployment.

## 1. Final Host Layout

Create and retain this layout:

```text
C:\ProgramData\clc-asap\
    Config\
        application.json
        deployment.json
        deployment-state.json
    DataProtection-Keys\
    Logs\
    Staging\
    Backups\

D:\Sites\
    ASAP\
```

Create every directory before the first deployment. Leave `D:\Sites\ASAP`
empty and keep its IIS app pool stopped until the host configuration is ready.
The deployment script creates `deployment-state.json` only after a successful
deployment; do not create or edit it manually.

## 2. Provision Identities And Prerequisites

Use two dedicated, environment-specific domain identities:

- **ASAP runtime identity:** the `ASAP` IIS app-pool identity. It runs the web
  application and connects to SQL with Windows Integrated Security.
- **ASAP deployment identity:** the GitHub Actions runner service identity. It
  controls the app pool, stages and replaces files, and performs deployment SQL
  operations.

Use distinct runtime and deployment identities on this host; production must
use its own separate identities. Do not grant deployment, schema, or backup
rights to the runtime identity.

Install and configure:

- IIS, its management tools, and the `WebAdministration` PowerShell module;
- the .NET 10 ASP.NET Core Hosting Bundle;
- PowerShell 7 (`pwsh`);
- `SqlPackage.exe` from the SQL Server DAC tooling;
- `sqlcmd.exe`, either on `PATH` or at a path recorded in `deployment.json`;
- a GitHub Actions self-hosted runner;
- DNS and a trusted TLS certificate for the test hostname;
- network access from the host to GitHub, SQL Server, Entra ID, and required
  application providers.

## 3. Apply Filesystem And Certificate Permissions

Use ACLs rather than inherited broad access. At minimum:

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

Restrict configuration-file modification to administrators. The deployment
identity also needs permission to manage the `ASAP` IIS site/app pool and to
read the deployment ZIP downloaded into the runner workspace.

Install the Data Protection key-encryption certificate with its private key in
the runtime identity's Current User `My` store or the Local Machine `My` store.
Grant the runtime identity access to the private key. Record the certificate
thumbprint for `application.json` and retain recoverable backups of both the
certificate/private key and `DataProtection-Keys`.

## 4. Prepare SQL And Entra ID

Before deployment:

1. Create the test ASAP and Hangfire databases. They may initially be the same
   database, but keep separate connection strings.
2. Grant the runtime identity only the required application DML/EXECUTE rights
   and minimum runtime rights over the existing Hangfire objects. Do not grant
   `db_owner`, schema deployment, or backup rights.
3. Grant the deployment identity the rights required to publish the ASAP
   DACPAC and install or upgrade the packaged Hangfire schema.
4. Verify both identities can connect to the intended test databases with
   Windows Integrated Security. Verify the runtime identity cannot perform
   deployment/schema operations or access another environment's databases.
5. Register the nonproduction Entra application. Configure the test callback
   URI `https://<test-host>/signin-oidc`, create the client secret, identify
   allowed tenant IDs, and select the initial super-admin tenant ID, object ID,
   UPN, display name, and notification email.

The test deployment script does not create SQL databases or take SQL backups.

## 5. Create The Application Configuration

Create the ACL-restricted runtime configuration at:

```text
C:\ProgramData\clc-asap\Config\application.json
```

Start from
[`Config.example.json`](../dotnet-port/examples/Config.example.json) and replace
every placeholder. Configure:

- a nonproduction environment name, `IsNonProduction: true`, and a visible
  nonproduction banner;
- `AsapDatabase` and `HangfireDatabase` Integrated Security connection strings
  used by the IIS runtime identity;
- the Entra client ID, secret, allowed tenants, and initial super-admin;
- `BusinessTimeZone`;
- `DataProtectionKeysPath` as
  `C:\ProgramData\clc-asap\DataProtection-Keys`;
- the Data Protection certificate thumbprint;
- `LogPath` as `C:\ProgramData\clc-asap\Logs`;
- nonproduction recipient-domain restrictions;
- all required Hangfire schedules and processing-limit keys from the template.

This file contains secrets. Never commit it or place it under `D:\Sites\ASAP`.
The published `appsettings.json` already points `Asap:ConfigFile` to this path;
do not add an `Asap__ConfigFile` IIS environment variable.

## 6. Create The Deployment Configuration

Create this ACL-restricted file:

```text
C:\ProgramData\clc-asap\Config\deployment.json
```

Use this shape, replacing the environment-specific hostname, SQL names, and
tool locations:

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

The deployment SQL connection strings run as the deployment/runner identity and
must use Windows Integrated Security. Directory and external-config paths must
be absolute. Tool paths may be absolute or executable names available on
`PATH`. `DeploymentPath`, `StagingRoot`, `BackupRoot`,
`ExternalApplicationConfigPath`, the Data Protection directory, and both SQL
tools must be available before deployment.

The application and deployment config pointers intentionally repeat the same
`application.json` path. Preflight compares their normalized absolute paths
case-insensitively against the exact staged web payload.

## 7. Configure IIS

1. Create application pool `ASAP` with **No Managed Code**, Integrated pipeline
   mode, `AlwaysRunning`, and the ASAP runtime identity.
2. Create site `ASAP` with physical path `D:\Sites\ASAP` and assign app pool
   `ASAP`.
3. Enable site/application preload so Hangfire work is not dependent on first
   user traffic.
4. Add the HTTPS binding for the test hostname and trusted TLS certificate.
5. Confirm the deployment identity can query the site, read the app-pool state,
   and stop/start the app pool through `WebAdministration`.
6. Leave the app pool stopped until sections 8-9 are complete.

The deployment script verifies that the configured site exists, its physical
path matches `DeploymentPath`, and it uses the configured app pool.

## 8. Configure The GitHub Runner

1. Register a dedicated repository self-hosted runner on this IIS host.
2. Install it as a Windows service using the ASAP deployment identity.
3. Assign labels `self-hosted`, `windows`, `x64`, and `asap-test-iis`.
4. Restrict the runner to the deployment workflow; do not use it for PR or
   ordinary hosted build jobs.
5. Create or verify the repository environment `asap-test-iis` and apply its
   required reviewers and deployment branch/tag policy.
6. Confirm the service can reach GitHub and reports online.
7. In repository settings, set
   `ASAP_TEST_DEPLOYMENT_ENABLED=true` only after all preceding checks pass.

The deployment job downloads the exact artifact produced by the hosted build;
it does not check out or build the repository on the IIS host.

## 9. Run The First Deployment

Use an exact reviewed test tag, such as `v1.0.0-test.1`. Dispatch `.NET baseline`
from GitHub Actions, or use:

```text
gh workflow run dotnet.yml --repo clcdpc/asap-pocketbase --ref v1.0.0-test.1
```

The Actions UI exposes manual dispatch only after the workflow exists on the
default branch. The CLI/API command can target the exact branch or tag after
the workflow has run once.

Watch both jobs:

1. The hosted job builds, tests, publishes, packages, and validates the exact
   commit. Its deployment ZIP contains the web publish, DACPAC, deployment
   script, Hangfire SQL asset, and manifest; it does not contain either host
   configuration file, keys, logs, staging data, or backups.
2. The self-hosted job downloads the tested ZIP, verifies its SHA-256 and
   manifest identity, stages the exact payload, validates host configuration,
   verifies the two application-config pointers, and checks IIS/SQL state.
3. The script stops the app pool before database or live-file mutation,
   publishes a changed DACPAC, installs Hangfire schema version 9 when needed,
   retains the previous web payload under `Backups`, replaces the site files,
   starts the pool, and waits for readiness.

## 10. Validate Activation

After the workflow succeeds, verify:

- `https://<test-host>/health/live` returns HTTP 200;
- `https://<test-host>/health/ready` returns HTTP 200 with
  `{"status":"healthy"}`;
- the `ASAP` site and app pool are started and the site resolves to
  `D:\Sites\ASAP`;
- `C:\ProgramData\clc-asap\Logs` contains current application logs without
  secrets;
- Data Protection keys exist and the runtime identity can use the configured
  certificate;
- an authorized initial super-admin can complete Entra sign-in;
- `deployment-state.json` records the expected label, exact commit, ZIP hash,
  DACPAC hash, and deployment time;
- the recorded first-deployment evidence supports changing
  `test_cd_activation` from `pending_runner_setup` to `active`.

Re-running the exact recorded artifact is idempotent when readiness is healthy.
Backups are not automatically pruned. A later successful deployment replaces
`deployment-state.json`; failures do not record success.

## 11. Failure Behavior

- Package, configuration, path, IIS, SQL preflight, or pointer-validation
  failures occur before the app pool is stopped and leave the live site
  untouched.
- A DACPAC, Hangfire, backup, or file-replacement failure after the pool stops
  may leave the pool stopped. Inspect the actual SQL and filesystem state before
  restarting anything.
- A readiness failure occurs after the pool has started, retains the new files
  for repair-forward, and does not write successful deployment state.
- The test deployment path performs no automatic SQL backup, database rollback,
  file rollback, or production deployment behavior.

Preserve the downloaded artifact, workflow logs, staging diagnostics, previous
web backup, and actual SQL state while investigating. Do not manually edit
`deployment-state.json` to claim success.

Never commit runner tokens, PATs, connection secrets, Entra secrets,
certificates/private keys, Data Protection keys, or either host-local JSON file.
