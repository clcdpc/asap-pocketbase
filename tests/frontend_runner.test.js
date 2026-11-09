const assert = require('node:assert/strict');
const cp = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const vm = require('node:vm');
const { EventEmitter } = require('node:events');
const { PassThrough } = require('node:stream');
const source = fs.readFileSync(path.join(__dirname, 'run_all.js'), 'utf8');
const runner = require('./run_all.js');
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-frontend-deadline-'));
const pids = new Set();
const budget = { timeoutMs: 3000, graceMs: 250, killWaitMs: 1000, taskkillMs: 2000 };

function cleanup() {
  // Independent of runFile: even a broken deadline/close handler must not leak
  // the fixture's detached process groups when the fixture watchdog fires.
  for (const marker of fs.readdirSync(root).filter(name => name.endsWith('.pid'))) {
    pids.add(Number(fs.readFileSync(path.join(root, marker), 'utf8')));
  }
  for (const pid of pids) {
    if (process.platform === 'win32') {
      if (!alive(pid)) continue;
      cp.spawnSync('taskkill', ['/PID', String(pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore', timeout: 1500 });
    } else {
      try { process.kill(-pid, 'SIGKILL'); } catch {}
      try { process.kill(pid, 'SIGKILL'); } catch {}
    }
  }
  fs.rmSync(root, { recursive: true, force: true });
}
const watchdog = setTimeout(() => {
  try { cleanup(); } finally { console.error('Independent frontend fixture watchdog expired'); process.exit(1); }
}, 90000);
watchdog.unref();

function loadRunner(overrides = {}) {
  const context = { require: name => name === 'child_process' ? { ...cp, ...overrides.cp } : require(name),
    process: overrides.process || process, console, Buffer, AbortController, __dirname: root,
    module: { exports: {} }, setTimeout, clearTimeout, setInterval, clearInterval };
  vm.runInNewContext(source, context, { filename: 'run_all.js' });
  return context.module.exports;
}
async function until(predicate) {
  const deadline = Date.now() + 5000;
  while (!predicate()) {
    assert.ok(Date.now() < deadline, 'Fixture synchronization timed out');
    await new Promise(resolve => setTimeout(resolve, 15));
  }
}
function alive(pid) {
  try { process.kill(pid, 0); return true; }
  catch (error) { if (error.code === 'ESRCH') return false; throw error; }
}
function running(pid) {
  if (!alive(pid)) return false;
  if (process.platform === 'linux') {
    try { return !/^\d+ \(.*\) Z /.test(fs.readFileSync('/proc/' + pid + '/stat', 'utf8')); }
    catch (error) { if (error.code === 'ENOENT') return false; throw error; }
  }
  return true;
}
function fixture(name, body) {
  const file = path.join(root, name + '.js');
  fs.writeFileSync(file, body);
  return file;
}
function pidFixture(name, body, prepare = '') {
  const marker = path.join(root, name + '.pid');
  const file = fixture(name, prepare + "require('fs').writeFileSync(" + JSON.stringify(marker) + ", String(process.pid));" + body);
  return { file, marker };
}
function observe(marker) {
  const pid = Number(fs.readFileSync(marker, 'utf8'));
  pids.add(pid);
  return pid;
}
async function checked(file, options = {}, instance = runner) {
  let count = 0;
  const before = Date.now();
  let watchdogTimer;
  let result;
  try {
    result = await Promise.race([
      instance.runFile(file, { ...budget, ...options }).then(value => { count++; return value; }),
      new Promise((_, reject) => { watchdogTimer = setTimeout(() => reject(new Error('Independent per-fixture deadline')), 15000); })
    ]);
  } finally { clearTimeout(watchdogTimer); }
  assert.ok(Date.now() - before < 15000, 'Execution and cleanup must be bounded');
  assert.equal(count, 1);
  assert.ok(result.elapsed_ms >= 0);
  return result;
}
function fakeChild() {
  const child = new EventEmitter();
  Object.assign(child, { pid: 1234, stdout: new PassThrough(), stderr: new PassThrough(), unref() {} });
  return child;
}

(async () => {
  try {
    const success = await checked(fixture('success', "console.log('partial success');"));
    assert.equal(success.passed, true); assert.equal(success.reason, 'completed');
    assert.equal(success.executed, true); assert.equal(success.cleanup_succeeded, true);
    assert.match(success.output, /partial success/);
    const failure = await checked(fixture('nonzero', "console.error('expected failure');process.exit(7)"));
    assert.equal(failure.passed, false); assert.equal(failure.code, 7);
    assert.match(failure.output, /expected failure/);
    const startup = await checked('unused', {}, loadRunner({ cp: {
      spawn: (_executable, args, options) => cp.spawn(path.join(root, 'missing-executable'), args, options)
    } }));
    assert.equal(startup.executed, false); assert.equal(startup.reason, 'startup_error');
    assert.equal(startup.passed, false); assert.equal(startup.cleanup_succeeded, true);
    assert.match(startup.output, /ENOENT/);

    for (const ignore of [false, true]) {
      const f = pidFixture('timeout-' + ignore, "console.log('before silence');setInterval(()=>{},1000)",
        ignore ? "process.on('SIGTERM',()=>{});" : "");
      const pending = checked(f.file);
      await until(() => fs.existsSync(f.marker));
      const pid = observe(f.marker);
      const result = await pending;
      assert.equal(result.reason, 'timeout'); assert.equal(result.passed, false);
      assert.equal(result.cleanup_succeeded, true, result.output); assert.equal(alive(pid), false);
      if (ignore && process.platform !== 'win32') assert.equal(result.signal, 'SIGKILL', 'Ignored grace must escalate');
      assert.match(result.output, /before silence/); assert.match(result.output, /timeout: elapsed.*deadline/);
    }
    const silent = pidFixture('silent', 'setInterval(()=>{},1000)');
    const silentRun = checked(silent.file);
    await until(() => fs.existsSync(silent.marker));
    const silentPid = observe(silent.marker);
    const silence = await silentRun;
    assert.equal(silence.reason, 'timeout'); assert.equal(silence.passed, false);
    assert.equal(alive(silentPid), false);

    const cancel = pidFixture('cancel', "console.log('ready to cancel');setInterval(()=>{},1000)", "process.on('SIGTERM',()=>{});");
    const controller = new AbortController();
    const pending = checked(cancel.file, { signal: controller.signal, timeoutMs: 5000 });
    await until(() => fs.existsSync(cancel.marker));
    const cancelPid = observe(cancel.marker);
    controller.abort();
    const cancelled = await pending;
    assert.equal(cancelled.reason, 'cancelled'); assert.equal(cancelled.passed, false);
    assert.equal(cancelled.cleanup_succeeded, true); assert.equal(alive(cancelPid), false);

    const overflow = pidFixture('overflow', "process.stdout.write('diagnostic prefix\\n'+'x'.repeat(2*1024*1024));setInterval(()=>{},1000)");
    const overflowing = checked(overflow.file);
    await until(() => fs.existsSync(overflow.marker));
    const overflowPid = observe(overflow.marker);
    const oversized = await overflowing;
    assert.equal(oversized.reason, 'output_overflow'); assert.equal(oversized.passed, false);
    assert.ok(oversized.output.length <= 1024 * 1024 + 1000);
    assert.match(oversized.output, /^diagnostic prefix/); assert.equal(alive(overflowPid), false);

    // Keep the launcher alive for the native Windows /T fixture so ancestry is
    // available. Observed PIDs establish more than taskkill's exit status.
    const descendantMarker = path.join(root, 'tree-descendant.pid');
    const descendant = fixture('descendant', "process.on('SIGTERM',()=>{});require('fs').writeFileSync(" + JSON.stringify(descendantMarker) +
      ",String(process.pid));console.log('descendant ready');setInterval(()=>{},1000)");
    const parent = pidFixture('tree', "require('child_process').spawn(process.execPath,[" + JSON.stringify(descendant) +
      "],{stdio:['ignore','inherit','inherit']});setInterval(()=>{},1000)", "process.on('SIGTERM',()=>{});");
    const treeRun = checked(parent.file);
    await until(() => fs.existsSync(parent.marker) && fs.existsSync(descendantMarker));
    const parentPid = observe(parent.marker), descendantPid = observe(descendantMarker);
    const tree = await treeRun;
    assert.equal(tree.passed, false); assert.equal(alive(parentPid), false);
    assert.equal(running(descendantPid), false, 'Observed owned descendant must stop executing');
    // POSIX orphan zombies may remain visible to kill(0); they are conservatively
    // treated as uncertain ownership until init reaps them.
    if (process.platform === 'win32') {
      assert.equal(tree.cleanup_succeeded, true); assert.equal(alive(descendantPid), false);
    }

    fs.unlinkSync(descendantMarker);
    const launcher = fixture('launcher', "const cp=require('child_process'),fs=require('fs');const c=cp.spawn(process.execPath,[" +
      JSON.stringify(descendant) + "],{stdio:['ignore','inherit','inherit']});c.unref();" +
      "const ready=setInterval(()=>{if(fs.existsSync(" + JSON.stringify(descendantMarker) + "))clearInterval(ready)},10);");
    const inheritedRun = checked(launcher);
    await until(() => fs.existsSync(descendantMarker));
    const inheritedPid = observe(descendantMarker);
    const inherited = await inheritedRun;
    if (process.platform === 'win32') {
      // Windows Node reports close before this unref'd descendant exits. Such
      // post-launcher ancestry is outside the supported tree boundary; unlike
      // POSIX, inherited handles do not establish observable ownership here.
      assert.equal(inherited.code, 0);
      console.log('Windows post-launcher unref ancestry is not observable through Node close.');
    } else {
      assert.equal(inherited.passed, false); assert.equal(inherited.reason, 'timeout');
      assert.equal(inherited.code, 0);
      assert.equal(running(inheritedPid), false, 'Owned inherited-pipe descendant must stop executing');
    }

    // Deterministic competing events: deadline wins, then exit(0), close and
    // cancellation arrive. Retain the timeout instead of a misleading success.
    const child = fakeChild();
    const raceController = new AbortController();
    const raceRunner = loadRunner({ process: { ...process, platform: 'win32' }, cp: {
      spawn: () => { queueMicrotask(() => child.emit('spawn')); return child; },
      spawnSync: () => {
        setImmediate(() => {
          child.emit('exit', 0, null); child.emit('close', 0, null); raceController.abort();
        });
        return { status: 0 };
      }
    } });
    const raced = await checked('race', { timeoutMs: 20, signal: raceController.signal }, raceRunner);
    assert.equal(raced.reason, 'timeout'); assert.equal(raced.code, 0); assert.equal(raced.passed, false);
    assert.equal(child.listenerCount('close'), 0); assert.equal(child.listenerCount('exit'), 0);
    assert.equal(child.stdout.listenerCount('data'), 0);
    await new Promise(resolve => setTimeout(resolve, 200));

    // Exercise the actual synchronous helper timeout using a disposable native
    // process, even on POSIX. Only command selection is simulated.
    let helperCalls = 0;
    const helperRunner = loadRunner({ process: { ...process, platform: 'win32' }, cp: {
      spawnSync: (_command, _args, options) => {
        helperCalls++;
        assert.equal(options.timeout, 80, 'Helper must retain its own deadline');
        const helper = pidFixture('hung-helper', "setInterval(()=>{},1000)");
        const result = cp.spawnSync(process.execPath, [helper.file], options);
        assert.equal(result.error.code, 'ETIMEDOUT');
        assert.equal(options.killSignal, 'SIGKILL');
        if (fs.existsSync(helper.marker)) assert.equal(alive(observe(helper.marker)), false);
        return result;
      }
    } });
    const beforeHelper = Date.now();
    assert.equal(helperRunner.terminate({ pid: 1234 }, true, 80), false);
    assert.ok(Date.now() - beforeHelper < 2000); assert.equal(helperCalls, 1);

    // Run the CLI with an unprovable cleanup result. Its actual sweep and report
    // must skip the next file, preserve identities, and fail overall.
    const cliRoot = path.join(root, 'cli');
    fs.mkdirSync(cliRoot);
    fs.writeFileSync(path.join(cliRoot, 'frontend-tests.json'), JSON.stringify({ safety: [], portable: [] }));
    fs.writeFileSync(path.join(cliRoot, 'a.test.js'), '');
    fs.writeFileSync(path.join(cliRoot, 'b.test.js'), "require('fs').writeFileSync('later-started','')");
    const receipt = path.join(cliRoot, 'results.json');
    const cli = fixture('cli-driver', "const vm=require('vm'),fs=require('fs');const cp=require('child_process');" +
      "const {EventEmitter}=require('events'),{PassThrough}=require('stream');" +
      "const child=new EventEmitter();Object.assign(child,{pid:1234,stdout:new PassThrough(),stderr:new PassThrough(),unref(){}});" +
      "const context={require:n=>n==='child_process'?{spawn(){setImmediate(()=>{child.emit('spawn');child.emit('exit',0,null)});return child},spawnSync(){return {status:1}}}:require(n.startsWith('./')?require('path').resolve(process.cwd(),n):n)," +
      "process:{...process,platform:'win32',on:process.on.bind(process),removeListener:process.removeListener.bind(process)},console,Buffer,AbortController,__dirname:" + JSON.stringify(cliRoot) + "," +
      "module:{exports:{}},setTimeout,clearTimeout,setInterval,clearInterval};" +
      "vm.runInNewContext(" + JSON.stringify(source.replace('timeoutMs: 900000, graceMs: 5000, killWaitMs: 2000, taskkillMs: 2000',
        'timeoutMs: 20, graceMs: 20, killWaitMs: 20, taskkillMs: 20')) +
      ",context);context.module.exports.main().then(()=>{process.exitCode=context.process.exitCode});");
    const cliRun = cp.spawnSync(process.execPath, [cli], { cwd: cliRoot,
      env: { ...process.env, ASAP_CI_FRONTEND_RESULTS: receipt }, encoding: 'utf8', timeout: 5000 });
    assert.equal(cliRun.status, 1, cliRun.stderr);
    assert.ok(fs.existsSync(receipt), cliRun.stderr + cliRun.stdout);
    const report = JSON.parse(fs.readFileSync(receipt));
    assert.deepEqual([report.executed, report.passed, report.failed, report.skipped], [1, 0, 1, 1]);
    assert.equal(report.file_results.length, 2); assert.equal(new Set(report.file_results.map(row => row.file)).size, 2);
    assert.equal(report.file_results[0].reason, 'timeout');
    assert.equal(report.file_results[0].code, 0);
    assert.equal(report.file_results[0].cleanup_succeeded, false);
    assert.equal(report.file_results[1].reason, 'not_started_cleanup');
    assert.equal(report.file_results[1].executed, false);
    assert.equal(fs.existsSync(path.join(cliRoot, 'later-started')), false);
    assert.match(cliRun.stderr, /not_started_cleanup/);
    console.log('Frontend deadline, output, cancellation, ownership, race and receipt fixtures passed on ' + process.platform + '.');
  } finally {
    clearTimeout(watchdog);
    cleanup();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
