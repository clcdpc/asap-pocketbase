'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { execFile } = require('node:child_process');
const { promisify } = require('node:util');
const path = require('node:path');
const execute = promisify(execFile);
const root = path.resolve(__dirname, '../..');
for (const name of ['configuration', 'revision', 'isolation', 'proof', 'lifecycle', 'compose']) {
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
