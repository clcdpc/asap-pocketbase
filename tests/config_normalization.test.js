const assert = require("assert");
const path = require("path");

// Mock environment
global.__hooks = path.resolve(__dirname, "../pb_hooks");

const normalization = require("../lib/config/normalization.js");

function testNormalization() {
  console.log("Running config normalization tests...");

  // 1. normalizeLeapBibUrlPattern
  assert.strictEqual(normalization.normalizeLeapBibUrlPattern("http://ex.com/"), "http://ex.com/{{bibid}}");
  assert.strictEqual(normalization.normalizeLeapBibUrlPattern("http://ex.com/{{bibid}}"), "http://ex.com/{{bibid}}");

  // 2. normalizeFormatIconUrlPattern
  const defaultPattern = normalization.defaultFormatIconUrlPattern();
  assert.strictEqual(normalization.normalizeFormatIconUrlPattern(""), defaultPattern);
  assert.strictEqual(normalization.normalizeFormatIconUrlPattern("  "), defaultPattern);
  
  // No placeholder -> fallback to default
  assert.strictEqual(normalization.normalizeFormatIconUrlPattern("http://ex.com/icon.gif"), defaultPattern);
  
  // Valid patterns
  assert.strictEqual(normalization.normalizeFormatIconUrlPattern("http://ex.com/{id}.gif"), "http://ex.com/{id}.gif");
  assert.strictEqual(normalization.normalizeFormatIconUrlPattern("http://ex.com/{MARCTypeOfMaterialID2}.gif"), "http://ex.com/{MARCTypeOfMaterialID2}.gif");
  
  // Unsafe schemes -> fallback to default
  assert.strictEqual(normalization.normalizeFormatIconUrlPattern("javascript:alert(1){id}"), defaultPattern);
  assert.strictEqual(normalization.normalizeFormatIconUrlPattern("data:text/html,{id}"), defaultPattern);

  console.log("Config normalization tests passed.");
}

testNormalization();
