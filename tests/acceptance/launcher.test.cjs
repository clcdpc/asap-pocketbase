'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { existsSync } = require('node:fs');
const { execFile } = require('node:child_process');
const { promisify } = require('node:util');
const path = require('node:path');
const execute = promisify(execFile);
const root = path.resolve(__dirname, '../..');
for (const name of ['authentication', 'configuration', 'revision', 'isolation', 'proof', 'lifecycle', 'compose']) {
  test('actual PowerShell launcher helpers: ' + name, async () => {
    const output = await execute('pwsh', ['-NoProfile', '-File', path.join(__dirname, 'launcher.test.ps1'), '-Case', name],
      { cwd: root, timeout: 30000, windowsHide: true });
    assert.match(output.stdout, new RegExp('PASS: ' + name));
  });
}
test('public launcher and compatibility entry reject invalid modes with exit 2 and no host Node', async () => {
  for (const args of [ ['-Pr', '410', '-Commit', 'HEAD'], ['-Suite', 'unknown'],
    ['-Suite', 'pr410', '-ReadOnly', '-AllowReviewWrites'], ['-AllowReviewWrites'] ]) {
    await assert.rejects(execute('pwsh', ['-NoProfile', '-File', path.join(root, 'scripts/review/Invoke-AsapReview.ps1'), ...args],
      { cwd: root, timeout: 15000, windowsHide: true }), error => error.code === 2 && /BLOCKED:/.test(error.stdout));
  }
  await assert.rejects(execute('pwsh', ['-NoProfile', '-File', path.join(root, 'scripts/review/Invoke-AsapAcceptance.ps1'),
    '-Suite', 'unknown', '-ExpectedCommit', 'a'.repeat(40)], { cwd: root, timeout: 15000, windowsHide: true }),
  error => error.code === 2 && /Unknown suite/.test(error.stdout));
});

test('PowerShell recognizes smoke from the shared registry without host Node or Playwright', async () => {
  const env = { ...process.env };
  const pathKey = Object.keys(env).find(key => key.toLowerCase() === 'path');
  env[pathKey] = env[pathKey].split(path.delimiter).filter(directory =>
    !['node', 'node.exe'].some(name => existsSync(path.join(directory, name)))).join(path.delimiter);
  const launcher = path.join(root, 'scripts/review/Invoke-AsapReview.ps1').replace(/'/g, "''");
  await assert.rejects(execute('pwsh', ['-NoProfile', '-Command',
    "if (Get-Command node -ErrorAction SilentlyContinue) { throw 'Host Node is still available' }; & '" + launcher +
    "' -Pr 410 -Suite smoke -Port 8081; exit $LASTEXITCODE"], { cwd: root, env, timeout: 15000, windowsHide: true }),
  error => error.code === 2 && /Container acceptance uses the registered/.test(error.stdout));
});
