const assert = require("assert");
const path = require("path");

global.__hooks = path.resolve(__dirname, "../pb_hooks");

const config = require("../lib/config.js");

function byteJson(value) {
  return Array.from(Buffer.from(JSON.stringify(value), "utf8"));
}

function runTests() {
  console.log("Running parseJsonObject tests...");

  // 1. Valid JSON object string
  assert.deepStrictEqual(config.parseJsonObject('{"key": "value"}'), { key: "value" });

  // 2. Invalid JSON string (triggers try/catch error block)
  assert.deepStrictEqual(config.parseJsonObject('{"invalid": json}'), {});
  assert.deepStrictEqual(config.parseJsonObject('{"invalid": json}', { custom: "fallback" }), { custom: "fallback" });

  // 3. Already a JavaScript object
  const obj = { a: 1 };
  assert.strictEqual(config.parseJsonObject(obj), obj);

  // 3b. PocketBase hook JSON fields may arrive as UTF-8 byte arrays
  assert.deepStrictEqual(config.parseJsonObject(byteJson({ label: "Café" })), { label: "Café" });

  // 4. Not a string or object
  assert.deepStrictEqual(config.parseJsonObject(123), {});

  // 5. Array (should not be treated as a plain object)
  assert.deepStrictEqual(config.parseJsonObject([]), {});
  assert.deepStrictEqual(config.parseJsonObject("[1, 2]"), {});

  // 6. Empty/whitespace string
  assert.deepStrictEqual(config.parseJsonObject(""), {});
  assert.deepStrictEqual(config.parseJsonObject("   "), {});

  // 7. Valid JSON string but parses to non-object (e.g., number, string)
  assert.deepStrictEqual(config.parseJsonObject("123"), {});
  assert.deepStrictEqual(config.parseJsonObject('"string"'), {});

  console.log("All parseJsonObject tests passed!");
}

runTests();
