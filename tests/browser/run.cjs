'use strict';

const { spawn } = require('node:child_process');
const path = require('node:path');

const repositoryRoot = path.resolve(__dirname, '..', '..');
const browserTests = [
  'FullyQualifiedName~PatronBrowserJourneyRunsOnKestrelWithRealSqlAndRecordingEmailTransport',
  'FullyQualifiedName~StaffBrowserJourneyRunsOnKestrelWithRealSqlScopeAndRecoveryBarriers',
  'FullyQualifiedName~LegacyRequestLinksReplaceDesktopAndMobileUrlsWithoutLeakingFailures',
].join('|');

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
    '3',
    '--filter',
    browserTests,
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
