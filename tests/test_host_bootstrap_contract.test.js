const assert = require('assert');
const childProcess = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

const root = path.resolve(__dirname, '..');
const bootstrap = path.join(root, 'scripts', 'deployment', 'Initialize-AsapTestHost.ps1');
const bootstrapSource = fs.readFileSync(bootstrap, 'utf8');
const documentedApplication = JSON.parse(
  fs.readFileSync(path.join(root, 'docs', 'dotnet-port', 'examples', 'Config.example.json'), 'utf8')
);
const deploymentSource = fs.readFileSync(path.join(root, 'scripts', 'deployment', 'Deploy-AsapTest.ps1'), 'utf8');
const workflowSource = fs.readFileSync(path.join(root, '.github', 'workflows', 'dotnet.yml'), 'utf8');
const appsettings = JSON.parse(fs.readFileSync(path.join(root, 'src', 'Asap.Web', 'appsettings.json'), 'utf8'));
const developmentAppsettings = JSON.parse(fs.readFileSync(path.join(root, 'src', 'Asap.Web', 'appsettings.Development.json'), 'utf8'));

function snapshotTree(directory) {
  const entries = [];
  function visit(current) {
    for (const name of fs.readdirSync(current).sort()) {
      const fullPath = path.join(current, name);
      const relativePath = path.relative(directory, fullPath);
      const stat = fs.statSync(fullPath);
      if (stat.isDirectory()) {
        entries.push({ path: relativePath, type: 'directory', modified: stat.mtimeMs });
        visit(fullPath);
      } else {
        entries.push({
          path: relativePath,
          type: 'file',
          modified: stat.mtimeMs,
          contents: fs.readFileSync(fullPath, 'utf8')
        });
      }
    }
  }
  visit(directory);
  return entries;
}

const parse = childProcess.spawnSync(
  'pwsh',
  [
    '-NoLogo',
    '-NoProfile',
    '-Command',
    '$tokens = $null; $errors = $null; [void][Management.Automation.Language.Parser]::ParseFile($env:BOOTSTRAP_PATH, [ref]$tokens, [ref]$errors); if ($errors.Count) { $errors | Out-String | Write-Error; exit 1 }'
  ],
  { encoding: 'utf8', env: { ...process.env, BOOTSTRAP_PATH: bootstrap } }
);
assert.strictEqual(parse.status, 0, parse.stderr || parse.stdout);

const obsoleteMode = childProcess.spawnSync(
  'pwsh',
  ['-NoLogo', '-NoProfile', '-File', bootstrap, '-ValidateOnly'],
  { encoding: 'utf8' }
);
assert.notStrictEqual(obsoleteMode.status, 0, '-ValidateOnly must not remain a supported switch');
assert.match(`${obsoleteMode.stdout}\n${obsoleteMode.stderr}`, /parameter name 'ValidateOnly'/i);

const selfTest = childProcess.spawnSync(
  'pwsh',
  ['-NoLogo', '-NoProfile', '-File', bootstrap, '-ContractSelfTest'],
  { encoding: 'utf8' }
);
assert.strictEqual(selfTest.status, 0, selfTest.stderr || selfTest.stdout);
assert.match(selfTest.stdout, /bootstrap self-test passed/i);

const exclusiveModes = childProcess.spawnSync(
  'pwsh',
  ['-NoLogo', '-NoProfile', '-File', bootstrap, '-Initialize', '-ContractSelfTest'],
  { encoding: 'utf8' }
);
assert.notStrictEqual(exclusiveModes.status, 0, '-Initialize and -ContractSelfTest must be mutually exclusive');

const isolatedRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-bootstrap-isolated-'));
try {
  const standaloneBootstrap = path.join(isolatedRoot, 'Initialize-AsapTestHost.ps1');
  const hostRoot = path.join(isolatedRoot, 'host');
  fs.copyFileSync(bootstrap, standaloneBootstrap);
  fs.mkdirSync(hostRoot);

  const beforeDefaultValidation = snapshotTree(isolatedRoot);
  const defaultValidation = childProcess.spawnSync(
    'pwsh',
    ['-NoLogo', '-NoProfile', '-File', standaloneBootstrap, '-RootPath', hostRoot],
    { encoding: 'utf8', cwd: isolatedRoot }
  );
  assert.notStrictEqual(defaultValidation.status, 0, 'missing host files must fail validation');
  const defaultValidationOutput = `${defaultValidation.stdout}\n${defaultValidation.stderr}`;
  assert.match(defaultValidationOutput, /pwsh -File .*Initialize-AsapTestHost\.ps1/i);
  assert.match(defaultValidationOutput, /-Initialize/i, 'validation should tell the operator how to initialize the host');
  assert.deepStrictEqual(
    snapshotTree(isolatedRoot),
    beforeDefaultValidation,
    'default validation must not mutate the script directory or supplied host root'
  );

  const initialize = childProcess.spawnSync(
    'pwsh',
    ['-NoLogo', '-NoProfile', '-File', standaloneBootstrap, '-Initialize', '-RootPath', hostRoot],
    { encoding: 'utf8', cwd: isolatedRoot }
  );
  assert.strictEqual(initialize.status, 0, initialize.stderr || initialize.stdout);
  assert.match(initialize.stdout, /rerun this script with no flags/i);

  for (const directory of ['Config', 'DataProtection-Keys', 'Logs', 'Staging', 'Backups']) {
    assert.ok(fs.statSync(path.join(hostRoot, directory)).isDirectory(), `${directory} should be initialized`);
  }
  assert.deepStrictEqual(
    fs.readdirSync(hostRoot).sort(),
    ['Backups', 'Config', 'DataProtection-Keys', 'Logs', 'Staging'].sort(),
    'initialization should create only the ASAP-owned layout'
  );
  assert.ok(fs.statSync(path.join(hostRoot, 'Config', 'application.json')).isFile());
  assert.ok(fs.statSync(path.join(hostRoot, 'Config', 'deployment.json')).isFile());
  assert.deepStrictEqual(fs.readdirSync(path.join(hostRoot, 'Config')).sort(), ['application.json', 'deployment.json']);

  const applicationPath = path.join(hostRoot, 'Config', 'application.json');
  const deploymentPath = path.join(hostRoot, 'Config', 'deployment.json');
  const generatedApplication = JSON.parse(fs.readFileSync(applicationPath, 'utf8'));
  const generatedDeployment = JSON.parse(fs.readFileSync(deploymentPath, 'utf8'));
  assert.deepStrictEqual(generatedApplication.Authentication.Entra.InitialSuperAdmin, {
    UserPrincipalName: 'REPLACE-ADMIN-EMAIL',
    DisplayName: 'ASAP Initial Administrator',
    NotificationEmail: 'REPLACE-ADMIN-EMAIL'
  });
  assert.ok(
    !Object.hasOwn(generatedApplication.Authentication.Entra.InitialSuperAdmin, 'TenantId'),
    'new bootstrap configuration must not emit a tenant identity field'
  );
  assert.ok(
    !Object.hasOwn(generatedApplication.Authentication.Entra.InitialSuperAdmin, 'ObjectId'),
    'new bootstrap configuration must not emit an object identity field'
  );
  assert.deepStrictEqual(
    generatedApplication.Authentication.Entra.AllowedTenantIds,
    ['REPLACE-TENANT-ID'],
    'AllowedTenantIds remains required in the generated configuration'
  );
  const normalizedGeneratedApplication = JSON.parse(JSON.stringify(generatedApplication));
  normalizedGeneratedApplication.Application.DataProtectionKeysPath = 'REPLACE-DATA-PROTECTION-KEYS-PATH';
  normalizedGeneratedApplication.Application.LogPath = 'REPLACE-LOG-PATH';
  assert.deepStrictEqual(
    normalizedGeneratedApplication,
    documentedApplication,
    'the documentation example must match the complete embedded operational template'
  );
  assert.deepStrictEqual(
    Object.keys(generatedApplication).sort(),
    [
      'Application',
      'Authentication',
      'ConnectionStrings',
      'EmailSafety',
      'EmailTransport',
      'Environment',
      'Hangfire',
      'PatronLoginRateLimit'
    ].sort()
  );
  assert.deepStrictEqual(generatedApplication.PatronLoginRateLimit, {
    PermitLimit: 20,
    WindowSeconds: 300
  });
  assert.strictEqual(
    generatedApplication.Application.DataProtectionKeysPath,
    path.join(hostRoot, 'DataProtection-Keys')
  );
  assert.strictEqual(generatedApplication.Application.LogPath, path.join(hostRoot, 'Logs'));
  assert.strictEqual(generatedDeployment.StagingRoot, path.join(hostRoot, 'Staging'));
  assert.strictEqual(generatedDeployment.BackupRoot, path.join(hostRoot, 'Backups'));
  assert.strictEqual(generatedDeployment.ExternalApplicationConfigPath, applicationPath);

  const operatorApplication = '{\n  "operatorEdited": true\n}\n';
  const operatorDeployment = '{\n  "operatorEdited": true\n}\n';
  fs.writeFileSync(applicationPath, operatorApplication);
  fs.writeFileSync(deploymentPath, operatorDeployment);

  const repeat = childProcess.spawnSync(
    'pwsh',
    ['-NoLogo', '-NoProfile', '-File', standaloneBootstrap, '-Initialize', '-RootPath', hostRoot],
    { encoding: 'utf8', cwd: isolatedRoot }
  );
  assert.strictEqual(repeat.status, 0, repeat.stderr || repeat.stdout);
  assert.strictEqual(fs.readFileSync(applicationPath, 'utf8'), operatorApplication);
  assert.strictEqual(fs.readFileSync(deploymentPath, 'utf8'), operatorDeployment);

  const beforeValidation = snapshotTree(hostRoot);
  const validation = childProcess.spawnSync(
    'pwsh',
    ['-NoLogo', '-NoProfile', '-File', standaloneBootstrap, '-RootPath', hostRoot],
    { encoding: 'utf8', cwd: isolatedRoot }
  );
  assert.notStrictEqual(validation.status, 0, 'blocking validation failures should return nonzero');
  assert.deepStrictEqual(snapshotTree(hostRoot), beforeValidation, 'default validation must remain read-only');
} finally {
  fs.rmSync(isolatedRoot, { recursive: true, force: true });
}

