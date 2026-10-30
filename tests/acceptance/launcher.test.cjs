'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { existsSync } = require('node:fs');
const fs = require('node:fs/promises');
const { execFile } = require('node:child_process');
const { promisify } = require('node:util');
const path = require('node:path');
const os = require('node:os');
const execute = promisify(execFile);
const root = path.resolve(__dirname, '../..');
for (const name of ['revision', 'snapshot', 'native', 'artifacts']) {
  test('actual PowerShell launcher helpers: ' + name, async () => {
    const output = await execute('pwsh', ['-NoProfile', '-File', path.join(__dirname, 'launcher.test.ps1'), '-Case', name],
      { cwd: root, timeout: 30000, windowsHide: true });
    assert.match(output.stdout, new RegExp('PASS: ' + name));
  });
}
test('public launcher rejects invalid revision/suite/write combinations with exit 2', async t => {
  const artifacts = await temporary(t);
  for (const args of [['-Pr', '410', '-Commit', 'HEAD'], ['-Suite', 'unknown'],
    ['-Suite', 'smoke', '-ReadOnly', '-AllowReviewWrites'], ['-Commit:--all']]) {
    await assert.rejects(execute('pwsh', ['-NoProfile', '-File', path.join(root, 'scripts/review/Invoke-AsapReview.ps1'), '-ArtifactsRoot', artifacts, ...args],
      { cwd: root, timeout: 15000, windowsHide: true }), error => error.code === 2 && /BLOCKED:/.test(error.stdout));
  }
});
test('PowerShell validates the named suite without host Node or Playwright', async t => {
  const artifacts = (await temporary(t)).replace(/'/g, "''");
  const env = { ...process.env };
  const pathKey = Object.keys(env).find(key => key.toLowerCase() === 'path');
  env[pathKey] = env[pathKey].split(path.delimiter).filter(directory =>
    !['node', 'node.exe'].some(name => existsSync(path.join(directory, name)))).join(path.delimiter);
  const launcher = path.join(root, 'scripts/review/Invoke-AsapReview.ps1').replace(/'/g, "''");
  await assert.rejects(execute('pwsh', ['-NoProfile', '-Command',
    "if (Get-Command node -ErrorAction SilentlyContinue) { throw 'Host Node is still available' }; & '" + launcher +
    "' -Suite unknown -ArtifactsRoot '" + artifacts + "'; exit $LASTEXITCODE"], { cwd: root, env, timeout: 15000, windowsHide: true }),
  error => error.code === 2 && /Unknown suite/.test(error.stdout));
});

async function temporary(t) {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'asap-launcher-export-'));
  t.after(() => fs.rm(directory, { recursive: true, force: true }));
  return directory;
}

