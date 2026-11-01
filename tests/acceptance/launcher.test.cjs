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
for (const name of ['revision', 'snapshot', 'native', 'artifacts', 'selection']) {
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
$script:actualResolveCommit = \${function:Resolve-ApplicationCommit}
$script:containerCases = @{}
function wslc { throw 'Mock WSLC must go through Invoke-ReviewNative.' }
function Resolve-ApplicationCommit {
    param([string]$Repository, [int]$PullRequest, [string]$Revision)
    @{ arguments = @('resolve', $PullRequest, $Revision) } | ConvertTo-Json -Compress | Add-Content -LiteralPath $env:ASAP_LAUNCHER_TEST_CALLS
    return & $script:actualResolveCommit @PSBoundParameters
}
function Copy-ReviewTooling {
    param([string]$Repository, [string]$Destination)
    $null = [System.IO.Directory]::CreateDirectory((Join-Path $Destination 'dev/review'))
    'fixture' | Set-Content -LiteralPath (Join-Path $Destination 'dev/review/VERSION')
}
function Invoke-ReviewNative {
    param([string]$File, [string[]]$Arguments, [switch]$Capture, [switch]$AllowFailure, [int]$TimeoutSeconds = 60, [string]$LogPath)
    if ($File -ne 'wslc') { return & $script:actualReviewNative @PSBoundParameters }
    @{ arguments = @($Arguments) } | ConvertTo-Json -Compress | Add-Content -LiteralPath $env:ASAP_LAUNCHER_TEST_CALLS
    $case = $env:ASAP_LAUNCHER_TEST_CASE
    $code = 0; $output = ''; $errorText = ''
    switch ($Arguments[0]) {
        'version' { if ($case -eq 'prerequisite') { throw 'Synthetic WSLC unavailable' }; Write-Host 'Mock WSLC version' }
        'image' { if ($case -in @('build', 'uncached')) { $code = 1 } }
        'build' { if ($case -eq 'build') { $code = 1 }; $errorText = 'Synthetic image build diagnostic' }
        'run' {
            $selected = $Arguments[[Array]::IndexOf($Arguments, '--suite') + 1]
            $outcomes = $env:ASAP_LAUNCHER_TEST_OUTCOMES | ConvertFrom-Json -AsHashtable
            if ($outcomes.ContainsKey($selected)) { $case = $outcomes[$selected] }
            $name = $Arguments[[Array]::IndexOf($Arguments, '--name') + 1]
            $script:containerCases[$name] = $case
            if ($case -in @('startup', 'startup-absent')) { throw 'Synthetic container startup blocker' }
            $mount = $Arguments[[Array]::IndexOf($Arguments, '--mount') + 1]
            if ($mount -notmatch '^type=bind,source=(.+),target=/artifacts$') { throw 'Invalid artifact mount' }
            $directory = $Matches[1]
            $report = Get-Content -LiteralPath (Join-Path $directory 'results.json') -Raw | ConvertFrom-Json -AsHashtable
            $status = switch ($case) { 'failed' { 'FAIL' }; 'failed-cleanup' { 'FAIL' }; 'blocked' { 'BLOCKED' }; default { 'PASS' } }
            $report.environment = @{ status = if ($case -eq 'blocked') { 'BLOCKED' } else { 'PASS' } }
            $report.authentication = @{ status = 'PASS'; mode = 'local' }
            $report.applicationCommit = $report.expectedApplicationCommit
            $report.requiredScenarioIds = @('fixture-first', 'fixture-second')
            $report.scenarios = @(
                @{ id = 'fixture-first'; category = 'functional'; status = $status },
                @{ id = 'fixture-second'; category = 'functional'; status = 'PASS' })
            $partial = $case -eq 'skipped' -or ($Arguments -contains '--read-only' -and $selected -in @('smoke', 'd1', 'pr410'))
            if ($partial) { $report.scenarios[1].status = 'NOT RUN'; $status = 'INCOMPLETE' }
            $report.automatedFunctionalAcceptance = $status
            $report.actualExitCode = if ($partial) { 2 } else { switch ($case) { 'failed' { 1 }; 'failed-cleanup' { 1 }; 'blocked' { 2 }; 'mismatch' { 1 }; default { 0 } } }
            $script:containerCases[$name] = if ($partial) { 'skipped' } else { $case }
            if ($case -eq 'falsepass') { $report.scenarios[1].status = 'NOT RUN' }
            if ($case -eq 'wrongsha') { $report.applicationCommit = 'a' * 40 }
            if ($case -eq 'wrongtooling') { $report.toolingCommit = 'b' * 40 }
            if ($case -eq 'duplicates') { $report.scenarios[1].id = 'fixture-first' }
            $report | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $directory 'results.json')
            "Synthetic automated: $status" | Set-Content -LiteralPath (Join-Path $directory 'report.md')
            'synthetic screenshot' | Set-Content -LiteralPath (Join-Path $directory 'fixture-first.png')
            '{}' | Set-Content -LiteralPath (Join-Path $directory 'fixture-geometry.json')
            $null = [System.IO.Directory]::CreateDirectory((Join-Path $directory 'diagnostics'))
            'synthetic diagnostics' | Set-Content -LiteralPath (Join-Path $directory 'diagnostics/web.log')
            if ($status -ne 'PASS') { '{}' | Set-Content -LiteralPath (Join-Path $directory 'fixture-first-failure-trace.json') }
            if ($case -eq 'missingreport') { Remove-Item -LiteralPath (Join-Path $directory 'results.json') }
            if ($case -eq 'malformed') { '{unfinished' | Set-Content -LiteralPath (Join-Path $directory 'results.json') }
        }
        'inspect' {
            $case = $script:containerCases[$Arguments[-1]]
            $exit = switch ($case) { 'failed' { 1 }; 'failed-cleanup' { 1 }; 'blocked' { 2 }; 'skipped' { 2 }; default { 0 } }
            if ($case -eq 'interrupt') { 'ready' | Set-Content -LiteralPath $env:ASAP_LAUNCHER_TEST_READY }
            $output = @{ State = @{ Running = $case -eq 'interrupt'; ExitCode = $exit } } | ConvertTo-Json -Compress
        }
        'logs' { $output = 'Synthetic container log' }
        'remove' { if ($script:containerCases[$Arguments[-1]] -in @('cleanup', 'failed-cleanup', 'startup-absent')) { throw 'Synthetic cleanup failure' } }
        'list' {
            $name = $Arguments[[Array]::IndexOf($Arguments, '--filter') + 1] -replace '^name=', ''
            if ($script:containerCases[$name] -ne 'startup-absent') { $output = 'still-existing-container-id' }
        }
        'stop' { }
        default { throw 'Unexpected mock WSLC call' }
    }
    if ($LogPath) { ($output + $errorText) | Set-Content -LiteralPath $LogPath }
    if ($Capture) { return [pscustomobject]@{ Code = $code; Output = $output; ErrorText = $errorText } }
}
`);
  const suites = path.join(repository, 'tests/acceptance/suites');
  await fs.mkdir(suites, { recursive: true });
  await fs.writeFile(path.join(suites, 'registry.json'), '["d1","pr410","q1","s1","smoke"]');
  const git = (...args) => execute('git', ['-C', repository, ...args], { windowsHide: true });
  await git('init');
  await git('add', '.');
  await git('-c', 'user.name=Launcher tests', '-c', 'user.email=test@example.org',
    '-c', 'commit.gpgsign=false', 'commit', '-m', 'Synthetic launcher fixture');
  await git('remote', 'add', 'origin', repository);
  await git('update-ref', 'refs/pull/415/head', 'HEAD');
  let count = 0;
  return { directory, repository, git, async launch({ from = repository, customRoot, mode = 'success',
    selection, selector = '-Commit HEAD', flags = '', outcomes = {}, interrupt = false } = {}) {
    const callsFile = path.join(directory, 'calls-' + (++count) + '.jsonl');
    const ready = path.join(directory, 'ready-' + count);
    const quote = value => "'" + value.replace(/'/g, "''") + "'";
    let command = '& ' + quote(path.join(from, 'scripts/review/Invoke-AsapReview.ps1')) + ' ' + selector +
      (customRoot === undefined ? '' : ' -ArtifactsRoot ' + quote(customRoot)) +
      (selection === undefined ? '' : ' -Suite ' + selection) + ' ' + flags;
    if (interrupt) {
      command = `$pipeline = [powershell]::Create(); $null = $pipeline.AddScript(${quote(command)}); $pending = $pipeline.BeginInvoke();
$deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
while (-not (Test-Path -LiteralPath ${quote(ready)})) {
    if ($pending.IsCompleted -or [DateTimeOffset]::UtcNow -ge $deadline) { throw 'Interrupted fixture did not reach inspection' }
    Start-Sleep -Milliseconds 25
}
$pipeline.Stop(); try { $null = $pipeline.EndInvoke($pending) } catch { }
$pipeline.Streams.Information | ForEach-Object { Write-Host $_.MessageData }
$pipeline.Dispose(); exit 2`;
    } else command += '; exit $LASTEXITCODE';
    let outcome;
    try {
      outcome = await execute('pwsh', ['-NoProfile', '-Command', command], {
        cwd: from, timeout: 30000, windowsHide: true,
        env: { ...process.env, ASAP_LAUNCHER_TEST_CASE: mode, ASAP_LAUNCHER_TEST_CALLS: callsFile,
          ASAP_LAUNCHER_TEST_OUTCOMES: JSON.stringify(outcomes), ASAP_LAUNCHER_TEST_READY: ready }
      });
      outcome.code = 0;
    } catch (error) { outcome = error; }
    const artifactPath = outcome.stdout?.match(/^Artifacts:\s+(.+)\r?$/m)?.[1].trim();
    const calls = existsSync(callsFile) ? (await fs.readFile(callsFile, 'utf8')).trim().split(/\r?\n/).map(JSON.parse) : [];
    const log = outcome.stdout?.match(/^Shared diagnostics:\s+(.+)\r?$/m)?.[1].trim();
    const summary = log && existsSync(log.replace(/\.log$/, '.json')) ? JSON.parse(await fs.readFile(log.replace(/\.log$/, '.json'), 'utf8')) : null;
    return { ...outcome, artifactPath, calls, summary, log };
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
    assert.match(path.basename(outcome.artifactPath), /^commit-[a-f0-9]{12}_smoke_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}(-\d{2,})?$/);
    // Remove only this test's newly created synthetic output, never historical evidence.
    t.after(() => fs.rm(outcome.artifactPath, { recursive: true, force: true }));
    t.after(() => Promise.all([outcome.log, outcome.log.replace(/\.log$/, '.json')].map(file => fs.rm(file, { force: true }))));
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
    assert.ok(outcome.stdout.indexOf(outcome.artifactPath) < outcome.stdout.indexOf('Report:'));
    assert.ok(outcome.stdout.includes('Report: ' + path.join(outcome.artifactPath, 'report.md')));
    assert.match(outcome.stdout, /Overall: PASS; exit 0/);
    for (const line of ['Passed: 1', 'Failed: 0', 'Blocked: 0', 'Not run: 0']) assert.ok(outcome.stdout.includes(line));
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
    assert.match(outcome.stdout, /Overall: BLOCKED; exit 2/);
    assert.doesNotMatch(outcome.stdout, /^Report:/m);
    assert.deepEqual(outcome.calls, []);
  }
  assert.equal(await fs.readFile(file, 'utf8'), 'unrelated');
});

test('failed, blocked, startup, mismatch and cleanup outcomes retain the correct exports and exit codes', async t => {
  const f = await fixture(t);
  const customRoot = path.join(f.directory, 'retained evidence');
  for (const mode of ['failed', 'blocked', 'startup', 'mismatch', 'cleanup']) {
    const outcome = await f.launch({ customRoot, mode });
    const expectedCode = mode === 'failed' ? 1 : 2;
    assert.equal(outcome.code, expectedCode, outcome.stdout + outcome.stderr);
    assert.equal(path.dirname(outcome.artifactPath), customRoot);
    const report = JSON.parse(await fs.readFile(path.join(outcome.artifactPath, 'results.json'), 'utf8'));
    assert.equal(report.actualExitCode, expectedCode);
    assert.equal(report.automatedFunctionalAcceptance, mode === 'failed' ? 'FAIL' : 'BLOCKED');
    assert.ok(outcome.stdout.includes('Report: ' + path.join(outcome.artifactPath, 'report.md')));
    assert.equal(outcome.summary.suites[0].Status, mode === 'failed' ? 'FAIL' : 'BLOCKED');
    {
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
  assert.equal((await fs.readdir(customRoot, { withFileTypes: true })).filter(entry => entry.isDirectory()).length, 5, 'Cleanup retained every started run');
});

test('native array invocations share one revision/image preparation and isolate each sequential suite', async t => {
  const f = await fixture(t), customRoot = path.join(f.directory, 'multi suite');
  for (const [selection, mode, flags] of [
    ['s1,d1,q1', 'uncached', ''], ["@('s1', 'd1', 'q1')", 'success', ''], ['s1,d1,q1', 'success', '-Rebuild']
  ]) {
    const outcome = await f.launch({ customRoot, selection, mode, flags, selector: '-Pr 415' });
    assert.equal(outcome.code, 0, outcome.stdout + outcome.stderr);
    const requested = ['s1', 'd1', 'q1'];
    assert.deepEqual(outcome.summary.suites.map(s => s.Suite), requested);
    assert.deepEqual(outcome.summary.suites.map(s => s.Status), ['PASS', 'PASS', 'PASS']);
    assert.deepEqual(outcome.summary.counts, { PASS: 3, FAIL: 0, BLOCKED: 0, 'NOT RUN': 0 });
    const calls = outcome.calls.map(c => c.arguments);
    assert.equal(calls.filter(c => c[0] === 'resolve').length, 1);
    assert.equal(calls.filter(c => c[0] === 'image').length, 1);
    const builds = calls.filter(c => c[0] === 'build');
    assert.equal(builds.length, mode === 'uncached' || flags ? 1 : 0);
    if (flags) assert.equal(builds[0].filter(arg => arg === '--no-cache').length, 1);
    const runs = calls.filter(c => c[0] === 'run');
    assert.deepEqual(runs.map(c => c[c.indexOf('--suite') + 1]), requested);
    assert.equal(new Set(runs.map(c => c[c.indexOf('--name') + 1])).size, 3);
    assert.equal(new Set(runs.map(c => c[c.indexOf('--expected-commit') + 1])).size, 1);
    assert.equal(new Set(runs.map(c => c[c.indexOf('--suite') - 1])).size, 1, 'One immutable image for every suite');
    assert.deepEqual(calls.filter(c => ['run', 'stop', 'logs', 'remove'].includes(c[0])).map(c => c[0]),
      requested.flatMap(() => ['run', 'stop', 'logs', 'remove']), 'Cleanup completes before starting the next fresh runtime');
    assert.equal(new Set(outcome.summary.suites.map(s => s.ArtifactPath)).size, 3);
    const reports = [];
    for (const suite of outcome.summary.suites) {
      assert.equal(path.dirname(suite.ArtifactPath), customRoot);
      assert.match(path.basename(suite.ArtifactPath), new RegExp('^pr-415_' + suite.Suite + '_\\d{4}-\\d{2}-\\d{2}_\\d{2}-\\d{2}-\\d{2}(-\\d{2,})?$'));
      const report = JSON.parse(await fs.readFile(path.join(suite.ArtifactPath, 'results.json'), 'utf8'));
      reports.push(report);
      assert.match(report.startedLocal, /[+-]\d{2}:\d{2}$/);
      assert.equal(new Date(report.startedLocal).getTime(), new Date(report.startedUtc).getTime());
      const local = new Date(report.startedLocal);
      const pad = v => String(v).padStart(2, '0');
      const stamp = `${local.getFullYear()}-${pad(local.getMonth() + 1)}-${pad(local.getDate())}_${pad(local.getHours())}-${pad(local.getMinutes())}-${pad(local.getSeconds())}`;
      assert.ok(path.basename(suite.ArtifactPath).startsWith('pr-415_' + suite.Suite + '_' + stamp));
      assert.equal(report.mode, 'mutating');
      assert.match(report.runId, /^[a-f0-9]{24}$/);
      assert.match(report.toolingDigest, /^[a-f0-9]{64}$/);
      assert.equal(report.toolingVersion, 'fixture');
      assert.equal(report.overallAcceptance, 'INCOMPLETE');
      assert.ok(suite.durationSeconds > 0);
      assert.equal(existsSync(path.join(suite.ArtifactPath, 'container.log')), true);
      const run = runs[requested.indexOf(suite.Suite)];
      assert.equal(run.filter(arg => arg === '--mount').length, 1);
      assert.equal(run[run.indexOf('--mount') + 1], 'type=bind,source=' + suite.ArtifactPath + ',target=/artifacts');
      assert.ok(!run.some(arg => ['--publish', '--volume', '-v', '-p', '--allow-review-writes', '--read-only'].includes(arg)));
    }
    for (const key of ['expectedApplicationCommit', 'applicationCommit', 'toolingCommit', 'toolingDigest', 'acceptanceImage']) {
      assert.equal(new Set(reports.map(report => report[key])).size, 1, key + ' is pinned for the invocation');
    }
    assert.ok(new Date(reports[1].startedUtc) > new Date(reports[0].startedUtc), 'Each suite captures its own start timestamp');
  }
});

test('default smoke, explicit single suites, HEAD/commit naming and write-mode compatibility', async t => {
  const f = await fixture(t), customRoot = path.join(f.directory, 'mode exports');
  for (const [selection, flags, selector, expected, status] of [
    [undefined, '', '', 'smoke', 'PASS'], ['s1', '', '-Commit HEAD', 's1', 'PASS'],
    ['smoke', '-AllowReviewWrites', '-Commit HEAD', 'smoke', 'PASS'],
    ['smoke', '-ReadOnly', '-Commit HEAD', 'smoke', 'BLOCKED'], ['s1', '-ReadOnly', '', 's1', 'PASS']
  ]) {
    const outcome = await f.launch({ customRoot, selection, flags, selector });
    assert.equal(outcome.code, status === 'PASS' ? 0 : 2, outcome.stdout + outcome.stderr);
    assert.deepEqual(outcome.summary.suites.map(s => [s.Suite, s.Status]), [[expected, status]]);
    assert.match(path.basename(outcome.artifactPath), new RegExp('^commit-[a-f0-9]{12}_' + expected + '_'));
    const run = outcome.calls.find(c => c.arguments[0] === 'run').arguments;
    assert.equal(run.includes('--read-only'), flags === '-ReadOnly');
    if (flags === '-AllowReviewWrites') assert.match(outcome.stdout, /deprecated/);
    const report = JSON.parse(await fs.readFile(path.join(outcome.artifactPath, 'results.json'), 'utf8'));
    assert.equal(report.mode, flags === '-ReadOnly' ? 'read-only' : 'mutating');
    if (status === 'BLOCKED') assert.ok(report.scenarios.some(s => s.status === 'NOT RUN'));
  }
});

test('complete selection validation rejects empty, unsafe, duplicate and unavailable suites before execution', async t => {
  const f = await fixture(t), customRoot = path.join(f.directory, 'invalid exports');
  for (const selection of ["''", '@()', '$null', "@('s1','')", 's1,S1', 's1,unknown', "'s1,d1'", "'../s1'"]) {
    const outcome = await f.launch({ customRoot, selection });
    assert.equal(outcome.code, 2, outcome.stdout + outcome.stderr);
    assert.match(outcome.stdout, /Available suites: d1, pr410, q1, s1, smoke/);
    assert.match(outcome.stdout, /local tooling checkout.*updating/);
    assert.deepEqual(outcome.calls, [], 'No resolution, WSLC call or execution before complete validation');
  }
  assert.equal(existsSync(customRoot), false);
});

test('functional and suite-local infrastructure failures continue without hiding classifications or evidence', async t => {
  const f = await fixture(t), customRoot = path.join(f.directory, 'failure exports');
  for (const [outcomes, statuses, code] of [
    [{ s1: 'failed' }, ['FAIL', 'PASS', 'PASS'], 1],
    [{ d1: 'failed' }, ['PASS', 'FAIL', 'PASS'], 1],
    [{ s1: 'startup' }, ['BLOCKED', 'PASS', 'PASS'], 2],
    [{ s1: 'startup-absent' }, ['BLOCKED', 'PASS', 'PASS'], 2],
    [{ d1: 'blocked' }, ['PASS', 'BLOCKED', 'PASS'], 2],
    [{ s1: 'failed', d1: 'blocked' }, ['FAIL', 'BLOCKED', 'PASS'], 2],
    [{ s1: 'falsepass', d1: 'duplicates' }, ['BLOCKED', 'BLOCKED', 'PASS'], 2],
    [{ s1: 'missingreport', d1: 'malformed' }, ['BLOCKED', 'BLOCKED', 'PASS'], 2],
    [{ s1: 'wrongsha', d1: 'wrongtooling' }, ['BLOCKED', 'BLOCKED', 'PASS'], 2]
  ]) {
    const outcome = await f.launch({ customRoot, selection: 's1,d1,q1', outcomes });
    assert.equal(outcome.code, code, outcome.stdout + outcome.stderr);
    assert.deepEqual(outcome.summary.suites.map(s => s.Status), statuses);
    assert.equal(outcome.calls.filter(c => c.arguments[0] === 'run').length, 3);
    assert.equal(outcome.calls.filter(c => c.arguments[0] === 'remove').length, 3);
    for (const [index, suite] of outcome.summary.suites.entries()) {
      assert.equal(existsSync(path.join(suite.ArtifactPath, 'container.log')), true);
      const report = JSON.parse(await fs.readFile(path.join(suite.ArtifactPath, 'results.json'), 'utf8'));
      assert.equal(report.launcherStatus, statuses[index]);
      if (statuses[index] === 'FAIL') {
        assert.equal(report.automatedFunctionalAcceptance, 'FAIL');
        assert.ok(report.scenarios.some(s => s.status === 'FAIL'));
        assert.equal(existsSync(path.join(suite.ArtifactPath, 'fixture-first-failure-trace.json')), true);
      }
    }
  }
});

test('fatal shared prerequisites/build retain diagnostics and mark every requested suite NOT RUN', async t => {
  const f = await fixture(t), customRoot = path.join(f.directory, 'shared failure');
  for (const mode of ['prerequisite', 'build']) {
    const outcome = await f.launch({ customRoot, selection: 's1,d1,q1', mode });
    assert.equal(outcome.code, 2, outcome.stdout + outcome.stderr);
    assert.deepEqual(outcome.summary.suites.map(s => s.Status), ['NOT RUN', 'NOT RUN', 'NOT RUN']);
    assert.ok(outcome.summary.suites.every(s => s.ArtifactPath === null));
    assert.ok(!outcome.calls.some(c => ['run', 'remove'].includes(c.arguments[0])));
    assert.match(await fs.readFile(outcome.log, 'utf8'), /BLOCKED:/);
    if (mode === 'build') {
      const build = outcome.calls.find(c => c.arguments[0] === 'build');
      assert.equal(existsSync(build.arguments.at(-1)), false, 'Failed build context is removed');
      const log = outcome.stdout.match(/Diagnostics: (.+)\r?$/m)[1].trim();
      assert.match(await fs.readFile(log, 'utf8'), /Synthetic image build diagnostic/);
    }
  }
});

test('cleanup failure stops later suites, retains earlier functional failures, and gives explicit recovery', async t => {
  const f = await fixture(t);
  const outcome = await f.launch({ customRoot: path.join(f.directory, 'cleanup failure'), selection: 's1,d1,q1',
    outcomes: { s1: 'failed', d1: 'cleanup' } });
  assert.equal(outcome.code, 2, outcome.stdout + outcome.stderr);
  assert.deepEqual(outcome.summary.suites.map(s => s.Status), ['FAIL', 'BLOCKED', 'NOT RUN']);
  assert.match(outcome.stdout, /Container cleanup failed. Run: wslc remove --force asap-acceptance-/);
  assert.equal(outcome.calls.filter(c => c.arguments[0] === 'run').length, 2);
  assert.equal(outcome.calls.filter(c => c.arguments[0] === 'remove').length, 2);
});

test('a functional FAIL and cleanup blocker in the same suite preserve both in retained evidence', async t => {
  const f = await fixture(t);
  const outcome = await f.launch({ customRoot: path.join(f.directory, 'failed cleanup'), selection: 's1,d1', outcomes: { s1: 'failed-cleanup' } });
  assert.equal(outcome.code, 2, outcome.stdout + outcome.stderr);
  assert.deepEqual(outcome.summary.suites.map(s => s.Status), ['BLOCKED', 'NOT RUN']);
  const report = JSON.parse(await fs.readFile(path.join(outcome.artifactPath, 'results.json'), 'utf8'));
  assert.deepEqual(report.scenarios.map(s => s.status), ['FAIL', 'PASS', 'BLOCKED']);
  assert.equal(existsSync(path.join(outcome.artifactPath, 'fixture-first-failure-trace.json')), true);
});

test('pipeline interruption still exports logs, removes the active container and leaves later suites NOT RUN', async t => {
  const f = await fixture(t), customRoot = path.join(f.directory, 'interrupted exports');
  const outcome = await f.launch({ customRoot, selection: 's1,d1,q1', outcomes: { s1: 'interrupt' }, interrupt: true });
  assert.equal(outcome.code, 2, outcome.stdout + outcome.stderr);
  assert.deepEqual(outcome.summary?.suites.map(s => s.Status), ['BLOCKED', 'NOT RUN', 'NOT RUN'], outcome.stdout + outcome.stderr);
  assert.deepEqual(outcome.calls.filter(c => ['run', 'stop', 'logs', 'remove'].includes(c.arguments[0])).map(c => c.arguments[0]),
    ['run', 'stop', 'logs', 'remove']);
  const directory = outcome.summary.suites[0].ArtifactPath;
  const report = JSON.parse(await fs.readFile(path.join(directory, 'results.json'), 'utf8'));
  assert.equal(report.actualExitCode, 2);
  assert.match(report.environment.detail, /interrupted/);
  for (const name of ['container.log', 'fixture-first.png', 'diagnostics/web.log']) assert.equal(existsSync(path.join(directory, name)), true);
});

test('atomic allocation handles simultaneous identical timestamps without modifying existing evidence', async t => {
  const directory = await temporary(t), exports = path.join(directory, 'race exports');
  const helper = path.join(root, 'scripts/review/Review-Environment.ps1').replace(/'/g, "''");
  const command = `. '${helper}'; New-ReviewArtifactDirectory -ArtifactsRoot '${exports.replace(/'/g, "''")}' ` +
    `-PullRequest 415 -ApplicationCommit '${'a'.repeat(40)}' -Suite s1 -Timestamp '2026-11-01T13:30:00+00:00'`;
  const first = (await execute('pwsh', ['-NoProfile', '-Command', command], { windowsHide: true })).stdout.trim();
  await fs.writeFile(path.join(first, 'keep.txt'), 'existing evidence');
  const results = await Promise.all(Array.from({ length: 6 }, () => execute('pwsh', ['-NoProfile', '-Command', command], { windowsHide: true })));
  const names = [first, ...results.map(r => r.stdout.trim())].map(p => path.basename(p)).sort();
  const base = path.basename(first);
  assert.deepEqual(names, [base, ...Array.from({ length: 6 }, (_, i) => base + '-' + String(i + 2).padStart(2, '0'))]);
  assert.equal(await fs.readFile(path.join(first, 'keep.txt'), 'utf8'), 'existing evidence');
  assert.equal((await fs.readdir(exports)).some(name => name.startsWith('.allocate-')), false);
});
