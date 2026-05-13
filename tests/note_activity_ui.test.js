const assert = require('assert');
const fs = require('fs');
const path = require('path');

class TestNode {
  constructor(tagName = '') {
    this.tagName = tagName;
    this.className = '';
    this.children = [];
    this.parent = null;
    this._textContent = '';
  }

  set textContent(value) {
    this._textContent = String(value || '');
    this.children = [];
  }

  get textContent() {
    return this._textContent + this.children.map(child => child.textContent).join('');
  }

  append(...nodes) {
    nodes.forEach(node => {
      if (!node) return;
      node.parent = this;
      this.children.push(node);
    });
  }

  replaceChildren(...nodes) {
    this.children = [];
    this._textContent = '';
    this.append(...nodes);
  }

  addEventListener() {}

  remove() {
    if (!this.parent) return;
    this.parent.children = this.parent.children.filter(child => child !== this);
  }

  matchesClass(selector) {
    return selector.startsWith('.') && this.className.split(/\s+/).includes(selector.slice(1));
  }

  querySelectorAll(selector) {
    const matches = [];
    const visit = node => {
      if (node.matchesClass && node.matchesClass(selector)) matches.push(node);
      node.children.forEach(visit);
    };
    visit(this);
    return matches;
  }

  querySelector(selector) {
    return this.querySelectorAll(selector)[0] || null;
  }
}

class TestDocumentFragment extends TestNode {}

function installTestDocument() {
  global.document = {
    createElement(tagName) {
      return new TestNode(tagName);
    },
    createDocumentFragment() {
      return new TestDocumentFragment();
    }
  };
}

function loadNoteActivityModule() {
  const sourcePath = path.resolve(__dirname, '../pb_public/staff/js/note-activity.js');
  const source = fs.readFileSync(sourcePath, 'utf8')
    .replace(/export function /g, 'function ');

  installTestDocument();

  return new Function(`
    ${source}
    return {
      parseNoteLine,
      parseNoteActivity,
      groupNoteActivity,
      renderNoteActivity
    };
  `)();
}

function runTests() {
  const {
    parseNoteLine,
    parseNoteActivity,
    groupNoteActivity,
    renderNoteActivity
  } = loadNoteActivityModule();

  const skip = parseNoteLine('5/13/2026 to 5/13/2026 (Count: 5) SKIP: No items are available to fill your request. Contact the library for assistance.');
  assert.strictEqual(skip.type, 'Skip');
  assert.strictEqual(skip.count, 5);
  assert.strictEqual(skip.groupDate, '5/13/2026');
  assert.strictEqual(skip.message, 'No items are available to fill your request. Contact the library for assistance.');

  const status = parseNoteLine('5/13/2026 to 5/13/2026 (Count: 1) Moved from Suggestions to Pending Purchase by wosborn.');
  assert.strictEqual(status.type, 'Status');
  assert.strictEqual(status.user, 'wosborn');
  assert.strictEqual(status.message, 'Moved from Suggestions to Pending Purchase');

  const polaris = parseNoteLine('5/13/2026 to 5/13/2026 (Count: 1) Identifier number verification completed: no Polaris bibliographic match found.');
  assert.strictEqual(polaris.type, 'Polaris');
  assert.strictEqual(polaris.user, '');

  const legacy = parseNoteLine('5/12/2026 Created on behalf of patron by wosborn.');
  assert.strictEqual(legacy.type, 'Created');
  assert.strictEqual(legacy.startDate, '5/12/2026');
  assert.strictEqual(legacy.user, 'wosborn');

  const raw = parseNoteLine('Patron prefers the downtown branch.');
  assert.strictEqual(raw.type, 'Note');
  assert.strictEqual(raw.groupDate, 'Undated');

  const notes = [
    '5/13/2026 to 5/13/2026 (Count: 5) SKIP: No items are available to fill your request.',
    '5/13/2026 to 5/13/2026 (Count: 1) Moved to Pending hold because a manual BIB ID was found.',
    '5/12/2026 Created on behalf of patron by wosborn.'
  ].join('\n');
  const events = parseNoteActivity(notes);
  assert.strictEqual(events.length, 3);
  const groups = groupNoteActivity(events);
  assert.strictEqual(groups.length, 2);
  assert.strictEqual(groups[0].date, '5/13/2026');
  assert.strictEqual(groups[0].events.length, 2);

  const rendered = renderNoteActivity(notes);
  assert.strictEqual(rendered.querySelectorAll('.note-activity-item').length, 3);
  assert.strictEqual(rendered.querySelector('.note-activity-date').textContent, '5/13/2026');
  assert.strictEqual(rendered.querySelector('.note-activity-badge--skip').textContent, 'Skip');
  assert.ok(rendered.textContent.includes('Count 5'));

  const unsafe = renderNoteActivity('<img src=x onerror=alert(1)>');
  assert.strictEqual(unsafe.querySelector('img'), null);
  assert.ok(unsafe.textContent.includes('<img src=x onerror=alert(1)>'));

  const gridJs = fs.readFileSync(path.resolve(__dirname, '../pb_public/staff/js/grid.js'), 'utf8');
  assert.ok(gridJs.includes("from './note-activity.js'"));
  assert.ok(gridJs.includes('data-note-record-id'));
  assert.ok(!gridJs.includes('data-full-note'));
  assert.ok(gridJs.includes('content.replaceChildren(renderNoteActivity(row.notes))'));

  const html = fs.readFileSync(path.resolve(__dirname, '../pb_public/staff/index.html'), 'utf8');
  assert.ok(html.includes('Notes &amp; activity'));

  console.log('Note activity UI tests passed.');
}

runTests();