// Exercise the public launcher, real Git revision handling and real filesystem.
// Only WSLC and the expensive tooling snapshot are replaced in disposable repos.
async function fixture(t) {
  const directory = await temporary(t);
  const repository = path.join(directory, 'source worktree');
  const scripts = path.join(repository, 'scripts/review');
  await fs.mkdir(scripts, { recursive: true });
  await fs.copyFile(path.join(root, 'scripts/review/Invoke-AsapReview.ps1'), path.join(scripts, 'Invoke-AsapReview.ps1'));
  const helpers = path.join(root, 'scripts/review/Review-Environment.ps1').replace(/'/g, "''");
  await fs.writeFile(path.join(scripts, 'Review-Environment.ps1'), `
. '${helpers}'
$script:actualReviewNative = \${function:Invoke-ReviewNative}
function wslc { throw 'Mock WSLC must go through Invoke-ReviewNative.' }
function Copy-ReviewTooling {
    param([string]$Repository, [string]$Destination)
    $null = [System.IO.Directory]::CreateDirectory((Join-Path $Destination 'dev/review'))
    'fixture' | Set-Content -LiteralPath (Join-Path $Destination 'dev/review/VERSION')
}
function Invoke-ReviewNative {
    param([string]$File, [string[]]$Arguments, [switch]$Capture, [switch]$AllowFailure, [int]$TimeoutSeconds = 60)
    if ($File -ne 'wslc') { return & $script:actualReviewNative @PSBoundParameters }
    @{ arguments = @($Arguments) } | ConvertTo-Json -Compress | Add-Content -LiteralPath $env:ASAP_LAUNCHER_TEST_CALLS
    $case = $env:ASAP_LAUNCHER_TEST_CASE
    $code = 0; $output = ''; $errorText = ''
    switch ($Arguments[0]) {
        'version' { Write-Host 'Mock WSLC version' }
        'image' { if ($case -eq 'build') { $code = 1 } }
        'build' { $code = 1; $errorText = 'Synthetic image build diagnostic' }
        'run' {
            if ($case -eq 'startup') { throw 'Synthetic container startup blocker' }
            $mount = $Arguments[[Array]::IndexOf($Arguments, '--mount') + 1]
            if ($mount -notmatch '^type=bind,source=(.+),target=/artifacts$') { throw 'Invalid artifact mount' }
            $directory = $Matches[1]
            $report = Get-Content -LiteralPath (Join-Path $directory 'results.json') -Raw | ConvertFrom-Json -AsHashtable
            $status = switch ($case) { 'failed' { 'FAIL' }; 'blocked' { 'BLOCKED' }; default { 'PASS' } }
            $report.environment = @{ status = if ($case -eq 'blocked') { 'BLOCKED' } else { 'PASS' } }
            $report.authentication = @{ status = 'PASS'; mode = 'local' }
            $report.applicationCommit = $report.expectedApplicationCommit
            $report.requiredScenarioIds = @('fixture-first', 'fixture-second')
            $report.scenarios = @(
                @{ id = 'fixture-first'; category = 'functional'; status = $status },
                @{ id = 'fixture-second'; category = 'functional'; status = if ($status -eq 'PASS') { 'PASS' } else { 'NOT RUN' } })
            $report.automatedFunctionalAcceptance = $status
            $report.actualExitCode = switch ($case) { 'failed' { 1 }; 'blocked' { 2 }; 'mismatch' { 1 }; default { 0 } }
            $report | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $directory 'results.json')
            "Synthetic automated: $status" | Set-Content -LiteralPath (Join-Path $directory 'report.md')
            'synthetic screenshot' | Set-Content -LiteralPath (Join-Path $directory 'fixture-first.png')
            '{}' | Set-Content -LiteralPath (Join-Path $directory 'fixture-geometry.json')
            $null = [System.IO.Directory]::CreateDirectory((Join-Path $directory 'diagnostics'))
            'synthetic diagnostics' | Set-Content -LiteralPath (Join-Path $directory 'diagnostics/web.log')
            if ($status -ne 'PASS') { '{}' | Set-Content -LiteralPath (Join-Path $directory 'fixture-first-failure-trace.json') }
        }
        'inspect' {
            $exit = switch ($case) { 'failed' { 1 }; 'blocked' { 2 }; default { 0 } }
            $output = @{ State = @{ Running = $false; ExitCode = $exit } } | ConvertTo-Json -Compress
        }
        'logs' { $output = 'Synthetic container log' }
        'remove' { if ($case -eq 'cleanup') { throw 'Synthetic cleanup failure' } }
        'stop' { }
        default { throw 'Unexpected mock WSLC call' }
    }
    if ($Capture) { return [pscustomobject]@{ Code = $code; Output = $output; ErrorText = $errorText } }
}
`);
  const suites = path.join(repository, 'tests/acceptance/suites');
  await fs.mkdir(suites, { recursive: true });
  await fs.writeFile(path.join(suites, 'registry.json'), '["smoke"]');
  const git = (...args) => execute('git', ['-C', repository, ...args], { windowsHide: true });
  await git('init');
  await git('add', '.');
  await git('-c', 'user.name=Launcher tests', '-c', 'user.email=test@example.org',
    '-c', 'commit.gpgsign=false', 'commit', '-m', 'Synthetic launcher fixture');
  let count = 0;
  return { directory, repository, git, async launch({ from = repository, customRoot, mode = 'success' } = {}) {
    const callsFile = path.join(directory, 'calls-' + (++count) + '.jsonl');
    let outcome;
    try {
      outcome = await execute('pwsh', ['-NoProfile', '-File', path.join(from, 'scripts/review/Invoke-AsapReview.ps1'),
        '-Commit', 'HEAD', ...(customRoot === undefined ? [] : ['-ArtifactsRoot', customRoot])], {
        cwd: from, timeout: 30000, windowsHide: true,
        env: { ...process.env, ASAP_LAUNCHER_TEST_CASE: mode, ASAP_LAUNCHER_TEST_CALLS: callsFile }
      });
      outcome.code = 0;
    } catch (error) { outcome = error; }
    const artifactPath = outcome.stdout?.match(/^Artifacts:\s+(.+)\r?$/m)?.[1].trim();
    const calls = existsSync(callsFile) ? (await fs.readFile(callsFile, 'utf8')).trim().split(/\r?\n/).map(JSON.parse) : [];
    return { ...outcome, artifactPath, calls };
  } };
}

test('default exports share the profile root across worktrees, stay unique and survive worktree deletion', async t => {
  const f = await fixture(t);
  const profile = (await execute('pwsh', ['-NoProfile', '-Command', "[Environment]::GetFolderPath('UserProfile')"],
    { windowsHide: true })).stdout.trim();
  const expected = path.join(profile, 'ASAP-Acceptance');
  const secondary = path.join(f.directory, 'secondary worktree');
  await f.git('worktree', 'add', '--detach', secondary, 'HEAD');
  const first = await f.launch();
  const second = await f.launch({ from: secondary });
  for (const outcome of [first, second]) {
    assert.equal(outcome.code, 0, outcome.stdout + outcome.stderr);
    assert.equal(path.dirname(outcome.artifactPath), expected);
    assert.match(path.basename(outcome.artifactPath), /^\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}-[a-f0-9]{24}$/);
    // Remove only this test's newly created synthetic output, never historical evidence.
    t.after(() => fs.rm(outcome.artifactPath, { recursive: true, force: true }));
  }
  assert.notEqual(first.artifactPath, second.artifactPath);
  assert.equal(existsSync(path.join(f.repository, '.artifacts')), false);
  assert.equal(existsSync(path.join(secondary, '.artifacts')), false);
  assert.ok(path.resolve(secondary).startsWith(path.resolve(f.directory) + path.sep));
  await f.git('worktree', 'remove', '--force', secondary);
  for (const outcome of [first, second]) {
    assert.match(await fs.readFile(path.join(outcome.artifactPath, 'report.md'), 'utf8'), /PASS/);
    assert.equal(existsSync(path.join(outcome.artifactPath, 'fixture-first.png')), true);
  }
});

