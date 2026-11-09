// tests/run_all.js
const fs = require("fs");
const path = require("path");
const cp = require("child_process");
const assert = require("node:assert/strict");

const testDir = __dirname;
const mode = process.argv[2] || "";
const { safety: safetyFiles, portable } = require("./frontend-tests.json");
const isSafety = mode === "--ci-safety";
const isRemaining = mode === "--ci-remaining";
const filterArg = isSafety || isRemaining ? "" : mode.toLowerCase();
const allFiles = [...fs.readdirSync(testDir).filter((name) => name.endsWith(".test.js")), ...portable].sort();
assert.equal(new Set(allFiles).size, allFiles.length, "Duplicate frontend identity");
for (const file of allFiles) assert.ok(fs.statSync(path.join(testDir, file)).isFile(), "Missing frontend test: " + file);
function completeReceipts(report, expected) {
  assert.deepEqual(report.file_results.map(row => row.file).sort(), expected);
  for (const row of report.file_results) {
    assert.equal(row.passed, true);
    assert.equal(row.code, 0);
    assert.equal(row.signal, null);
  }
}
let prior = { executed: 0, passed: 0, failed: 0, skipped: 0 };
if (isRemaining) {
  prior = JSON.parse(fs.readFileSync(process.env.ASAP_CI_FRONTEND_SAFETY, "utf8"));
  assert.equal(prior.filter, "--ci-safety");
  assert.deepEqual(prior.files, safetyFiles);
  assert.equal(prior.executed, safetyFiles.length);
  assert.equal(prior.passed, safetyFiles.length);
  assert.equal(prior.failed, 0);
  assert.equal(prior.skipped, 0);
  completeReceipts(prior, safetyFiles);
}
const files = allFiles
  .filter((name) => {
    if (isSafety) return safetyFiles.includes(name);
    if (isRemaining) return !safetyFiles.includes(name);
    if (filterArg) {
      return name.toLowerCase().includes(filterArg);
    }
    return true;
  })
  .sort();
assert.ok(files.length > 0, "No frontend tests selected");
if (isSafety) assert.deepEqual(files, safetyFiles);

// Keep one child active: concurrent sweeps timed out on the 2-vCPU CI runner.
const children = new Set();
const outcomes = new Map();
let cancelled = false;

function terminate(child, signal) {
  if (!child.pid) return;
  if (process.platform === "win32") {
    cp.spawnSync("taskkill", ["/PID", String(child.pid), "/T", "/F"], { windowsHide: true, stdio: "ignore" });
  } else {
    try { process.kill(-child.pid, signal); } catch (error) { if (error.code !== "ESRCH") throw error; }
  }
}

function stop(signal) {
  cancelled = true;
  for (const child of children) terminate(child, signal);
  if (signal !== "SIGKILL" && children.size) {
    setTimeout(() => stop("SIGKILL"), 5000).unref();
  }
}
process.once("SIGINT", () => stop("SIGTERM"));
process.once("SIGTERM", () => stop("SIGTERM"));

function run(file) {
  return new Promise((resolve) => {
    console.log("START " + file);
    const started = process.hrtime.bigint();
    const child = cp.spawn(process.execPath, [path.join(testDir, file)], {
      shell: false, windowsHide: true, detached: process.platform !== "win32",
      stdio: ["ignore", "pipe", "pipe"]
    });
    children.add(child);
    let output = "", overflow = false;
    const collect = (chunk) => {
      if (output.length + chunk.length > 1024 * 1024) {
        overflow = true;
        terminate(child, "SIGKILL");
      } else {
        output += chunk.toString();
      }
    };
    child.stdout.on("data", collect);
    child.stderr.on("data", collect);
    child.on("error", (error) => { output += error.message; });
    child.on("close", (code, signal) => {
      children.delete(child);
      outcomes.set(file, { file, passed: code === 0 && !signal && !overflow && !cancelled,
        code, signal, elapsed_ms: Number(process.hrtime.bigint() - started) / 1e6, output });
      resolve();
    });
  });
}

async function main() {
  const safety = files.filter((file) => safetyFiles.includes(file));
  for (const file of safety) {
    if (!cancelled) await run(file);
  }
  for (const file of files.filter((file) => !safetyFiles.includes(file))) {
    if (!cancelled) await run(file);
  }
  let passed = 0;
  for (const file of files) {
    const result = outcomes.get(file);
    console.log("\n==> " + file);
    if (result) process.stdout.write(result.output);
    if (result?.passed) {
      passed++;
    } else {
      console.error("FAILED: " + file + (result ? "" : " (cancelled before execution)"));
    }
  }
  const failed = files.length - passed;
  if (process.env.ASAP_CI_FRONTEND_RESULTS) {
    const reportPath = path.resolve(process.env.ASAP_CI_FRONTEND_RESULTS);
    fs.mkdirSync(path.dirname(reportPath), { recursive: true });
    fs.writeFileSync(reportPath, JSON.stringify({
      filter: isSafety ? mode : filterArg, files: isRemaining ? allFiles : files,
      executed: prior.executed + outcomes.size,
      passed: prior.passed + passed, failed: prior.failed + failed, skipped: 0, workers: 1,
      file_results: [...(prior.file_results || []), ...files.flatMap((file) => {
        const result = outcomes.get(file);
        if (!result) return [];
        const { output, ...receipt } = result;
        return [receipt];
      })]
    }) + "\n");
  }
  process.exitCode = failed || cancelled ? 1 : 0;
}
main().catch((error) => { stop("SIGKILL"); console.error(error); process.exitCode = 1; });
