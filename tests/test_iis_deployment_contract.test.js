const assert = require('assert');
const fs = require('fs');
const path = require('path');
const childProcess = require('child_process');
const YAML = require('yaml');

const root = path.resolve(__dirname, '..');
const workflow = fs.readFileSync(path.join(root, '.github/workflows/dotnet.yml'), 'utf8').replace(/\r\n/g, '\n');
const browserRunner = fs.readFileSync(path.join(root, 'tests/browser/run.cjs'), 'utf8').replace(/\r\n/g, '\n');
const deployment = fs.readFileSync(path.join(root, 'scripts/deployment/Deploy-AsapTest.ps1'), 'utf8');
const runtimeSchemaVersion = fs.readFileSync(
  path.join(root, 'src', 'Asap.Web', 'Infrastructure', 'Data', 'SchemaVersion.cs'),
  'utf8'
);
const postDeployment = fs.readFileSync(
  path.join(root, 'database', 'Asap.Database', 'Scripts', 'PostDeployment.sql'),
  'utf8'
);

childProcess.execFileSync(
  'pwsh',
  ['-NoLogo', '-NoProfile', '-File', path.join(__dirname, 'test_deployment_configuration_preflight.ps1')],
  { stdio: 'inherit' }
);
childProcess.execFileSync(
  'pwsh',
  ['-NoLogo', '-NoProfile', '-File', path.join(__dirname, 'test_deployment_sql_plan.ps1')],
  { stdio: 'inherit' }
);
childProcess.execFileSync(
  'pwsh',
  ['-NoLogo', '-NoProfile', '-File', path.join(__dirname, 'test_deployment_automatic.ps1')],
  { stdio: 'inherit' }
);

assert.ok(workflow.includes("tags:\n      - 'v*.*.*'"), 'all release tags must qualify every category');
assert.deepEqual(YAML.parse(workflow).on.push.branches, ['main'], 'only the supported main branch has push qualification');
assert.ok(workflow.includes('  workflow_dispatch:\n'), 'manual dispatch should be declared');
assert.ok(
  workflow.includes(
    "concurrency:\n  group: ${{ github.workflow }}-${{ github.event_name == 'pull_request' && format('pr-{0}', github.event.pull_request.number) || format('run-{0}-{1}', github.run_id, github.run_attempt) }}\n  cancel-in-progress: ${{ github.event_name == 'pull_request' }}\n\njobs:"
  ),
  'workflow concurrency should cancel superseded PR runs and isolate non-PR executions'
);
assert.equal(YAML.parse(workflow, { uniqueKeys: true }).jobs['build-test-package'].concurrency, undefined,
  'build-test-package must not retain job-level concurrency');
assert.ok(workflow.includes('Generate ephemeral SQL test credentials'), 'CI SQL credentials should be generated per run');
assert.ok(!workflow.includes('Asap_Slice0_SQL_2026'), 'CI must not retain the historical hard-coded SQL password');
assert.ok(workflow.includes('ref: ${{ github.sha }}'), 'the hosted job should check out the exact event SHA');
const execution = YAML.parse(workflow, { uniqueKeys: true }).jobs['build-test-package'];
const selectionStep = execution.steps.find((step) => step.name === 'Resolve required test population');
assert.equal(selectionStep.run, 'python3 -B scripts/ci/selection.py prepare');
assert.equal(selectionStep['continue-on-error'], undefined, 'population verification is mandatory');
const nonBrowserStep = YAML.parse(workflow, { uniqueKeys: true }).jobs['dotnet-qualification'].steps.find((step) => step.id === 'dotnet');
assert.equal(nonBrowserStep.run, 'bash scripts/ci/run-with-performance.sh python3 -B scripts/ci/selection.py run');
assert.ok(!/--minimum-expected-tests [0-9]+/.test(nonBrowserStep.run), 'non-browser counts must derive from complete native discovery');
const reviewed = JSON.parse(fs.readFileSync(path.join(root, 'scripts/ci/reviewed-test-categories.json'), 'utf8'));
assert.ok(reviewed.Browser.length > 0, "the reviewed browser method population must be nonempty");
assert.equal(new Set(reviewed.Browser).size, reviewed.Browser.length);
assert.ok(browserRunner.includes("filter: 'TestCategory=Browser'"), 'standalone browser execution must use the explicit category');
assert.ok(browserRunner.includes('String(selected.expected)'), 'CI browser minimum must use native discovered population');
assert.ok(workflow.includes('run: npm test'), 'the frontend test gate must remain');
assert.ok(workflow.includes('dotnet publish src/Asap.Web/Asap.Web.csproj'), 'Web publish must remain a hosted check');
assert.ok(!workflow.includes('src/Asap.Migration'), 'retired migration executable must not be built or published');

const schemaVersions = {
  workflow: Number(workflow.match(/applicationSchemaVersion = (\d+)/)?.[1]),
  deployment: Number(deployment.match(/manifest\.applicationSchemaVersion -ne (\d+)/)?.[1]),
  runtime: Number(runtimeSchemaVersion.match(/ExpectedVersion = (\d+)/)?.[1]),
  postDeployment: Number(postDeployment.match(/VALUES \(1, (\d+), SYSUTCDATETIME\(\)\)/)?.[1])
};
assert.ok(
  Object.values(schemaVersions).every((version) => version === 13),
  `application schema version must be 13 in every package/runtime contract: ${JSON.stringify(schemaVersions)}`
);
assert.match(
  fs.readFileSync(path.join(root, 'database/Asap.Database/Scripts/PreDeployment.sql'), 'utf8'),
  /Version.*< 13/,
  'the DACPAC must reject every unsupported earlier schema before changing data'
);

