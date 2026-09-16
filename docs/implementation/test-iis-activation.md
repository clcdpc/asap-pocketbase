# Test IIS Deployment Activation

This is a later one-time operational task for the reduced Slice 8 repository
implementation. It is intentionally separate from Slice 8 acceptance.

Current state: `test_cd_activation: pending_runner_setup`. No runner has been
installed or registered, no host configuration has been created by this
repository task, and no live IIS deployment is claimed.

## Repository Contract

The `.NET baseline` workflow keeps Release build, real-SQL tests, frontend tests,
publish checks, and test-package validation on `ubuntu-latest`. A matching test
tag such as `v1.0.0-test.1` runs those hosted gates for the exact tagged commit
and creates:

```text
manifest.json
web/                         published Asap.Web files and Asap.Database.dacpac
hangfire/1.8.25/install.sql  exact Hangfire schema-9 operator asset
deployment/Deploy-AsapTest.ps1
```

The ZIP and its adjacent `.sha256` file are uploaded as one artifact named for
the exact commit. The deployment job downloads that artifact and does not check
out the repository. It runs only when the event is a matching tag push or
`workflow_dispatch` and the repository variable
`ASAP_TEST_DEPLOYMENT_ENABLED` is exactly `true`. Empty, unset, or any other
value leaves the self-hosted job skipped.

`workflow_dispatch` uses the selected branch or tag as its ref, and the hosted
job checks out `github.sha`, so the build, tests, package and deployment
identity remain tied to one commit. GitHub shows the UI "Run workflow" button
only when the workflow file exists on the default branch. Until that file is
available on `main`, use the CLI/API after the workflow has run once:

```text
gh workflow run dotnet.yml --repo clcdpc/asap-pocketbase --ref v1.0.0-test.1
```

Do not dispatch a deployment until this activation checklist is complete.
See the [GitHub workflow_dispatch documentation](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#workflow_dispatch)
for the default-branch/UI and branch-or-tag dispatch behavior.

## Host-Local Configuration

Create this file on the IIS test host only:

```text
C:\ProgramData\ASAP\test-deployment.json
```

The deployment script requires this shape. Values below are placeholders, not
usable credentials or infrastructure values:

```json
{
  "IisSiteName": "ASAP-Test",
  "IisAppPoolName": "ASAP-Test",
  "DeploymentPath": "D:\\Sites\\ASAP-Test",
  "StagingRoot": "D:\\ASAP\\staging",
  "BackupRoot": "D:\\ASAP\\backups",
  "ExternalApplicationConfigPath": "C:\\ProgramData\\ASAP\\test-app.json",
  "ReadinessUrl": "https://test.example.invalid/health/ready",
  "AsapDatabaseConnectionString": "Server=TEST-SQL;Database=AsapTest;Integrated Security=True;Encrypt=True;TrustServerCertificate=False",
  "HangfireDatabaseConnectionString": "Server=TEST-SQL;Database=AsapTest;Integrated Security=True;Encrypt=True;TrustServerCertificate=False",
  "SqlPackagePath": "C:\\Program Files\\Microsoft SQL Server\\DAC\\170\\SqlPackage.exe",
  "SqlCmdPath": "sqlcmd.exe"
}
```

`Asap:ConfigFile` is the application configuration pointer. On IIS, set the
corresponding `Asap__ConfigFile` environment setting to the external
`ExternalApplicationConfigPath`; that application file must in turn point to
an existing Data Protection key directory outside `DeploymentPath`. Neither
that file nor its keys are copied into or overwritten by the deployment ZIP.

Successful state is written beside the deployment config at:

```text
C:\ProgramData\ASAP\test-deployment-state.json
```

The state records schema version, version/label, exact commit, deployment ZIP
SHA-256, DACPAC SHA-256 and UTC deployment time. Old state files are retained
until an operator removes them; old web backups under `BackupRoot` are never
pruned by the script.

## One-Time Activation Checklist

1. Create/register a dedicated repository self-hosted runner on the Windows
   IIS test host.
2. Install it as a Windows service with the dedicated service identity.
3. Assign labels `self-hosted`, `windows`, `x64`, and `asap-test-iis`.
4. Restrict the runner to the deployment workflow; it must not run PR or
   ordinary build jobs.
5. Create the host-local deployment config shown above and the external app
   config. Keep both outside the deployed web path.
6. Ensure the configured IIS site, app pool, deployment/staging/backup paths,
   HTTPS certificate and SQL database already exist.
7. Ensure PowerShell 7 (`pwsh`), `SqlPackage.exe` and `sqlcmd.exe` are available and the runner
   identity can use them with Windows Integrated Security.
8. Grant only the required IIS, filesystem and SQL deployment permissions.
9. Verify outbound GitHub connectivity and that the runner reports online.
10. Set repository variable `ASAP_TEST_DEPLOYMENT_ENABLED=true`.
11. Use an explicit CLI/API dispatch against one exact test tag, for example
    `gh workflow run dotnet.yml --repo clcdpc/asap-pocketbase --ref v1.0.0-test.1`.
12. Confirm the deployment job validates the ZIP SHA-256 and manifest before
    stopping the app pool, then verifies the site path and external config.
13. Confirm `/health/ready` returns HTTP 200 with JSON `status: healthy`.
14. Confirm `test-deployment-state.json` records the expected exact commit and
    ZIP/DACPAC hashes.
15. Record the first live test deployment evidence separately. This is the
    point at which `test_cd_activation` may become `active`.

The script checks Hangfire schema version 9 before mutation. If the `[HangFire]`
schema is missing or older, it stops the app pool, runs the exact packaged
`install.sql` asset with `sqlcmd`, and verifies version 9 before proceeding.
If it is newer, the deployment fails. Application runtime schema preparation
remains disabled (`PrepareSchemaIfNecessary = false`); this is the minimum
test-environment prerequisite and is not the deferred production classifier.

The script publishes the DACPAC only when its hash differs from the last
successful test state. It uses data-loss blocking, backs up the prior web
payload, replaces the complete staged web payload, starts the app pool, polls
the configured HTTPS readiness URL with normal TLS validation, and writes state
only after readiness succeeds. Preflight failure leaves the running site
untouched. Database or file failure leaves the app pool stopped as applicable;
readiness failure retains the new files for repair-forward. There is no
automatic SQL backup, schema rollback, old-artifact rollback, or production
deployment behavior in this test path.

Do not commit runner tokens, PATs, database passwords, certificates, private
keys, or a host-local configuration file.
