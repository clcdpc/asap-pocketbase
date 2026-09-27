const assert = require('assert');
const fs = require('fs');
const path = require('path');
const childProcess = require('child_process');

const root = path.resolve(__dirname, '..');
const workflow = fs.readFileSync(path.join(root, '.github/workflows/dotnet.yml'), 'utf8').replace(/\r\n/g, '\n');
const deployment = fs.readFileSync(path.join(root, 'scripts/deployment/Deploy-AsapTest.ps1'), 'utf8');
const runtimeSchemaVersion = fs.readFileSync(
  path.join(root, 'src', 'Asap.Web', 'Infrastructure', 'Data', 'SchemaVersion.cs'),
  'utf8'
);
const postDeployment = fs.readFileSync(
  path.join(root, 'database', 'Asap.Database', 'Scripts', 'PostDeployment.sql'),
  'utf8'
);
const migrationContract = fs.readFileSync(
  path.join(root, 'src', 'Asap.Migration', 'MigrationContract.cs'),
  'utf8'
);
const migrationProject = fs.readFileSync(
  path.join(root, 'src', 'Asap.Migration', 'Asap.Migration.csproj'),
  'utf8'
);
const activation = fs.readFileSync(path.join(root, 'docs/implementation/test-iis-activation.md'), 'utf8');
const slice8 = fs.readFileSync(path.join(root, 'docs/implementation/slice-08.md'), 'utf8');

childProcess.execFileSync(
  'pwsh',
  ['-NoLogo', '-NoProfile', '-File', path.join(__dirname, 'test_deployment_configuration_preflight.ps1')],
  { stdio: 'inherit' }
);

assert.ok(workflow.includes("tags:\n      - 'v*.*.*-test.*'"), 'test deployment tags should use the documented convention');
assert.ok(workflow.includes('- codex/csharp-port'), 'integration-branch push trigger is required');
assert.ok(workflow.includes('- codex/slice-08-test-deployment'), 'candidate push trigger is required');
assert.ok(workflow.includes('  workflow_dispatch:\n'), 'manual dispatch should be declared');
assert.ok(
  workflow.includes(
    'concurrency:\n  group: ${{ github.workflow }}-${{ github.event.pull_request.head.sha || github.sha }}\n  cancel-in-progress: true\n\njobs:'
  ),
  'workflow concurrency should deduplicate PR, push, tag, and manual runs by source commit'
);
assert.ok(!workflow.includes('\n    concurrency:'), 'build-test-package must not retain job-level concurrency');
assert.ok(workflow.includes('Generate ephemeral SQL test credentials'), 'CI SQL credentials should be generated per run');
assert.ok(!workflow.includes('Asap_Slice0_SQL_2026'), 'CI must not retain the historical hard-coded SQL password');
assert.ok(workflow.includes('ref: ${{ github.sha }}'), 'the hosted job should check out the exact event SHA');
assert.ok(workflow.includes('--minimum-expected-tests 475'), 'the non-browser real-SQL partition must retain its test-count guard');
assert.ok(workflow.includes('run: npm test'), 'the frontend test gate must remain');
assert.ok(workflow.includes('dotnet publish src/Asap.Web/Asap.Web.csproj'), 'Web publish must remain a hosted check');
assert.ok(workflow.includes('dotnet publish src/Asap.Migration/Asap.Migration.csproj'), 'native migration publish check must remain');