assert.ok(bootstrapSource.includes("[string] $RootPath = 'C:\\ProgramData\\clc-asap'"));
assert.ok(bootstrapSource.includes("$store = 'Cert:\\LocalMachine\\My'"));
assert.ok(bootstrapSource.includes('$certificate.HasPrivateKey'));
for (const forbidden of [
  'New-WebAppPool',
  'New-Website',
  'Set-ItemProperty',
  'Stop-WebAppPool',
  'Start-WebAppPool',
  'Restart-Web',
  'New-SelfSignedCertificate',
  'Import-PfxCertificate',
  'New-LocalUser',
  'Add-LocalGroupMember',
  'Set-Acl',
  'icacls',
  'Install-WindowsFeature',
  'Enable-WindowsOptionalFeature',
  'Get-Credential',
  '[PSCredential]',
  'Asap__ConfigFile',
  'Win32_Service',
  'Invoke-WebRequest',
  'Invoke-RestMethod',
  'Cert:\\CurrentUser\\My'
]) {
  assert.ok(!bootstrapSource.includes(forbidden), `bootstrap must not contain provisioning behavior: ${forbidden}`);
}

assert.strictEqual(appsettings.Asap.ConfigFile, 'C:\\ProgramData\\clc-asap\\Config\\application.json');
assert.strictEqual(developmentAppsettings.Asap.ConfigFile, 'Development.local.json');
assert.ok(deploymentSource.includes("[string] $ConfigPath = 'C:\\ProgramData\\clc-asap\\Config\\deployment.json'"));
assert.ok(deploymentSource.includes("'deployment-state.json'"));
assert.ok(deploymentSource.includes('Stop-TestAppPool'));
assert.ok(deploymentSource.includes('Start-TestAppPool'));
assert.ok(deploymentSource.includes("'/health/ready'"));
assert.ok(!workflowSource.includes('test-deployment.json'), 'workflow must use PR #275 deployment.json contract');

console.log('ASAP test-host bootstrap contract tests passed.');