test('absolute custom roots with spaces are normalized and passed to exactly one artifact mount', async t => {
  const f = await fixture(t);
  const customRoot = path.join(f.directory, 'custom output with spaces');
  await fs.mkdir(customRoot);
  await fs.writeFile(path.join(customRoot, 'keep.txt'), 'unrelated');
  const first = await f.launch({ customRoot: customRoot + path.sep + '..' + path.sep + path.basename(customRoot) });
  assert.equal(first.code, 0, first.stdout + first.stderr);
  const names = ['report.md', 'results.json', 'fixture-first.png', 'fixture-geometry.json', 'diagnostics/web.log', 'container.log'];
  const original = await Promise.all(names.map(name => fs.readFile(path.join(first.artifactPath, name), 'utf8')));
  const second = await f.launch({ customRoot });
  for (const outcome of [first, second]) {
    assert.equal(outcome.code, 0, outcome.stdout + outcome.stderr);
    assert.equal(path.dirname(outcome.artifactPath), customRoot);
    const run = outcome.calls.find(call => call.arguments[0] === 'run').arguments;
    assert.equal(run.filter(arg => arg === '--mount').length, 1);
    assert.equal(run[run.indexOf('--mount') + 1], 'type=bind,source=' + outcome.artifactPath + ',target=/artifacts');
    assert.ok(!run.some(arg => ['--publish', '--volume', '-v', '-p'].includes(arg)));
    assert.ok(outcome.stdout.indexOf(outcome.artifactPath) < outcome.stdout.indexOf('Mock WSLC version'));
    assert.ok(outcome.stdout.includes('Report: ' + path.join(outcome.artifactPath, 'report.md')));
    assert.match(outcome.stdout, /Automated: PASS; exit 0/);
    for (const line of ['PASS: 2', 'FAIL: 0', 'BLOCKED: 0', 'NOT RUN: 0']) assert.ok(outcome.stdout.includes(line));
    assert.equal(outcome.calls.filter(call => call.arguments[0] === 'remove').length, 1);
  }
  assert.notEqual(first.artifactPath, second.artifactPath);
  assert.equal(await fs.readFile(path.join(customRoot, 'keep.txt'), 'utf8'), 'unrelated');
  assert.deepEqual(await Promise.all(names.map(name => fs.readFile(path.join(first.artifactPath, name), 'utf8'))), original,
    'Later runs and cleanup leave previous evidence byte-for-byte unchanged');
  for (const name of names) assert.equal(existsSync(path.join(second.artifactPath, name)), true);
  assert.deepEqual(await fs.readdir(path.join(f.repository, '.review/build')), []);
});

