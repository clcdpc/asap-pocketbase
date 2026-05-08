const assert = require("assert");

global.__hooks = __dirname + "/../pb_hooks";

const staffRoutes = require("../lib/staff_routes.js");

class MockRecord {
  constructor(data) {
    this.data = Object.assign({}, data || {});
    this.id = this.data.id || "staff1";
  }
  collection() {
    return { name: "staff_users" };
  }
  get(key) {
    return this.data[key];
  }
  set(key, value) {
    this.data[key] = value;
  }
  getBool(key) {
    return !!this.data[key];
  }
  getString(key) {
    return this.data[key] || "";
  }
}

function makeEvent(staff, body) {
  return {
    app: {
      saved: [],
      save(record) {
        this.saved.push(record);
      }
    },
    requestInfo() {
      return { auth: staff, body: body || {} };
    },
    json(status, responseBody) {
      return { status, body: responseBody };
    }
  };
}

{
  const staff = new MockRecord({
    id: "staff1",
    username: "jane",
    displayName: "Jane",
    role: "staff",
    active: true
  });
  const event = makeEvent(staff, {
    weekly_action_summary_enabled: true,
    purchase_reminder_default: false,
    default_mine_unclaimed_filter: true,
    weekly_action_summary_email: " jane@example.org "
  });

  const result = staffRoutes.staffProfileUpdate(event);

  assert.strictEqual(result.status, 200);
  assert.strictEqual(event.app.saved.length, 1);
  assert.strictEqual(staff.get("weekly_action_summary_enabled"), true);
  assert.strictEqual(staff.get("purchase_reminder_default"), false);
  assert.strictEqual(staff.get("default_mine_unclaimed_filter"), true);
  assert.strictEqual(staff.get("weekly_action_summary_email"), "jane@example.org");
  assert.strictEqual(result.body.default_mine_unclaimed_filter, true);
}

{
  const staff = new MockRecord({
    id: "staff2",
    username: "sam",
    role: "staff",
    active: true,
    default_mine_unclaimed_filter: true
  });
  const event = makeEvent(staff, {
    default_mine_unclaimed_filter: false
  });

  const result = staffRoutes.staffProfileUpdate(event);

  assert.strictEqual(result.status, 200);
  assert.strictEqual(staff.get("default_mine_unclaimed_filter"), false);
  assert.strictEqual(result.body.default_mine_unclaimed_filter, false);
}

console.log("Staff profile preference tests passed.");
