const assert = require('assert');
const childProcess = require('child_process');
const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..');
const provisioningPath = path.join(
  root,
  'scripts',
  'deployment',
  'New-AsapDataProtectionCertificate.ps1'
);
const bootstrapPath = path.join(root, 'scripts', 'deployment', 'Initialize-AsapTestHost.ps1');
const provisioning = fs.readFileSync(provisioningPath, 'utf8');
const bootstrap = fs.readFileSync(bootstrapPath, 'utf8');

const parse = childProcess.spawnSync(
  'pwsh',
  [
    '-NoLogo',
    '-NoProfile',
    '-Command',
    '$tokens = $null; $errors = $null; [void][Management.Automation.Language.Parser]::ParseFile($env:SCRIPT_PATH, [ref]$tokens, [ref]$errors); if ($errors.Count) { $errors | Out-String | Write-Error; exit 1 }'
  ],
  { encoding: 'utf8', env: { ...process.env, SCRIPT_PATH: provisioningPath } }
);
assert.strictEqual(parse.status, 0, parse.stderr || parse.stdout);

assert.match(provisioning, /#Requires -Version 7\.0/);
assert.match(provisioning, /\[Parameter\(Mandatory = \$true\)\][\s\S]*\[string\] \$RuntimeIdentity/);
assert.match(provisioning, /\[Parameter\(Mandatory = \$true\)\][\s\S]*\[string\] \$PfxBackupPath/);
assert.ok(provisioning.includes("[string] $EnvironmentName = 'Test'"));
assert.ok(provisioning.includes("[string] $ApplicationConfigPath = 'C:\\ProgramData\\clc-asap\\Config\\application.json'"));
assert.ok(!provisioning.match(/param\([\s\S]*\$PfxPassword/), 'the PFX password must not be a command-line parameter');
assert.ok(provisioning.includes("Read-Host 'Enter a password for the Data Protection recovery PFX' -AsSecureString"));
assert.ok(provisioning.includes("Read-Host 'Confirm the Data Protection recovery PFX password' -AsSecureString"));

assert.ok(provisioning.includes("$script:CertificateStorePath = 'Cert:\\LocalMachine\\My'"));
assert.ok(provisioning.includes("$script:KeyStorageProvider = 'Microsoft Software Key Storage Provider'"));
assert.ok(provisioning.includes('-CertStoreLocation $script:CertificateStorePath'));
assert.ok(provisioning.includes('-Provider $script:KeyStorageProvider'));
assert.ok(provisioning.includes('-KeyAlgorithm RSA'));
assert.ok(provisioning.includes('-KeyExportPolicy Exportable'));
assert.ok(!provisioning.includes('-KeyLocation Machine'));
assert.ok(!provisioning.includes('-KeySpec KeyExchange'));
const keySpecValues = [...provisioning.matchAll(/-KeySpec\s+([A-Za-z]+)/g)].map((match) => match[1]);
assert.ok(keySpecValues.every((value) => value === 'None'), 'a CNG KeySpec, if present, must be None');
assert.ok(provisioning.includes('$rsa -isnot [Security.Cryptography.RSACng]'));
assert.ok(provisioning.includes('$rsa.Key.Provider.Provider.Equals($script:KeyStorageProvider'));
assert.ok(provisioning.includes('$rsa.Key.IsMachineKey'));
assert.ok(provisioning.includes("'Microsoft\\Crypto\\Keys'"));
assert.ok(provisioning.includes('[Security.AccessControl.FileSystemRights]::Read'));
assert.ok(provisioning.includes("'S-1-1-0'"), 'broad runtime principals should be rejected');
assert.ok(provisioning.includes("'S-1-5-11'"), 'broad runtime principals should be rejected');
assert.ok(provisioning.includes("'S-1-5-32-545'"), 'broad runtime principals should be rejected');
assert.ok(!provisioning.includes('[Security.AccessControl.FileSystemRights]::FullControl'));
assert.ok(!provisioning.includes('[Security.AccessControl.FileSystemRights]::Modify'));

assert.ok(provisioning.includes('Export-PfxCertificate'));
assert.ok(provisioning.includes('Get-PfxData -FilePath $Path -Password $Password'));
assert.ok(provisioning.includes('$matchingCertificate.HasPrivateKey'));
assert.match(provisioning, /PFX backup path already exists and will not be overwritten/);
assert.ok(
  provisioning.indexOf('if (Test-Path -LiteralPath $resolvedPfxBackupPath)') <
    provisioning.lastIndexOf('$pfxPassword = Read-ConfirmedPfxPassword'),
  'an existing PFX must fail before the password prompt or machine mutation'
);

assert.ok(provisioning.includes("$script:BootstrapPlaceholder = 'REPLACE-DATA-PROTECTION-CERTIFICATE-THUMBPRINT'"));
assert.ok(provisioning.includes('Retaining configured Data Protection certificate'));
assert.ok(provisioning.includes('The existing application.json value was not changed.'));
assert.ok(provisioning.includes('$initialThumbprint -ceq $script:BootstrapPlaceholder'));
assert.ok(provisioning.includes('$current.Thumbprint -cne $ExpectedValue'));
assert.ok(provisioning.includes('[IO.File]::Replace($temporaryPath, $Path, $null)'));
assert.ok(
  provisioning.lastIndexOf('Export-AndVerifyPfx -Certificate') <
    provisioning.lastIndexOf('Set-ApplicationCertificateThumbprint `'),
  'the verified PFX export must precede the application.json update'
);

for (const forbidden of [
  'DataProtection-Keys',
  'New-WebAppPool',
  'New-Website',
  'Stop-WebAppPool',
  'Start-WebAppPool',
  'Import-Module WebAdministration',
  'SqlPackage',
  'sqlcmd',
  'Authentication:Entra',
  'actions/runner',
  'config.cmd'
]) {
  assert.ok(!provisioning.includes(forbidden), `provisioning helper must not mutate unrelated host state: ${forbidden}`);
}

assert.ok(!bootstrap.includes('New-SelfSignedCertificate'));
assert.ok(!bootstrap.includes('Export-PfxCertificate'));
assert.ok(!bootstrap.includes('Set-Acl'));
assert.ok(!bootstrap.includes('New-AsapDataProtectionCertificate.ps1'));

console.log('ASAP Data Protection certificate provisioning contract tests passed.');
