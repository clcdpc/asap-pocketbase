const assert = require("assert");
const Module = require("module");

global.__hooks = __dirname + "/../pb_hooks";

const originalRequire = Module.prototype.require;

let jobsMock = {
  runWeeklyStaffActionSummary: () => {
    throw new Error("Mocked jobs error");
  }
};

let routeUtilsMock = {
  requestHeader: (e, header) => "Bearer secret",
  boolValue: (val, def) => def,
  body: (e) => ({})
};

let calledLoggerError = false;

Module.prototype.require = function(moduleName) {
  if (moduleName.includes("lib/jobs.js")) {
    return jobsMock;
  }
  if (moduleName.includes("lib/polaris.js")) {
    return {};
  }
  if (moduleName.includes("lib/route_utils.js")) {
    return routeUtilsMock;
  }
  return originalRequire.apply(this, arguments);
};

global.$os = {
  getenv: (v) => "secret"
};
global.$security = {
  equal: (a, b) => a === b
};

const jobRoutes = require("../lib/job_routes.js");

Module.prototype.require = originalRequire;

const e = {
  app: {
    logger: () => ({
      error: (msg, key, errStr) => {
        calledLoggerError = true;
        assert.strictEqual(msg, "Weekly staff action summary job failed");
        assert.strictEqual(errStr, "Error: Mocked jobs error");
      }
    })
  },
  json: (status, body) => {
    return { status, body };
  }
};

const result = jobRoutes.runWeeklyStaffActionSummary(e);

assert.strictEqual(result.status, 400);
assert.strictEqual(result.body.message, "Mocked jobs error");
assert.strictEqual(calledLoggerError, true);

console.log("job_routes.runWeeklyStaffActionSummary error path test passed.");
