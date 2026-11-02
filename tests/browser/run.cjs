'use strict';

const { spawn, spawnSync } = require('node:child_process');
const assert = require('node:assert/strict');
const { createHash } = require('node:crypto');
const path = require('node:path');

const repositoryRoot = path.resolve(__dirname, '..', '..');
const fs = require('node:fs');
const dotnet = process.platform === 'win32' ? 'dotnet.exe' : 'dotnet';
const discovery = spawnSync(dotnet, [
  'tests/Asap.Tests/bin/Release/net10.0/Asap.Tests.dll',
  '--list-tests', 'json', '--no-ansi', '--filter', 'TestCategory=Browser'
], { cwd: repositoryRoot, encoding: 'utf8', shell: false });
assert.ifError(discovery.error);
assert.equal(discovery.status, 0, discovery.stderr);
const catalog = JSON.parse(discovery.stdout);
assert.equal(catalog.schemaVersion, 1);
assert.ok(catalog.tests.length > 0);
const identities = catalog.tests.map((test) =>
  createHash('sha256').update(JSON.stringify(test.uid)).digest('hex')).sort();
assert.equal(new Set(identities).size, identities.length);
const selected = process.env.ASAP_CI_TEST_SELECTION
  ? JSON.parse(fs.readFileSync(process.env.ASAP_CI_TEST_SELECTION, 'utf8')).suites.browser
  : { filter: 'TestCategory=Browser', expected: identities.length, identities };
assert.equal(selected.filter, 'TestCategory=Browser');
assert.equal(selected.expected, identities.length);
assert.deepEqual(selected.identities, identities);
const browserTests = selected.filter;

const child = spawn(
  process.platform === 'win32' ? 'dotnet.exe' : 'dotnet',
  [
    'test',
    '--project',
    path.join('tests', 'Asap.Tests', 'Asap.Tests.csproj'),
    '--configuration',
    'Release',
    '--no-build',
    '--no-restore',
    '--minimum-expected-tests',
    String(selected.expected),
    '--filter',
    browserTests,
    ...(process.env.ASAP_CI_BROWSER_RESULTS ? [
      '--report-trx',
      '--report-trx-filename',
      path.basename(process.env.ASAP_CI_BROWSER_RESULTS),
      '--results-directory',
      path.dirname(process.env.ASAP_CI_BROWSER_RESULTS),
    ] : []),
  ],
  { cwd: repositoryRoot, stdio: 'inherit' },
);

child.once('error', (error) => {
  console.error(`Unable to start browser acceptance tests: ${error.message}`);
  process.exitCode = 1;
});

child.once('close', (code) => {
  process.exitCode = code === null ? 1 : code;
});