const schemaVersions = {
  workflow: Number(workflow.match(/applicationSchemaVersion = (\d+)/)?.[1]),
  deployment: Number(deployment.match(/manifest\.applicationSchemaVersion -ne (\d+)/)?.[1]),
  runtime: Number(runtimeSchemaVersion.match(/ExpectedVersion = (\d+)/)?.[1]),
  postDeployment: Number(postDeployment.match(/VALUES \(1, (\d+), SYSUTCDATETIME\(\)\)/)?.[1]),
  migrationContract: Number(migrationContract.match(/ExpectedSchemaVersion = (\d+)/)?.[1]),
  migrationProject: Number(migrationProject.match(/ExpectedSchemaVersion" Value="(\d+)"/)?.[1])
};
assert.ok(
  Object.values(schemaVersions).every((version) => version === 6),
  `application schema version must be 6 in every package/runtime contract: ${JSON.stringify(schemaVersions)}`
);
assert.match(
  postDeployment,
  /Schema 6 is a pre-release reset boundary\. Recreate the application database from this DACPAC\./,
  'the DACPAC must reject in-place upgrades from pre-schema-6 databases'
);

const deploymentJobStart = workflow.indexOf('  deploy-test-iis:');
assert.ok(deploymentJobStart > 0, 'the deployment job should be present');
const hostedJobs = workflow.slice(0, deploymentJobStart);
const deploymentJob = workflow.slice(deploymentJobStart);
assert.ok(!hostedJobs.includes('self-hosted'), 'hosted build/test/package jobs must not use self-hosted labels');
assert.ok(!hostedJobs.includes('ASAP_TEST_DEPLOYMENT_ENABLED'), 'hosted build/test/package jobs must not depend on activation');
assert.ok(
  workflow.slice(deploymentJobStart).includes(
    "if: ${{ vars.ASAP_TEST_DEPLOYMENT_ENABLED == 'true' && (github.event_name == 'workflow_dispatch' || (github.event_name == 'push' && startsWith(github.ref, 'refs/tags/v'))) }}"
  ),
  'deployment must require the explicit activation variable and an eligible event'
);
assert.ok(
  workflow.slice(deploymentJobStart).includes('runs-on: [self-hosted, windows, x64, asap-test-iis]'),
  'deployment must be isolated to the dedicated test-IIS runner labels'
);
assert.ok(!workflow.slice(deploymentJobStart).includes('actions/checkout'), 'the IIS job must not check out the repository');
assert.ok(workflow.includes('actions/download-artifact@v4'), 'the IIS job must consume the hosted artifact');
assert.ok(workflow.includes('zip_sha256: ${{ steps.package.outputs.zip_sha256 }}'), 'the hosted package job must expose the final ZIP digest as a job output');
assert.ok(workflow.includes('id: package'), 'the ZIP packaging step must own the digest output');
assert.ok(workflow.includes('"zip_sha256=$zipHash" >> $env:GITHUB_OUTPUT'), 'the package step must emit the computed final ZIP digest');
assert.ok(
  deploymentJob.includes('EXPECTED_ZIP_SHA256: ${{ needs.build-test-package.outputs.zip_sha256 }}'),
  'the IIS job must receive the hosted package digest through its environment'
);
assert.ok(!deploymentJob.includes('$zipPath.sha256'), 'the IIS job must not trust a digest sidecar downloaded with the artifact');
const deploymentHashCheck = deploymentJob.indexOf('$actualHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()');
const deploymentExtraction = deploymentJob.indexOf('Expand-Archive -LiteralPath $zipPath -DestinationPath $bootstrapRoot -Force');
assert.ok(deploymentHashCheck >= 0, 'the IIS job must hash the downloaded ZIP inline');
assert.ok(deploymentJob.includes('throw "Deployment ZIP SHA-256 mismatch: expected $expectedHash, got $actualHash."'), 'a mismatched hosted digest must reject deployment');
assert.ok(deploymentHashCheck < deploymentExtraction, 'the IIS job must verify the ZIP before extracting artifact code');

for (const token of [
  'Get-FileHash',
  'ExpectedZipSha256',
  'Expand-Archive',
  'versionOrLabel',
  'commitSha',
  'dacpacSha256',
  'webPayloadSha256',
  'IisSiteName',
  'ExternalApplicationConfigPath',
  'Test-PublishedApplicationConfigPath',
  'Get-Website',
  'Stop-WebAppPool',
  'Get-WebAppPoolState',
  'Start-WebAppPool',
  'BlockOnPossibleDataLoss',
  'hangfireSchemaVersion',
  'sqlcmd',
  '/health/ready',
  'status -eq \'healthy\'',
  'deployment-state.json',
  'ValidateOnly'
]) {
  assert.ok(deployment.includes(token), `deployment entry point should implement ${token}`);
}

assert.ok(!deployment.includes('SkipCertificateCheck'), 'readiness must use normal TLS certificate validation');
assert.ok(!deployment.includes('[asap].[DeploymentState]'), 'test deployment must not repurpose application DeploymentState');
assert.ok(deployment.includes("'/p:BlockOnPossibleDataLoss=True'"), 'deployment must retain DACPAC data-loss protection');
assert.ok(deployment.includes("'/p:DropObjectsNotInSource=False'"), 'deployment must preserve objects outside the DACPAC');
assert.ok(deployment.includes("[string] $ConfigPath = 'C:\\ProgramData\\clc-asap\\Config\\deployment.json'"), 'deployment should default to the conventional host config path');
assert.ok(deployment.includes("(^|/)(application|deployment)\\.json$"), 'host-owned application and deployment JSON must be forbidden from the artifact');
const archiveValidation = deployment.indexOf('$archive = Test-DeploymentArchive', deployment.indexOf('$hostConfig = Read-HostConfiguration'));
const pointerValidation = deployment.indexOf('Test-PublishedApplicationConfigPath', archiveValidation);
const iisValidation = deployment.indexOf('$null = Test-IisConfiguration', archiveValidation);
const poolStop = deployment.indexOf('Stop-TestAppPool', iisValidation);
assert.ok(archiveValidation >= 0 && pointerValidation > archiveValidation, 'the staged web payload must be available before pointer validation');
assert.ok(pointerValidation < iisValidation && iisValidation < poolStop, 'pointer mismatch must fail before IIS validation or mutation');
assert.ok(activation.includes('gh workflow run dotnet.yml'), 'activation docs should provide the explicit CLI dispatch');
assert.ok(activation.includes('--ref v1.0.0-test.1'), 'activation docs should show an exact test tag ref');
assert.ok(activation.includes('ASAP_TEST_DEPLOYMENT_ENABLED=true'), 'activation docs should describe enabling the gate');
assert.ok(activation.includes('pending_runner_setup'), 'activation docs must distinguish repository acceptance from live activation');
assert.ok(activation.includes('"IisSiteName": "ASAP"'), 'activation docs should use the application name for the IIS site');
assert.ok(activation.includes('"IisAppPoolName": "ASAP"'), 'activation docs should use the application name for the IIS app pool');
assert.ok(activation.includes('"DeploymentPath": "D:\\\\Sites\\\\ASAP"'), 'activation docs should use the conventional live application path');
assert.ok(activation.includes('"StagingRoot": "C:\\\\ProgramData\\\\clc-asap\\\\Staging"'), 'activation docs should keep staging under the host root');
assert.ok(activation.includes('"BackupRoot": "C:\\\\ProgramData\\\\clc-asap\\\\Backups"'), 'activation docs should keep backups under the host root');
assert.ok(slice8.includes('C:\\ProgramData\\clc-asap\\Config\\deployment.json'), 'Slice 8 should document the host-local config path');

console.log('Test-IIS deployment workflow and script contract tests passed.');
