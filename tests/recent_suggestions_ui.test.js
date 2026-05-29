const assert = require('assert');
const path = require('path');
const fs = require('fs');

global.__hooks = path.resolve(__dirname, "../pb_hooks");

// Mock DOM manually
global.localStorage = {
  store: {},
  getItem(key) { return this.store[key] || null; },
  setItem(key, value) { this.store[key] = value; },
  removeItem(key) { delete this.store[key]; },
  clear() { this.store = {}; }
};

class MockElement {
  constructor(tag) {
    this.tag = tag;
    this.className = '';
    this.textContent = '';
    this.innerHTML = '';
    this.href = '';
    this.children = [];
    this.listeners = {};
    this.classList = {
      classes: new Set(),
      add: (c) => this.classList.classes.add(c),
      remove: (c) => this.classList.classes.delete(c),
      contains: (c) => this.classList.classes.has(c)
    };
  }
  appendChild(child) { this.children.push(child); }
  replaceChildren() { this.children = []; }
  addEventListener(event, handler) { this.listeners[event] = handler; }
  dispatchEvent(e) { 
    if (this.listeners[e.type]) {
      this.listeners[e.type](e);
    }
  }
}

global.document = {
  elements: {
    'recent-suggestions-menu': new MockElement('div')
  },
  getElementById(id) { return this.elements[id] || null; },
  createElement(tag) { return new MockElement(tag); },
  listeners: {},
  addEventListener(event, handler) { this.listeners[event] = handler; },
  dispatchEvent(e) {
    if (this.listeners[e.type]) {
      this.listeners[e.type](e);
    }
  }
};

global.CustomEvent = class {
  constructor(type, options) {
    this.type = type;
    this.detail = options ? options.detail : null;
  }
};

global.MouseEvent = class {
  constructor(type) {
    this.type = type;
    this.preventDefault = () => {};
  }
};

// Mock modules
const mockPb = {
  authStore: {
    model: { id: "user123" }
  }
};

const mockApi = {
  workflowStatusLabel(status) {
    if (status === 'pending_hold') return 'Pending hold';
    return status;
  }
};

// We need to bypass ES modules to test the logic. Let's just read the file and eval it or use a simple regex to replace imports
const fileStr = fs.readFileSync(path.resolve(__dirname, "../pb_public/staff/js/recent-suggestions.js"), "utf8");
const transformed = fileStr
  .replace(/export /g, "")
  .replace(/import \{.*?\} from '.*?';/g, "");

// Eval in current context
eval(transformed);

// Override the internal dependencies that were imported
const pb = mockPb;
const workflowStatusLabel = mockApi.workflowStatusLabel;

function testRecentSuggestions() {
  console.log("Running recent suggestions UI tests...");

  // 1. Initial state
  global.localStorage.clear();
  let suggestions = getRecentSuggestions();
  assert.strictEqual(suggestions.length, 0);

  // 2. Remember a suggestion
  rememberRecentSuggestion({ id: "req1", type: "title_request", title: "Book A", author: "Author A", status: "suggestion" });
  suggestions = getRecentSuggestions();
  assert.strictEqual(suggestions.length, 1);
  assert.strictEqual(suggestions[0].id, "req1");

  // 3. Update same suggestion moves it to top
  rememberRecentSuggestion({ id: "req2", type: "title_request", title: "Book B", author: "Author B", status: "pending_hold" });
  rememberRecentSuggestion({ id: "req1", type: "title_request", title: "Book A", author: "Author A", status: "outstanding_purchase" });
  
  suggestions = getRecentSuggestions();
  assert.strictEqual(suggestions.length, 2);
  assert.strictEqual(suggestions[0].id, "req1");
  assert.strictEqual(suggestions[0].status, "outstanding_purchase");
  assert.strictEqual(suggestions[1].id, "req2");

  // 4. More than 5 items trims to 5
  rememberRecentSuggestion({ id: "req3" });
  rememberRecentSuggestion({ id: "req4" });
  rememberRecentSuggestion({ id: "req5" });
  rememberRecentSuggestion({ id: "req6" });

  suggestions = getRecentSuggestions();
  assert.strictEqual(suggestions.length, 5);
  assert.strictEqual(suggestions[0].id, "req6");
  assert.strictEqual(suggestions[4].id, "req1"); // req2 was pushed out

  // 5. Ignore missing id or additional_copy
  rememberRecentSuggestion({ title: "No ID" });
  rememberRecentSuggestion({ id: "req7", type: "additional_copy" });
  assert.strictEqual(getRecentSuggestions().length, 5);

  // 6. Scoped by user ID
  pb.authStore.model.id = "user456";
  assert.strictEqual(getRecentSuggestions().length, 0);
  rememberRecentSuggestion({ id: "req99" });
  assert.strictEqual(getRecentSuggestions().length, 1);
  
  pb.authStore.model.id = "user123";
  assert.strictEqual(getRecentSuggestions().length, 5);

  // 7. Render Switcher
  renderRecentSuggestionsSwitcher();
  const menu = document.getElementById("recent-suggestions-menu");
  assert.strictEqual(menu.children.length, 5);
  
  const firstItem = menu.children[0];
  assert.ok(firstItem.innerHTML.includes("Unknown Title")); // fallback for missing title is Unknown Title
  
  // 8. Event dispatching
  let eventDispatched = false;
  document.addEventListener("asap:recent-suggestion-selected", (e) => {
    eventDispatched = true;
    assert.strictEqual(e.detail.id, "req6");
  });
  
  firstItem.dispatchEvent(new global.MouseEvent("click"));
  assert.strictEqual(eventDispatched, true);

  // 9. Update existing record
  global.localStorage.clear();
  rememberRecentSuggestion({ id: "req1", title: "Old Title", author: "Old Author" });
  if (typeof updateRecentSuggestion === 'function') {
      updateRecentSuggestion({ id: "req1", title: "New Title", author: "New Author" });
      suggestions = getRecentSuggestions();
      assert.strictEqual(suggestions.length, 1);
      assert.strictEqual(suggestions[0].title, "New Title");
      assert.strictEqual(suggestions[0].author, "New Author");
  }

  console.log("recent_suggestions_ui.test.js passed.");
}

testRecentSuggestions();
