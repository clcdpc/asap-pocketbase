const assert = require('assert');
const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..');
const workflow = fs.readFileSync(path.join(root, '.github/workflows/dotnet.yml'), 'utf8');
const deployment = fs.readFileSync(path.join(root, 'scripts/deployment/Deploy-AsapTest.ps1'), 'utf8');
const activation = fs.readFileSync(path.join(root, 'docs/implementation/test-iis-activation.md'), 'utf8');
const slice8 = fs.readFileSync(path.join(root, 'docs/implementation/slice-08.md'), 'utf8');

assert.ok(workflow.includes("tags:\n      - 'v*.*.*-test.*'"), 'test deployment tags should use the documented convention');
assert.ok(workflow.includes('- codex/csharp-port'), 'integration-branch push trigger is required');
assert.ok(workflow.includes('- codex/slice-08-test-deployment'), 'candidate push trigger is required');
assert.ok(workflow.includes('  workflow_dispatch:\n'), 'manual dispatch should be declared');
assert.ok(workflow.includes('Generate ephemeral SQL test credentials'), 'CI SQL credentials should be generated per run');
assert.ok(!workflow.includes('Asap_Slice0_SQL_2026'), 'CI must not retain the historical hard-coded SQL password');
assert.ok(workflow.includes('ref: ${{ github.sha }}'), 'the hosted job should check out the exact event SHA');
assert.ok(workflow.includes('--minimum-expected-tests 190'), 'the real-SQL minimum test-count guard must remain');
assert.ok(workflow.includes('run: npm test'), 'the frontend test gate must remain');
assert.ok(workflow.includes('dotnet publish src/Asap.Web/Asap.Web.csproj'), 'Web publish must remain a hosted check');
assert.ok(workflow.includes('dotnet publish src/Asap.Migration/Asap.Migration.csproj'), 'native migration publish check must remain');

const deploymentJobStart = workflow.indexOf('  deploy-test-iis:');
assert.ok(deploymentJobStart > 0, 'the deployment job should be present');
const hostedJobs = workflow.slice(0, deploymentJobStart);
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
assert.ok(activation.includes('gh workflow run dotnet.yml'), 'activation docs should provide the explicit CLI dispatch');
assert.ok(activation.includes('--ref v1.0.0-test.1'), 'activation docs should show an exact test tag ref');
assert.ok(activation.includes('ASAP_TEST_DEPLOYMENT_ENABLED=true'), 'activation docs should describe enabling the gate');
assert.ok(activation.includes('pending_runner_setup'), 'activation docs must distinguish repository acceptance from live activation');
assert.ok(slice8.includes('C:\\ProgramData\\ASAP\\test-deployment.json'), 'Slice 8 should document the host-local config path');

console.log('Test-IIS deployment workflow and script contract tests passed.');
