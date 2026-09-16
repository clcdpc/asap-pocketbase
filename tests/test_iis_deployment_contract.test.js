const assert = require('assert');
const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..');
const workflow = fs.readFileSync(path.join(root, '.github/workflows/dotnet.yml'), 'utf8').replace(/\r\n/g, '\n');
const deployment = fs.readFileSync(path.join(root, 'scripts/deployment/Deploy-AsapTest.ps1'), 'utf8');
const bootstrap = fs.readFileSync(path.join(root, 'scripts/deployment/Initialize-AsapTestHost.ps1'), 'utf8');
const activation = fs.readFileSync(path.join(root, 'docs/implementation/test-iis-activation.md'), 'utf8');

assert.ok(workflow.includes("tags:\n      - 'v*.*.*-test.*'"), 'test deployment tags should use the documented convention');
assert.ok(workflow.includes('- codex/csharp-port'), 'integration-branch push trigger is required');
assert.ok(workflow.includes('- codex/slice-08-test-deployment'), 'candidate push trigger is required');
assert.ok(workflow.includes('  workflow_dispatch:\n'), 'manual dispatch should be declared');
assert.ok(workflow.includes('Generate ephemeral SQL test credentials'), 'CI SQL credentials should be generated per run');
assert.ok(!workflow.includes('Asap_Slice0_SQL_2026'), 'CI must not retain the historical hard-coded SQL password');
assert.ok(workflow.includes('ref: ${{ github.sha }}'), 'the hosted job should check out the exact event SHA');
assert.ok(workflow.includes('--minimum-expected-tests 310'), 'the non-browser real-SQL partition must retain its test-count guard');
assert.ok(workflow.includes('run: npm test'), 'the frontend test gate must remain');
assert.ok(workflow.includes('dotnet publish src/Asap.Web/Asap.Web.csproj'), 'Web publish must remain a hosted check');
assert.ok(workflow.includes('dotnet publish src/Asap.Migration/Asap.Migration.csproj'), 'native migration publish check must remain');

const deploymentJobStart = workflow.indexOf('  deploy-test-iis:');
assert.ok(deploymentJobStart > 0, 'the deployment job should be present');
const hostedJobs = workflow.slice(0, deploymentJobStart);
const deploymentJob = workflow.slice(deploymentJobStart);
assert.ok(!hostedJobs.includes('self-hosted'), 'hosted build/test/package jobs must not use self-hosted labels');
assert.ok(!hostedJobs.includes('ASAP_TEST_DEPLOYMENT_ENABLED'), 'hosted build/test/package jobs must not depend on activation');
assert.ok(
  deploymentJob.includes(
    "if: ${{ vars.ASAP_TEST_DEPLOYMENT_ENABLED == 'true' && (github.event_name == 'workflow_dispatch' || (github.event_name == 'push' && startsWith(github.ref, 'refs/tags/v'))) }}"
  ),
  'deployment must require the explicit activation variable and an eligible event'
);
assert.ok(
  deploymentJob.includes('runs-on: [self-hosted, windows, x64, asap-test-iis]'),
  'deployment must be isolated to the dedicated test-IIS runner labels'
);
assert.ok(!deploymentJob.includes('actions/checkout'), 'the IIS job must not check out the repository');
assert.ok(workflow.includes('actions/download-artifact@v4'), 'the IIS job must consume the hosted artifact');
assert.ok(workflow.includes('zip_sha256: ${{ steps.package.outputs.zip_sha256 }}'), 'the hosted package job must expose the final ZIP digest as a job output');
assert.ok(workflow.includes('id: package'), 'the ZIP packaging step must own the digest output');
assert.ok(workflow.includes('"zip_sha256=$zipHash" >> $env:GITHUB_OUTPUT'), 'the package step must emit the computed final ZIP digest');
assert.ok(
  deploymentJob.includes('EXPECTED_ZIP_SHA256: ${{ needs.build-test-package.outputs.zip_sha256 }}'),
  'the IIS job must receive the hosted package digest through its environment'
);
assert.ok(!deploymentJob.includes('$zipPath.sha256'), 'the IIS job must not trust a digest sidecar downloaded with the artifact');
assert.ok(
  deploymentJob.includes("-ConfigPath 'C:\\ProgramData\\clc-asap\\config\\test-deployment.json'"),
  'the deployment job must use the bootstrapped host configuration'
);
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
  'Get-Website',
  'Stop-WebAppPool',
  'Get-WebAppPoolState',
  'Start-WebAppPool',
  'BlockOnPossibleDataLoss',
  'hangfireSchemaVersion',
  'sqlcmd',
  '/health/ready',
  'status -eq \'healthy\'',
  'test-deployment-state.json',
  'ValidateOnly'
]) {
  assert.ok(deployment.includes(token), `deployment entry point should implement ${token}`);
}

assert.ok(!deployment.includes('SkipCertificateCheck'), 'readiness must use normal TLS certificate validation');
assert.ok(!deployment.includes('[asap].[DeploymentState]'), 'test deployment must not repurpose application DeploymentState');

for (const token of [
  "[string] $DatabaseName = 'AsapTest'",
  "[string] $RootPath = 'C:\\ProgramData\\clc-asap'",
  "Where-Object Name -Like 'actions.runner.*'",
  'Get-CimInstance Win32_Service',
  'IIS_IUSRS membership',
  'IIS lifecycle permission',
  'New-SelfSignedCertificate',
  'Asap__ConfigFile',
  'test-deployment.json',
  'test-app.json',
  "Join-Path $root 'web'",
  "Join-Path $root 'staging'",
  "Join-Path $root 'backups'",
  'ValidateOnly'
]) {
  assert.ok(bootstrap.includes(token), `test host bootstrap should implement ${token}`);
}
assert.ok(
  bootstrap.includes("Add-Result 'INFO' 'SQL authorization'"),
  'bootstrap must leave SQL privilege grants as an explicit operator boundary'
);
assert.ok(
  bootstrap.includes("[switch] $SkipLocalAdministrator"),
  'bootstrap should allow separately provisioned narrower IIS lifecycle rights'
);

assert.ok(activation.includes('Initialize-AsapTestHost.ps1'), 'activation docs should use the automated host bootstrap');
assert.ok(activation.includes('C:\\ProgramData\\clc-asap\\config\\test-deployment.json'), 'activation docs should use the current host config path');
assert.ok(activation.includes('gh workflow run dotnet.yml'), 'activation docs should provide the explicit CLI dispatch');
assert.ok(activation.includes('--ref v1.0.0-test.1'), 'activation docs should show an exact test tag ref');
assert.ok(activation.includes('ASAP_TEST_DEPLOYMENT_ENABLED=true'), 'activation docs should describe enabling the gate');

console.log('Test-IIS deployment workflow, deployment script, and host bootstrap contract tests passed.');
