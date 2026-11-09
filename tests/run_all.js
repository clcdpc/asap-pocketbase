// tests/run_all.js
const fs = require("fs");
const path = require("path");
const cp = require("child_process");
const assert = require("node:assert/strict");

// Fifteen minutes includes output draining. See scripts/ci/test-selection.md for
// the timing basis and the deliberately narrower Windows ownership boundary.
const limits = { timeoutMs: 900000, graceMs: 5000, killWaitMs: 2000, taskkillMs: 2000 };
const outputLimit = 1024 * 1024;

function groupExists(pid) {
  try { process.kill(-pid, 0); return true; }
  catch (error) { if (error.code === "ESRCH") return false; throw error; }
}

function terminate(child, force, taskkillMs) {
  if (!child.pid) return false;
  if (process.platform === "win32") {
    const result = cp.spawnSync("taskkill", ["/PID", String(child.pid), "/T", ...(force ? ["/F"] : [])], {
      windowsHide: true, stdio: "ignore", timeout: taskkillMs, killSignal: "SIGKILL"
    });
    return !result.error && result.status === 0;
  }
  try { process.kill(-child.pid, force ? "SIGKILL" : "SIGTERM"); return true; }
  catch (error) { if (error.code === "ESRCH") return true; throw error; }
}

function runFile(file, { signal, ...overrides } = {}) {
  const budget = { ...limits, ...overrides };
  return new Promise((resolve) => {
    const started = process.hrtime.bigint();
    const child = cp.spawn(process.execPath, [file], {
      shell: false, windowsHide: true, detached: process.platform !== "win32",
      stdio: ["ignore", "pipe", "pipe"]
    });
    let output = Buffer.alloc(0), reason = null, code = null, exitSignal = null;
    let executed = false, exited = false, closed = false, settled = false;
    let cleanupStarted = false, treeKilled = false, cleanupError = "";
    let escalation, postKill, poll;
    const elapsed = () => Number(process.hrtime.bigint() - started) / 1e6;
    const diagnostic = (text) => { cleanupError = (cleanupError + (cleanupError ? "; " : "") + text).slice(0, 1024); };
    const ownedGone = () => {
      if (!child.pid) return closed;
      if (process.platform === "win32") return closed && (!cleanupStarted || treeKilled);
      try { return closed && !groupExists(child.pid); }
      catch (error) { diagnostic(error.message); return false; }
    };
    const finish = (cleanupSucceeded) => {
      if (settled) return;
      settled = true;
      clearTimeout(deadline); clearTimeout(escalation); clearTimeout(postKill); clearInterval(poll);
      signal?.removeEventListener("abort", cancel);
      child.removeListener("spawn", onSpawn); child.removeListener("exit", onExit);
      child.removeListener("close", onClose); child.removeListener("error", onError);
      child.stdout.removeListener("data", collect); child.stderr.removeListener("data", collect);
      // An escaped descendant can retain pipes. Release our handles even when
      // ownership cannot be established, then stop the serial sweep.
      child.stdout.destroy(); child.stderr.destroy(); child.unref();
      resolve({ passed: executed && !reason && code === 0 && !exitSignal && cleanupSucceeded,
        executed, code, signal: exitSignal, reason: reason || (code === 0 ? "completed" : "exit"),
        cleanup_succeeded: cleanupSucceeded, elapsed_ms: elapsed(), timeout_ms: budget.timeoutMs,
        output: output.toString() + (reason ? "\n" + reason + ": elapsed " + Math.round(elapsed()) +
          "ms (deadline " + budget.timeoutMs + "ms)." : "") +
          (!cleanupSucceeded ? "\nOwned cleanup could not be established; remaining files will not start." : "") +
          (cleanupError ? "\nCleanup: " + cleanupError : "") });
    };
    const check = () => { if (ownedGone()) finish(true); };
    const kill = (force) => {
      try {
        const killed = terminate(child, force, budget.taskkillMs);
        treeKilled ||= killed;
        if (!killed) diagnostic((force ? "forced" : "graceful") + " tree termination failed or exceeded its bound");
      } catch (error) { diagnostic(error.message); }
    };
    const stop = (failure) => {
      if (settled) return;
      reason ||= failure;
      if (cleanupStarted) return;
      cleanupStarted = true;
      kill(false);
      check();
      if (settled) return;
      poll = setInterval(check, 25);
      escalation = setTimeout(() => {
        kill(true);
        check();
        if (!settled) postKill = setTimeout(() => finish(ownedGone()), budget.killWaitMs);
      }, budget.graceMs);
    };
    const cancel = () => stop("cancelled");
    const collect = (chunk) => {
      const remaining = outputLimit - output.length;
      if (remaining > 0) output = Buffer.concat([output, chunk.subarray(0, remaining)]);
      if (chunk.length > remaining) stop("output_overflow");
    };
    const onSpawn = () => { executed = true; };
    const onExit = (value, valueSignal) => { exited = true; code = value; exitSignal = valueSignal; };
    const onError = (error) => {
      output = Buffer.concat([output, Buffer.from(error.message).subarray(0, outputLimit - output.length)]);
      stop("startup_error");
    };
    const onClose = (value, valueSignal) => {
      closed = true;
      if (!exited) { code = value; exitSignal = valueSignal; }
      if (!reason && elapsed() >= budget.timeoutMs) reason = "timeout";
      if (ownedGone()) finish(true);
      else stop(reason || "owned_descendants");
    };
    const deadline = setTimeout(() => stop("timeout"), budget.timeoutMs);
    child.once("spawn", onSpawn); child.once("exit", onExit);
    child.once("error", onError); child.once("close", onClose);
    child.stdout.on("data", collect); child.stderr.on("data", collect);
    signal?.addEventListener("abort", cancel, { once: true });
    if (signal?.aborted) cancel();
  });
}

