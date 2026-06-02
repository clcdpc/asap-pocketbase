const assert = require("assert");
const path = require("path");

global.__hooks = path.resolve(__dirname, "../pb_hooks");

const httpUtils = require("../lib/http_utils.js");

function runTests() {
  console.log("Running parseJsonObject tests...");

  assert.deepStrictEqual(httpUtils.parseJsonObject('{"key": "value"}'), { key: "value" });
  assert.deepStrictEqual(httpUtils.parseJsonObject('  {"key": "value"}  '), { key: "value" });

  const obj = { a: 1 };
  assert.strictEqual(httpUtils.parseJsonObject(obj), obj);

  assert.deepStrictEqual(httpUtils.parseJsonObject([1, 2]), {});
  assert.deepStrictEqual(httpUtils.parseJsonObject([1, 2], { default: true }), { default: true });

  assert.deepStrictEqual(httpUtils.parseJsonObject('{"broken": '), {});
  assert.deepStrictEqual(httpUtils.parseJsonObject('invalid json', { fb: 1 }), { fb: 1 });

  assert.deepStrictEqual(httpUtils.parseJsonObject(123), {});
  assert.deepStrictEqual(httpUtils.parseJsonObject(true, { b: 2 }), { b: 2 });
  assert.deepStrictEqual(httpUtils.parseJsonObject(null), {});
  assert.deepStrictEqual(httpUtils.parseJsonObject(undefined), {});

  assert.deepStrictEqual(httpUtils.parseJsonObject(""), {});
  assert.deepStrictEqual(httpUtils.parseJsonObject("   "), {});

  assert.deepStrictEqual(httpUtils.parseJsonObject('"[1, 2]"'), {});
  assert.deepStrictEqual(httpUtils.parseJsonObject("123"), {});
  assert.deepStrictEqual(httpUtils.parseJsonObject('"string"'), {});
  assert.deepStrictEqual(httpUtils.parseJsonObject("null"), {});
  assert.deepStrictEqual(httpUtils.parseJsonObject("true"), {});

  console.log("All http_utils parseJsonObject tests passed!");
}

runTests();
