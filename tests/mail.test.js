const assert = require("assert");

// Mock __hooks globally
global.__hooks = __dirname + "/../pb_hooks";

// Mock configuration
const mockConfig = {
  librarySettings: function(app, libraryOrgId) {
    return {
      emails: {
        fromAddress: "test@library.org",
        fromName: "Test Library",
        suggestion_submitted: {
          subject: "Subject: {{title}} by {{author}}",
          body: "Hello {{firstName}} {{lastName}},\n\nYour suggestion for {{title}} was submitted."
        },
        already_owned: {
          subject: "Already Owned: {{title}}",
          body: "We already own {{title}}."
        },
        rejected: {
          subject: "Rejected: {{title}}",
          body: "Your suggestion for {{title}} was rejected."
        },
        hold_placed: {
          subject: "Hold Placed: {{title}}",
          body: "A hold was placed on {{title}}. Barcode: {{barcode}}"
        },
        rejection_templates: [
          {
            id: "tmpl_123",
            subject: "Special Rejection: {{title}}",
            body: "Special reason for rejecting {{title}}."
          }
        ]
      }
    };
  },
  mail: function() {
    return {
      host: "smtp.library.org",
      port: 587,
      username: "",
      password: "",
      tls: true
    };
  },
  uiText: function() {
    return {
      formatLabels: {
        book: "Book"
      }
    };
  },
  mail: function() {
    return {
      host: "smtp.test",
      port: 587,
      username: "",
      password: "",
      tls: true
    };
  }
};

let mockPolarisEmail = "";
const mockPolaris = {
  adminStaffAuth: function() {
    return { AccessToken: "staff-token" };
  },
  lookupPatron: function() {
    return { EmailAddress: mockPolarisEmail };
  }
};

// Override require to intercept config.js
const Module = require('module');
const originalRequire = Module.prototype.require;
Module.prototype.require = function(moduleName) {
  if (moduleName.includes("lib/config.js")) {
    return mockConfig;
  }
  if (moduleName.includes("lib/polaris.js")) {
    return mockPolaris;
  }
  return originalRequire.apply(this, arguments);
};

const mail = require("../pb_hooks/lib/mail.js");

// Mock PocketBase app
let sentMessages = [];
let savedRecords = [];
const mockApp = {
  settings: function() {
    return { meta: { senderAddress: "default@library.org", senderName: "Default Library" } };
  },
  logger: function() {
    return { warn: function() {} };
  },
  save: function(record) {
    savedRecords.push(record);
  },
  newMailClient: function() {
    return {
      send: function(message) {
        sentMessages.push(message);
      }
    };
  }
};

// Mock MailerMessage globally
global.MailerMessage = class MailerMessage {
  constructor(data) {
    Object.assign(this, data);
  }
};

// Mock Record
class MockRecord {
  constructor(data) {
    this.data = data;
  }
  get(key) {
    return this.data[key];
  }
  set(key, value) {
    this.data[key] = value;
  }
}

function runTests() {
  console.log("Running mail.js tests...");

  // Test suggestionSubmitted
  sentMessages = [];
  let record = new MockRecord({
    nameFirst: "John",
    nameLast: "Doe",
    title: " The Great Gatsby ",
    author: "F. Scott Fitzgerald (1896-1940)",
    format: "book",
    email: "john.doe@example.com",
    libraryOrgId: "org1"
  });

  mail.suggestionSubmitted(mockApp, record);
  assert.strictEqual(sentMessages.length, 1);
  assert.strictEqual(sentMessages[0].subject, "Subject: The Great Gatsby by F. Scott Fitzgerald");
  assert.strictEqual(sentMessages[0].text, "Hello John Doe,\n\nYour suggestion for The Great Gatsby was submitted.");
  assert.strictEqual(sentMessages[0].html, "Hello John Doe,<br><br>Your suggestion for The Great Gatsby was submitted.");
  assert.strictEqual(sentMessages[0].to[0].address, "john.doe@example.com");

  // Test alreadyOwned
  sentMessages = [];
  let patron = { NameFirst: "Jane", NameLast: "Smith", EmailAddress: "jane.smith@example.com" };
  mail.alreadyOwned(mockApp, record, patron);
  assert.strictEqual(sentMessages.length, 1);
  assert.strictEqual(sentMessages[0].subject, "Already Owned: The Great Gatsby");
  assert.strictEqual(sentMessages[0].to[0].address, "john.doe@example.com");

  // Test rejected with default template
  sentMessages = [];
  mail.rejected(mockApp, record, patron);
  assert.strictEqual(sentMessages.length, 1);
  assert.strictEqual(sentMessages[0].subject, "Rejected: The Great Gatsby");

  // Test rejected with specific template
  sentMessages = [];
  mail.rejected(mockApp, record, patron, "tmpl_123");
  assert.strictEqual(sentMessages.length, 1);
  assert.strictEqual(sentMessages[0].subject, "Special Rejection: The Great Gatsby");

  // Test holdPlaced
  sentMessages = [];
  let recordWithBarcode = new MockRecord({
    title: "1984",
    barcode: "123456789",
    format: "book",
    email: "test@example.com"
  });
  mail.holdPlaced(mockApp, recordWithBarcode, null);
  assert.strictEqual(sentMessages.length, 1);
  assert.strictEqual(sentMessages[0].subject, "Hold Placed: 1984");
  assert.strictEqual(sentMessages[0].text, "A hold was placed on 1984. Barcode: 123456789");

  // Test Polaris email refresh before workflow email
  sentMessages = [];
  savedRecords = [];
  mockPolarisEmail = "current@example.com";
  mail.holdPlaced(mockApp, recordWithBarcode, null);
  assert.strictEqual(sentMessages.length, 1);
  assert.strictEqual(sentMessages[0].to[0].address, "current@example.com");
  assert.strictEqual(recordWithBarcode.get("email"), "current@example.com");
  assert.strictEqual(savedRecords.length, 1);
  mockPolarisEmail = "";

  // Test autoRejected
  sentMessages = [];
  mail.autoRejected(mockApp, record);
  assert.strictEqual(sentMessages.length, 1);
  assert.strictEqual(sentMessages[0].subject, "Rejected: The Great Gatsby");
  assert.strictEqual(sentMessages[0].to[0].address, "john.doe@example.com");

  // Test staff purchase reminder
  sentMessages = [];
  let staff = new MockRecord({
    username: "selector",
    displayName: "Collection Selector"
  });
  let purchaseRecord = new MockRecord({
    title: "Future Classic",
    author: "A. Writer",
    identifier: "9781234567890",
    format: "book",
    agegroup: "Adult",
    publication: "Coming soon",
    exactPublicationDate: "2026-06-01",
    bibid: "456789",
    notes: "Order for downtown branch."
  });
  mail.purchaseReminder(mockApp, purchaseRecord, staff, "selector@example.com", "https://asap.example.org/staff/?stage=outstanding_purchase&request=abc");
  assert.strictEqual(sentMessages.length, 1);
  assert.strictEqual(sentMessages[0].subject, "Purchase reminder: Future Classic");
  assert.strictEqual(sentMessages[0].to[0].address, "selector@example.com");
  assert.ok(sentMessages[0].text.includes("Title: Future Classic"));
  assert.ok(sentMessages[0].text.includes("Staff member: Collection Selector"));
  assert.ok(sentMessages[0].text.includes("Open in ASAP: https://asap.example.org/staff/?stage=outstanding_purchase&request=abc"));

  console.log("All mail.js tests passed!");
}

runTests();
