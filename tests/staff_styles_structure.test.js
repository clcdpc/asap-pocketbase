const assert = require('assert');
const fs = require('fs');
const path = require('path');

const cssPath = path.resolve(__dirname, '../pb_public/staff/styles.css');
const css = fs.readFileSync(cssPath, 'utf8');

function assertBalancedBraces(source) {
  let depth = 0;
  source.split('\n').forEach((line, index) => {
    for (const char of line) {
      if (char === '{') depth++;
      if (char === '}') depth--;
      assert.ok(depth >= 0, `Unexpected closing brace at line ${index + 1}`);
    }
  });
  assert.strictEqual(depth, 0, 'CSS braces should be balanced');
}

function assertNoDuplicateDeclarations(source) {
  const stack = [];
  const failures = [];
  source.split('\n').forEach((rawLine, index) => {
    const line = rawLine.replace(/\/\*.*?\*\//g, '').trim();
    if (!line) return;

    if (line.endsWith('{')) {
      stack.push({ selector: line.slice(0, -1).trim(), declarations: new Map() });
      return;
    }

    if (line === '}') {
      stack.pop();
      return;
    }

    const current = stack[stack.length - 1];
    if (!current || current.selector.startsWith('@') || !line.endsWith(';') || !line.includes(':')) return;

    const declaration = line.slice(0, -1).trim();
    if (current.declarations.has(declaration)) {
      failures.push(`${current.selector} repeats "${declaration}" at line ${index + 1}`);
    }
    current.declarations.set(declaration, index + 1);
  });
  assert.deepStrictEqual(failures, []);
}

console.log('Running staff stylesheet structure tests...');

assert.ok(css.includes('Staff Stylesheet Map'), 'Stylesheet should keep a top-level map comment');

[
  '/* 1. Design Tokens */',
  '/* 4. Grid Controls */',
  '/* 7. Staff Grid */',
  '/* 9. Settings Layout */',
  '/* 14. Dialog Shells */',
  '/* 20. Searchable Cells and Polaris Search */',
  '/* 24. Focus and Hardening */'
].forEach(header => {
  assert.ok(css.includes(header), `Missing stylesheet section header: ${header}`);
});

[
  '#grid-container .gridjs-td:has(.searchable-cell)',
  '.settings-panel-locked .btn:not(.settings-nav-link):not(.system-only-switch-link)',
  '.asap-dialog-polaris-search',
  '.polaris-search-result-actions',
  '.asap-accordion',
  '.btn-outline-warning'
].forEach(selector => {
  assert.ok(css.includes(selector), `Missing critical selector: ${selector}`);
});

assertBalancedBraces(css);
assert.ok(!/}\.[A-Za-z0-9_-]/.test(css), 'Adjacent rule braces should be split onto separate lines');
assertNoDuplicateDeclarations(css);

console.log('Staff stylesheet structure tests passed.');
