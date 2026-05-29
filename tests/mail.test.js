const assert = require("assert");
const { MockRecord, createMockApp, interceptRequire } = require("./helpers/mock_pb.js");

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

// Use standard mock interceptor
interceptRequire({
  "lib/config.js": mockConfig,
  "lib/polaris.js": mockPolaris
});

const mail = require("../lib/mail.js");

// Mock PocketBase app using our unified builder
let sentMessages = [];
let savedRecords = [];
const mockApp = createMockApp({
  onSave: function(record) {
    savedRecords.push(record);
  },
  onMailSend: function(message) {
    sentMessages.push(message);
  }
});
mockApp.settings = function() {
  return { meta: { senderAddress: "default@library.org", senderName: "Default Library" } };
};

// Mock MailerMessage globally
global.MailerMessage = class MailerMessage {
  constructor(data) {
    Object.assign(this, data);
  }
};

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

  // Test sendAssignmentNotification with HTML escaping
  sentMessages = [];
  const assignee = new MockRecord({
    username: "assignee",
    displayName: "Jane Assignee",
    email: "assignee@example.com"
  });
  const actor = new MockRecord({
    username: "actor",
    displayName: "John Actor <script>alert(1)</script>"
  });
  const complexRecord = new MockRecord({
    title: "Title & More <img src=x onerror=alert(2)>",
    author: "Author ' Quote",
    format: "book"
  });

  mail.sendAssignmentNotification(mockApp, assignee, complexRecord, actor);
  assert.strictEqual(sentMessages.length, 1);
  assert.strictEqual(sentMessages[0].subject, "Assigned suggestion: Title & More <img src=x onerror=alert(2)>");
  assert.ok(sentMessages[0].html.includes("Hello Jane Assignee"), "HTML should include recipient name");
  assert.ok(sentMessages[0].html.includes("John Actor &lt;script&gt;alert(1)&lt;/script&gt;"), "HTML should escape actor name");
  assert.ok(sentMessages[0].html.includes("Title &amp; More &lt;img src=x onerror=alert(2)&gt;"), "HTML should escape title");
  assert.ok(sentMessages[0].html.includes("Author &#39; Quote"), "HTML should escape author quotes");
  assert.ok(sentMessages[0].text.includes("John Actor <script>alert(1)</script>"), "Plaintext should not escape actor name");

  // Test sendAssignmentNotification for additional copy
  sentMessages = [];
  mail.sendAssignmentNotification(mockApp, assignee, complexRecord, actor, { type: "additional_copy" });
  assert.strictEqual(sentMessages.length, 1);
  assert.strictEqual(sentMessages[0].subject, "Assigned additional-copy task: Title & More <img src=x onerror=alert(2)>");
  assert.ok(sentMessages[0].html.includes("assigned an open additional-copy task to you"), "HTML should specify additional-copy type");

  console.log("All mail.js tests passed!");
}

runTests();
