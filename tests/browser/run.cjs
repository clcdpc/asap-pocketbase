'use strict';

const { spawn } = require('node:child_process');
const path = require('node:path');

const repositoryRoot = path.resolve(__dirname, '..', '..');
const fs = require('node:fs');
const selected = process.env.ASAP_CI_TEST_SELECTION
  ? JSON.parse(fs.readFileSync(process.env.ASAP_CI_TEST_SELECTION, 'utf8')).suites.browser
  : { filter: 'TestCategory=Browser', expected: 17 };
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