const deploymentJobStart = workflow.indexOf('  deploy-test-iis:');
assert.ok(deploymentJobStart > 0, 'the deployment job should be present');
const hostedJobs = workflow.slice(0, deploymentJobStart);
const deploymentJob = workflow.slice(deploymentJobStart);
assert.ok(!hostedJobs.includes('self-hosted'), 'hosted build/test/package jobs must not use self-hosted labels');
const workflowJobs = YAML.parse(workflow, { uniqueKeys: true }).jobs;
for (const job of [workflowJobs['validation-policy'], workflowJobs['build-test-package'], workflowJobs['dotnet-qualification'], workflowJobs['browser-qualification'], workflowJobs['draft-validation']]) {
  assert.ok(!JSON.stringify(job).includes('ASAP_TEST_DEPLOYMENT_ENABLED'), 'hosted build/test/package jobs must not depend on activation');
}
assert.match(
  workflow,
  /  workflow_dispatch:\n    inputs:\n      deploy_test_iis:\n(?:        [^\n]+\n)*        default: false\n/,
  'manual workflow dispatch must default to no test-IIS deployment'
);
const deployWorkflow = YAML.parse(workflow, { uniqueKeys: true }).jobs['deploy-test-iis'];
assert.deepEqual(deployWorkflow.environment, { name: 'asap-test-iis', deployment: false },
  'safe skips must not generate successful implicit GitHub deployment records');
assert.deepEqual(deployWorkflow.concurrency, { group: 'asap-test-iis', 'cancel-in-progress': false, queue: 'max' },
  'IIS activation must be serialized without interrupting a running SQL deployment');
const deployCondition = deployWorkflow.if;
assert.match(deployCondition, /!\s*cancelled\(\)/,
  'the deployment gate must not inherit skipped draft-validation status');
assert.match(deployCondition, /needs\.build-test-package\.result\s*==\s*'success'/,
  'deployment must require successful full CI execution');
assert.match(deployCondition, /needs\.validation\.result\s*==\s*'success'/,
  'deployment must require the successful reporting gate');
assert.ok(deployCondition.includes("vars.ASAP_TEST_DEPLOYMENT_ENABLED == 'true'"),
  'deployment must retain the repository activation gate');
assert.ok(deployCondition.includes("(github.event_name == 'workflow_dispatch' && inputs.deploy_test_iis)"),
  'manual deployment must require explicit opt-in');
assert.ok(deployCondition.includes("needs.validation-policy.outputs.test_tag == 'true'"),
  'test-release tags must remain eligible for automatic deployment');
assert.ok(deployCondition.includes("github.ref == 'refs/heads/main' && needs.validation.outputs.automatic_current == 'true'"),
  'current main pushes must become eligible only after hosted freshness preflight');
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
assert.ok(deployment.includes("'/p:CreateNewDatabase=False'"), 'deployment must not recreate the database');
assert.ok(deployment.includes("'/p:ScriptDatabaseOptions=False'"), 'DACPAC must leave generic environment options alone');
assert.match(fs.readFileSync(path.join(root, 'src/Asap.Web/Infrastructure/Development/DacpacDeploymentService.cs'), 'utf8'),
  /ScriptDatabaseOptions = false/, 'development publication must also preserve environment options');
const recovery = deployment.indexOf('    Set-NonProductionDatabaseRecovery `', deployment.indexOf('$hostConfig = Read-HostConfiguration'));
const exactSuccess = deployment.indexOf('The exact test-IIS artifact is already recorded and ready');
assert.ok(recovery > deployment.indexOf('if ($hangfireVersion -gt 9)') && recovery < exactSuccess,
  'recovery enforcement must follow host/schema preflight and precede exact-artifact success');
assert.ok(recovery < deployment.indexOf('    Invoke-TestDeployment -Config $hostConfig'), 'recovery must run independently of DACPAC publication');
assert.match(deployment.slice(recovery, exactSuccess), /-ConnectionString \$hostConfig\.AsapDatabaseConnectionString/);
assert.strictEqual((deployment.match(/    Set-NonProductionDatabaseRecovery `/g) || []).length, 1, 'Hangfire must not receive a separate recovery command');
assert.ok(!/RECOVERY\s+SIMPLE/i.test(postDeployment + fs.readFileSync(path.join(root, 'database/Asap.Database/Scripts/PreDeployment.sql'), 'utf8')),
  'DACPAC scripts must not own recovery policy');
assert.match(postDeployment, /ALTER DATABASE CURRENT SET COMPATIBILITY_LEVEL = 160;/);
assert.ok(deployment.includes("[string] $ConfigPath = 'C:\\ProgramData\\clc-asap\\Config\\deployment.json'"), 'deployment should default to the conventional host config path');
assert.ok(deployment.includes("(^|/)(application|deployment)\\.json$"), 'host-owned application and deployment JSON must be forbidden from the artifact');
const archiveValidation = deployment.indexOf('$archive = Test-DeploymentArchive', deployment.indexOf('$hostConfig = Read-HostConfiguration'));
const pointerValidation = deployment.indexOf('Test-PublishedApplicationConfigPath', archiveValidation);
const iisValidation = deployment.indexOf('$null = Test-IisConfiguration', archiveValidation);
const poolStop = deployment.indexOf('    Invoke-TestDeployment -Config $hostConfig', iisValidation);
assert.ok(archiveValidation >= 0 && pointerValidation > archiveValidation, 'the staged web payload must be available before pointer validation');
assert.ok(pointerValidation < iisValidation && iisValidation < poolStop, 'pointer mismatch must fail before IIS validation or mutation');

console.log('Test-IIS deployment workflow and script contract tests passed.');