test('invalid or unusable custom roots block without fallback, mount or a claimed report', async t => {
  const f = await fixture(t);
  const file = path.join(f.directory, 'occupied.txt');
  await fs.writeFile(file, 'unrelated');
  for (const customRoot of ['', 'relative output', file]) {
    const outcome = await f.launch({ customRoot });
    assert.equal(outcome.code, 2, outcome.stdout + outcome.stderr);
    assert.match(outcome.stdout, /BLOCKED:.*(absolute filesystem path|Cannot create or write acceptance artifacts)/);
    assert.match(outcome.stdout, /Artifacts: unavailable/);
    assert.match(outcome.stdout, /Automated: BLOCKED; exit 2/);
    assert.doesNotMatch(outcome.stdout, /^Report:/m);
    assert.deepEqual(outcome.calls, []);
  }
  assert.equal(await fs.readFile(file, 'utf8'), 'unrelated');
});

test('failed, blocked, build, startup, mismatch and cleanup outcomes retain the correct exports and exit codes', async t => {
  const f = await fixture(t);
  const customRoot = path.join(f.directory, 'retained evidence');
  for (const mode of ['failed', 'blocked', 'build', 'startup', 'mismatch', 'cleanup']) {
    const outcome = await f.launch({ customRoot, mode });
    const expectedCode = mode === 'failed' ? 1 : 2;
    assert.equal(outcome.code, expectedCode, outcome.stdout + outcome.stderr);
    assert.equal(path.dirname(outcome.artifactPath), customRoot);
    const report = JSON.parse(await fs.readFile(path.join(outcome.artifactPath, 'results.json'), 'utf8'));
    assert.equal(report.actualExitCode, expectedCode);
    assert.equal(report.automatedFunctionalAcceptance, mode === 'failed' ? 'FAIL' : 'BLOCKED');
    assert.ok(outcome.stdout.includes('Report: ' + path.join(outcome.artifactPath, 'report.md')));
    assert.equal(outcome.stdout.split(outcome.artifactPath).length - 1, 3, 'Run path appears early, at completion and in the report path');
    for (const status of ['PASS', 'FAIL', 'BLOCKED', 'NOT RUN']) {
      assert.ok(outcome.stdout.includes(status + ': ' + report.scenarios.filter(s => s.category === 'functional' && s.status === status).length));
    }
    if (mode === 'build') {
      assert.match(await fs.readFile(path.join(outcome.artifactPath, 'build.log'), 'utf8'), /Synthetic image build diagnostic/);
      assert.ok(!outcome.calls.some(call => call.arguments[0] === 'run'));
    } else {
      assert.equal(existsSync(path.join(outcome.artifactPath, 'container.log')), true);
      assert.equal(outcome.calls.filter(call => call.arguments[0] === 'remove').length, 1);
    }
    if (['failed', 'blocked', 'mismatch', 'cleanup'].includes(mode)) {
      assert.equal(existsSync(path.join(outcome.artifactPath, 'fixture-first.png')), true);
      assert.equal(existsSync(path.join(outcome.artifactPath, 'diagnostics/web.log')), true);
    }
    if (['failed', 'blocked'].includes(mode)) assert.equal(existsSync(path.join(outcome.artifactPath, 'fixture-first-failure-trace.json')), true);
    if (mode === 'mismatch') assert.match(report.environment.detail, /exit and exported report disagree/);
    if (mode === 'cleanup') assert.match(report.environment.detail, /Container cleanup failed/);
    assert.deepEqual(await fs.readdir(path.join(f.repository, '.review/build')), []);
  }
  assert.equal((await fs.readdir(customRoot)).length, 6, 'Cleanup retained every run');
});