function completeReceipts(report, expected) {
  assert.deepEqual(report.file_results.map(row => row.file).sort(), expected);
  for (const row of report.file_results) {
    assert.equal(row.passed, true); assert.equal(row.code, 0); assert.equal(row.signal, null);
    assert.equal(row.executed, true); assert.equal(row.reason, "completed");
    assert.equal(row.cleanup_succeeded, true);
  }
}

async function main() {
  const testDir = __dirname;
  const mode = process.argv[2] || "";
  const { safety: safetyFiles, portable } = require("./frontend-tests.json");
  const isSafety = mode === "--ci-safety", isRemaining = mode === "--ci-remaining";
  const filterArg = isSafety || isRemaining ? "" : mode.toLowerCase();
  const allFiles = [...fs.readdirSync(testDir).filter(name => name.endsWith(".test.js")), ...portable].sort();
  assert.equal(new Set(allFiles).size, allFiles.length, "Duplicate frontend identity");
  for (const file of allFiles) assert.ok(fs.statSync(path.join(testDir, file)).isFile(), "Missing frontend test: " + file);
  let prior = { executed: 0, passed: 0, failed: 0, skipped: 0 };
  if (isRemaining) {
    prior = JSON.parse(fs.readFileSync(process.env.ASAP_CI_FRONTEND_SAFETY, "utf8"));
    assert.equal(prior.filter, "--ci-safety"); assert.deepEqual(prior.files, safetyFiles);
    assert.equal(prior.executed, safetyFiles.length); assert.equal(prior.passed, safetyFiles.length);
    assert.equal(prior.failed, 0); assert.equal(prior.skipped, 0);
    completeReceipts(prior, safetyFiles);
  }
  const files = allFiles.filter(name => isSafety ? safetyFiles.includes(name) :
    isRemaining ? !safetyFiles.includes(name) : !filterArg || name.toLowerCase().includes(filterArg));
  assert.ok(files.length > 0, "No frontend tests selected");
  if (isSafety) assert.deepEqual(files, safetyFiles);
  const controller = new AbortController();
  const cancel = () => controller.abort();
  process.on("SIGINT", cancel); process.on("SIGTERM", cancel);
  const outcomes = new Map();
  let cleanupFailed = false;
  try {
    // Keep one child active: concurrent sweeps timed out on the 2-vCPU CI runner.
    for (const file of [...files.filter(file => safetyFiles.includes(file)), ...files.filter(file => !safetyFiles.includes(file))]) {
      if (controller.signal.aborted || cleanupFailed) break;
      console.log("START " + file);
      const result = await runFile(path.join(testDir, file), { signal: controller.signal });
      outcomes.set(file, { file, ...result });
      cleanupFailed = !result.cleanup_succeeded;
    }
  } finally {
    process.removeListener("SIGINT", cancel); process.removeListener("SIGTERM", cancel);
  }
  let passed = 0, failed = 0, skipped = 0, executed = 0;
  for (const file of files) {
    let result = outcomes.get(file);
    if (!result) {
      result = { file, passed: false, executed: false, code: null, signal: null,
        reason: cleanupFailed ? "not_started_cleanup" : "not_started_cancelled", elapsed_ms: 0, cleanup_succeeded: true };
      outcomes.set(file, result);
      skipped++;
    } else {
      executed += Number(result.executed);
      if (result.passed) passed++; else if (result.executed) failed++; else skipped++;
    }
    console.log("\n==> " + file);
    if (result.output) process.stdout.write(result.output);
    if (!result.passed) console.error("FAILED: " + file + " (" + result.reason + ")");
  }
  if (process.env.ASAP_CI_FRONTEND_RESULTS) {
    const reportPath = path.resolve(process.env.ASAP_CI_FRONTEND_RESULTS);
    fs.mkdirSync(path.dirname(reportPath), { recursive: true });
    fs.writeFileSync(reportPath, JSON.stringify({ filter: isSafety ? mode : filterArg, files: isRemaining ? allFiles : files,
      executed: prior.executed + executed, passed: prior.passed + passed, failed: prior.failed + failed,
      skipped: prior.skipped + skipped, workers: 1,
      file_results: [...(prior.file_results || []), ...files.map(file => {
        const { output, ...receipt } = outcomes.get(file); return receipt;
      })] }) + "\n");
  }
  process.exitCode = failed || skipped || controller.signal.aborted ? 1 : 0;
}

module.exports = { runFile, terminate, limits, main };
if (require.main === module) main().catch(error => { console.error(error); process.exitCode = 1; });
