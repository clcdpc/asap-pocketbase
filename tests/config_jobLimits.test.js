const assert = require("assert");
const path = require("path");

global.__hooks = path.resolve(__dirname, "../pb_hooks");

let env = {};
global.$os = {
  getenv(name) {
    return env[name] || "";
  }
};

const config = require("../lib/config.js");

function runTests() {
  env = {};
  assert.deepStrictEqual(config.jobLimits("pending_holds"), { pageSize: 50, maxPerRun: 500 });

  env = {
    ASAP_JOB_PAGE_SIZE: "25",
    ASAP_JOB_MAX_PER_RUN: "300",
  };
  assert.deepStrictEqual(config.jobLimits("pending_holds"), { pageSize: 25, maxPerRun: 300 });

  env = {
    ASAP_JOB_PAGE_SIZE: "nope",
    ASAP_JOB_MAX_PER_RUN: "-10",
  };
  assert.deepStrictEqual(config.jobLimits("pending_holds"), { pageSize: 50, maxPerRun: 1 });

  env = {
    ASAP_JOB_PAGE_SIZE: "20",
    ASAP_JOB_MAX_PER_RUN: "200",
    ASAP_TIMEOUT_PAGE_SIZE: "40",
    ASAP_TIMEOUT_MAX_PER_RUN: "400",
    ASAP_HOLD_PICKUP_TIMEOUT_PAGE_SIZE: "10",
    ASAP_HOLD_PICKUP_TIMEOUT_MAX_PER_RUN: "100",
  };
  assert.deepStrictEqual(config.jobLimits("hold_pickup_timeout"), { pageSize: 10, maxPerRun: 100 });
  assert.deepStrictEqual(config.jobLimits("outstanding_timeout"), { pageSize: 40, maxPerRun: 400 });

  env = {
    ASAP_JOB_PAGE_SIZE: "9999",
    ASAP_JOB_MAX_PER_RUN: "999999",
  };
  assert.deepStrictEqual(config.jobLimits("checked_out"), { pageSize: 500, maxPerRun: 5000 });

  console.log("job limit config tests passed.");
}

runTests();
