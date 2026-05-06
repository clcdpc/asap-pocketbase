const assert = require("assert");
const path = require("path");

global.__hooks = path.resolve(__dirname, "../pb_hooks");

const config = require("../lib/config.js");

function runTests() {
  console.log("Running config.parseJsonArray tests...");

  let passed = 0;
  let failed = 0;

  function test(name, fn) {
    try {
      fn();
      console.log(`✅ ${name} passed`);
      passed++;
    } catch (err) {
      console.error(`❌ ${name} failed:`, err.message);
      failed++;
    }
  }

  const fallbackArray = ["fallback"];

  test("parseJsonArray() handles already parsed arrays", () => {
    assert.deepStrictEqual(config.parseJsonArray([1, 2, 3], []), [1, 2, 3]);
  });

  test("parseJsonArray() parses valid JSON array strings", () => {
    assert.deepStrictEqual(config.parseJsonArray('["a", "b"]', []), ["a", "b"]);
    assert.deepStrictEqual(config.parseJsonArray('  [1, 2, 3]  ', []), [1, 2, 3]);
  });

  test("parseJsonArray() returns fallback for valid JSON but not an array", () => {
    assert.deepStrictEqual(config.parseJsonArray('{"a": 1}', fallbackArray), fallbackArray);
    assert.deepStrictEqual(config.parseJsonArray('"not_array"', fallbackArray), fallbackArray);
    assert.deepStrictEqual(config.parseJsonArray('123', fallbackArray), fallbackArray);
    assert.deepStrictEqual(config.parseJsonArray('true', fallbackArray), fallbackArray);
  });

  test("parseJsonArray() returns fallback for invalid JSON strings", () => {
    assert.deepStrictEqual(config.parseJsonArray('invalid', fallbackArray), fallbackArray);
    assert.deepStrictEqual(config.parseJsonArray('[1, 2', fallbackArray), fallbackArray);
    assert.deepStrictEqual(config.parseJsonArray('["a",]', fallbackArray), fallbackArray);
  });

  test("parseJsonArray() returns fallback for non-string types", () => {
    assert.deepStrictEqual(config.parseJsonArray(null, fallbackArray), fallbackArray);
    assert.deepStrictEqual(config.parseJsonArray(undefined, fallbackArray), fallbackArray);
    assert.deepStrictEqual(config.parseJsonArray(123, fallbackArray), fallbackArray);
    assert.deepStrictEqual(config.parseJsonArray(true, fallbackArray), fallbackArray);
    assert.deepStrictEqual(config.parseJsonArray({ a: 1 }, fallbackArray), fallbackArray);
  });

  test("parseJsonArray() returns fallback for empty or whitespace strings", () => {
    assert.deepStrictEqual(config.parseJsonArray('', fallbackArray), fallbackArray);
    assert.deepStrictEqual(config.parseJsonArray('   ', fallbackArray), fallbackArray);
  });

  test("parseJsonArray() defaults fallback to empty array if fallback is not an array", () => {
    assert.deepStrictEqual(config.parseJsonArray('invalid', null), []);
    assert.deepStrictEqual(config.parseJsonArray('invalid', "not array"), []);
    assert.deepStrictEqual(config.parseJsonArray('invalid', {a: 1}), []);
    assert.deepStrictEqual(config.parseJsonArray('invalid', undefined), []);
  });

  console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

  if (failed > 0) {
    process.exit(1);
  }
}

runTests();
