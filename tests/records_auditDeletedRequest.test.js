const assert = require('assert');

// Mock __hooks globally
global.__hooks = __dirname + "/../pb_hooks";

// Intercept requires
const Module = require('module');
const originalRequire = Module.prototype.require;

Module.prototype.require = function(moduleName) {
  if (moduleName.includes("lib/config.js")) return {};
  if (moduleName.includes("lib/identity.js")) return {};
  return originalRequire.apply(this, arguments);
};

class MockRecord {
  constructor(collection, initial = {}) {
    this.collection = collection;
    this.data = { ...initial };
    this.id = initial.id || "rec_" + Math.random().toString(16).slice(2);
    // Explicitly copy created and updated to object root for fallback tests
    if (initial.created) this.created = initial.created;
    if (initial.updated) this.updated = initial.updated;
  }
  get(key) {
    return this.data[key];
  }
  set(key, value) {
    this.data[key] = value;
  }
  email() {
    return this.data.email || "";
  }
  getBool(key) {
    return !!this.data[key];
  }
}

global.Record = MockRecord;

const { auditDeletedRequest } = require('../pb_hooks/lib/records.js');

let savedRecords = [];
const mockApp = {
  findCollectionByNameOrId: function(name) {
    return { name };
  },
  save: function(record) {
    savedRecords.push(record);
  }
};

let passed = 0;
let failed = 0;

function runTest(name, setup, run, verify) {
  try {
    savedRecords = [];
    setup();
    run();
    verify();
    console.log(`✅ Test passed: ${name}`);
    passed++;
  } catch (err) {
    console.error(`❌ Test failed: ${name}`);
    console.error(err);
    failed++;
  }
}

console.log('Running tests for auditDeletedRequest in pb_hooks/lib/records.js...');

runTest('Fully populated record with explicit mode',
  () => {},
  () => {
    const record = new MockRecord('title_requests', {
      id: 'req_123',
      title: 'The Great Gatsby',
      author: 'F. Scott Fitzgerald',
      identifier: '9780743273565',
      bibid: 'bib_1',
      barcode: '123456789',
      email: 'patron@example.com',
      nameFirst: 'John',
      nameLast: 'Doe',
      libraryOrgId: 'org_1',
      libraryOrgName: 'Main Library',
      status: 'pending',
      closeReason: 'Not needed',
      notes: 'Some notes',
      created: '2023-01-01T00:00:00Z',
      updated: '2023-01-02T00:00:00Z'
    });

    const staffUser = new MockRecord('users', {
      id: 'staff_1',
      username: 'jdoe',
      role: 'admin'
    });

    auditDeletedRequest(mockApp, record, staffUser, 'bulk');
  },
  () => {
    assert.strictEqual(savedRecords.length, 1);
    const audit = savedRecords[0];
    assert.strictEqual(audit.collection.name, 'deleted_request_audit');
    assert.strictEqual(audit.get('titleRequestId'), 'req_123');
    assert.strictEqual(audit.get('title'), 'The Great Gatsby');
    assert.strictEqual(audit.get('author'), 'F. Scott Fitzgerald');
    assert.strictEqual(audit.get('identifier'), '9780743273565');
    assert.strictEqual(audit.get('bibid'), 'bib_1');
    assert.strictEqual(audit.get('barcode'), '123456789');
    assert.strictEqual(audit.get('libraryOrgId'), 'org_1');
    assert.strictEqual(audit.get('libraryOrgName'), 'Main Library');
    assert.strictEqual(audit.get('status'), 'pending');
    assert.strictEqual(audit.get('closeReason'), 'Not needed');
    assert.strictEqual(audit.get('deletedByStaff'), 'staff_1');
    assert.strictEqual(audit.get('deletedByUsername'), 'jdoe');
    assert.strictEqual(audit.get('deletedByRole'), 'admin');
    assert.strictEqual(audit.get('deleteMode'), 'bulk');
    assert.ok(audit.get('deletedAt').length > 0);

    const snapshot = audit.get('snapshot');
    assert.strictEqual(snapshot.id, 'req_123');
    assert.strictEqual(snapshot.email, 'patron@example.com');
    assert.strictEqual(snapshot.nameFirst, 'John');
    assert.strictEqual(snapshot.nameLast, 'Doe');
    assert.strictEqual(snapshot.notes, 'Some notes');
    assert.strictEqual(snapshot.created, '2023-01-01T00:00:00Z');
    assert.strictEqual(snapshot.updated, '2023-01-02T00:00:00Z');
  }
);

runTest('Missing fields and default mode',
  () => {},
  () => {
    const record = new MockRecord('title_requests', {
      id: 'req_456'
    });

    const staffUser = new MockRecord('users', {
      id: 'staff_2'
    });

    auditDeletedRequest(mockApp, record, staffUser); // No mode
  },
  () => {
    assert.strictEqual(savedRecords.length, 1);
    const audit = savedRecords[0];
    assert.strictEqual(audit.get('titleRequestId'), 'req_456');
    assert.strictEqual(audit.get('title'), '');
    assert.strictEqual(audit.get('author'), '');
    assert.strictEqual(audit.get('identifier'), '');
    assert.strictEqual(audit.get('bibid'), '');
    assert.strictEqual(audit.get('barcode'), '');
    assert.strictEqual(audit.get('libraryOrgId'), '');
    assert.strictEqual(audit.get('libraryOrgName'), '');
    assert.strictEqual(audit.get('status'), '');
    assert.strictEqual(audit.get('closeReason'), '');
    assert.strictEqual(audit.get('deletedByStaff'), 'staff_2');
    assert.strictEqual(audit.get('deletedByUsername'), '');
    assert.strictEqual(audit.get('deletedByRole'), '');
    assert.strictEqual(audit.get('deleteMode'), 'single'); // Default mode

    const snapshot = audit.get('snapshot');
    assert.strictEqual(snapshot.id, 'req_456');
    assert.strictEqual(snapshot.email, '');
    assert.strictEqual(snapshot.nameFirst, '');
    assert.strictEqual(snapshot.nameLast, '');
    assert.strictEqual(snapshot.notes, '');
    assert.strictEqual(snapshot.created, '');
    assert.strictEqual(snapshot.updated, '');
  }
);

runTest('Fallback to created/updated properties on record root',
  () => {},
  () => {
    // Record with created/updated ONLY at the root, not in this.data
    const record = new MockRecord('title_requests', { id: 'req_789' });
    record.data = { id: 'req_789' }; // Clear data
    record.created = 'root_created';
    record.updated = 'root_updated';

    const staffUser = new MockRecord('users', { id: 'staff_3' });

    auditDeletedRequest(mockApp, record, staffUser);
  },
  () => {
    assert.strictEqual(savedRecords.length, 1);
    const audit = savedRecords[0];
    const snapshot = audit.get('snapshot');
    assert.strictEqual(snapshot.created, 'root_created');
    assert.strictEqual(snapshot.updated, 'root_updated');
  }
);

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
