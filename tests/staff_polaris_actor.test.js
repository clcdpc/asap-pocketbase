const assert = require("assert");
const path = require("path");

global.__hooks = path.resolve(__dirname, "../pb_hooks");
const actor = require("../lib/staff/polaris_actor.js");

function makeStaff(fields) {
  return {
    get(key) {
      return fields[key];
    }
  };
}

{
  const resolved = actor.resolvePolarisUpdateActor(
    makeStaff({ role: "admin", polarisUserId: "77", username: "alex" }),
    { userId: "fallback" }
  );
  assert.strictEqual(resolved.polarisUserId, "77");
  assert.strictEqual(resolved.fallbackUsed, false);
}

{
  const resolved = actor.resolvePolarisUpdateActor(
    makeStaff({ role: "super_admin", polarisUserId: "", username: "root" }),
    { userId: "99" }
  );
  assert.strictEqual(resolved.polarisUserId, "99");
  assert.strictEqual(resolved.fallbackUsed, true);
}

assert.throws(
  () => actor.resolvePolarisUpdateActor(makeStaff({ role: "admin", polarisUserId: "" }), { userId: "99" }),
  /missing a Polaris user ID/
);

assert.throws(
  () => actor.resolvePolarisUpdateActor(makeStaff({ role: "super_admin", polarisUserId: "" }), { userId: "" }),
  /Configured Polaris system user ID is missing/
);

console.log("staff_polaris_actor.test.js passed.");
