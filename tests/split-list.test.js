const assert = require("assert");

global.__hooks = __dirname + "/../pb_hooks";
const splitList = require("../lib/split-list.js");

assert.deepStrictEqual(splitList.split("a,b,c"), ["a", "b", "c"]);
assert.deepStrictEqual(splitList.split(" a , b , c "), ["a", "b", "c"]);
assert.deepStrictEqual(splitList.split(""), []);
assert.deepStrictEqual(splitList.split(null), []);
assert.deepStrictEqual(splitList.split("single"), ["single"]);
assert.deepStrictEqual(splitList.split("a,,b"), ["a", "b"]);

console.log("split-list tests passed.");
