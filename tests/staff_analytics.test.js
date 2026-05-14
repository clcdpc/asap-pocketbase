const assert = require("assert");

global.__hooks = __dirname + "/../pb_hooks";

const staffRoutes = require("../lib/staff_routes.js");

class MockRecord {
  constructor(data) {
    this.data = data;
    this.id = data.id || Math.random().toString(36).slice(2);
    this.created = data.created;
    this.updated = data.updated;
  }
  get(key) {
    return this.data[key];
  }
  getBool(key) {
    return !!this.data[key];
  }
}

function staff(role, libraryOrgId) {
  return new MockRecord({
    role,
    libraryOrgId,
    libraryOrgName: libraryOrgId ? "Library " + libraryOrgId : ""
  });
}

function appWithNoTags() {
  return {
    findRecordsByFilter(collection) {
      if (collection === "title_request_tags") return [];
      return [];
    }
  };
}

const rows = [
  new MockRecord({ id: "r1", status: "suggestion", libraryOrgId: "10", created: "2026-04-20 10:00:00", updated: "2026-04-20 10:00:00" }),
  new MockRecord({ id: "r2", status: "outstanding_purchase", libraryOrgId: "10", created: "2026-03-01 10:00:00", updated: "2026-03-02 10:00:00" }),
  new MockRecord({ id: "r3", status: "pending_hold", libraryOrgId: "20", created: "2026-04-01 10:00:00", updated: "2026-04-03 10:00:00", isbnCheckStatus: "error_max_retries" }),
  new MockRecord({ id: "r4", status: "closed", libraryOrgId: "10", closeReason: "rejected", created: "2026-04-01 10:00:00", updated: "2026-04-11 10:00:00" }),
  new MockRecord({ id: "r5", status: "closed", libraryOrgId: "10", closeReason: "hold_completed", created: "2026-03-01 10:00:00", updated: "2026-03-10 10:00:00" }),
  new MockRecord({ id: "r6", status: "hold_placed", libraryOrgId: "10", created: "2026-04-10 10:00:00", updated: "2026-04-11 10:00:00" }),
];

const events = [
  new MockRecord({ titleRequest: "r4", eventType: "hold_skipped", created: "2026-04-03 10:00:00" }),
  new MockRecord({ titleRequest: "r5", eventType: "hold_placed", created: "2026-03-10 10:00:00" }),
  new MockRecord({ titleRequest: "r4", eventType: "hold_placed", created: "2026-04-06 10:00:00" }),
  new MockRecord({ titleRequest: "r4", eventType: "hold_placed", created: "2026-04-08 10:00:00" }),
  new MockRecord({ titleRequest: "r6", eventType: "hold_placed" }),
];

function appWithEvents() {
  return {
    findRecordsByFilter(collection, filter, sort, limit, offset, params) {
      if (collection === "title_request_tags") return [];
      if (collection !== "title_request_events") return [];
      const ids = new Set(Object.keys(params || {}).map(key => params[key]));
      return events
        .filter(event => ids.has(event.get("titleRequest")))
        .sort((a, b) => new Date(a.get("created")) - new Date(b.get("created")))
        .slice(offset, offset + limit);
    }
  };
}

const range = {
  key: "last30",
  start: new Date("2026-04-01T00:00:00Z"),
  end: new Date("2026-05-01T00:00:00Z")
};

const libraryScope = staffRoutes.resolveAnalyticsScope(appWithNoTags(), staff("staff", "10"), "all");
assert.strictEqual(libraryScope.mode, "library");
assert.strictEqual(libraryScope.libraryOrgId, "10");
assert.strictEqual(libraryScope.filter, "libraryOrgId = {:libraryOrgId}");

const superAllScope = staffRoutes.resolveAnalyticsScope(appWithNoTags(), staff("super_admin", ""), "all");
assert.strictEqual(superAllScope.mode, "all");
assert.strictEqual(superAllScope.filter, "id != ''");

const staffWorkflowTamperedScope = staffRoutes.titleRequestListScope(appWithNoTags(), staff("staff", "10"), "20");
assert.strictEqual(staffWorkflowTamperedScope.mode, "library");
assert.strictEqual(staffWorkflowTamperedScope.libraryOrgId, "10");
assert.strictEqual(staffWorkflowTamperedScope.filter, "libraryOrgId = {:libraryOrgId}");
assert.deepStrictEqual(staffWorkflowTamperedScope.params, { libraryOrgId: "10" });

const superWorkflowAllScope = staffRoutes.titleRequestListScope(appWithNoTags(), staff("super_admin", ""), "all");
assert.strictEqual(superWorkflowAllScope.mode, "all");
assert.strictEqual(superWorkflowAllScope.filter, "id != ''");

const superWorkflowLibraryScope = staffRoutes.titleRequestListScope(appWithNoTags(), staff("super_admin", ""), "20");
assert.strictEqual(superWorkflowLibraryScope.mode, "library");
assert.strictEqual(superWorkflowLibraryScope.libraryOrgId, "20");
assert.strictEqual(superWorkflowLibraryScope.filter, "libraryOrgId = {:libraryOrgId}");

const holdTimes = staffRoutes.loadFirstHoldPlacedEventTimes(appWithEvents(), rows.filter(row => row.get("libraryOrgId") === "10"));
assert.strictEqual(holdTimes.r4, "2026-04-06 10:00:00");
assert.strictEqual(holdTimes.r5, "2026-03-10 10:00:00");
assert.strictEqual(holdTimes.r6, "2026-04-11 10:00:00");

const summary = staffRoutes.loadAnalyticsSummary(libraryScope, range, rows.filter(row => row.get("libraryOrgId") === "10"), holdTimes);
assert.strictEqual(summary.newSuggestions, 3);
assert.strictEqual(summary.openRequests, 3);
assert.strictEqual(summary.closedRequests, 1);
assert.strictEqual(summary.heldRequests, 2);
assert.strictEqual(Math.round(summary.averageDaysToHold), 3);

const stages = staffRoutes.loadStageCounts(libraryScope, rows.filter(row => row.get("libraryOrgId") === "10"));
assert.strictEqual(stages.suggestion, 1);
assert.strictEqual(stages.outstanding_purchase, 1);
assert.strictEqual(stages.hold_placed, 1);
assert.strictEqual(stages.closed, 2);

const reasons = staffRoutes.loadClosedReasonBreakdown(libraryScope, range, rows.filter(row => row.get("libraryOrgId") === "10"));
assert.deepStrictEqual(reasons, [{ reason: "rejected", count: 1 }]);

const aging = staffRoutes.loadAgingMetrics(libraryScope, rows.filter(row => row.get("libraryOrgId") === "10"), new Date("2026-05-01T00:00:00Z"));
assert.strictEqual(aging.openOlderThanThreshold, 1);
assert.strictEqual(aging.averageAgeByStage.find(row => row.status === "outstanding_purchase").count, 1);

const exceptions = staffRoutes.loadExceptionCounts(appWithNoTags(), superAllScope, range, rows);
assert.strictEqual(exceptions.identifierFailures, 1);

console.log("Staff analytics tests passed.");
