const assert = require('assert');
const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '../pb_public/staff/js');

function walk(dir) {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap(entry => {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) return walk(full);
    return entry.isFile() && entry.name.endsWith('.js') ? [full] : [];
  });
}

function previousLineAllowsStaticHtml(lines, index) {
  const prev = lines[index - 1] || '';
  return prev.includes('dom-safe-static-html: static developer-authored markup only');
}

function collectInnerHtmlAssignments(source) {
  const assignments = [];
  const pattern = /\.innerHTML\s*(\+=|=)\s*/g;
  let match;

  while ((match = pattern.exec(source))) {
    const start = match.index;
    let cursor = pattern.lastIndex;
    let rhs = '';
    let quote = '';
    let escaped = false;
    let depth = 0;
    let inLineComment = false;
    let inBlockComment = false;

    while (cursor < source.length) {
      const ch = source[cursor];
      const next = source[cursor + 1];
      rhs += ch;

      if (inLineComment) {
        if (ch === '\n') inLineComment = false;
      } else if (inBlockComment) {
        if (ch === '*' && next === '/') {
          rhs += next;
          cursor++;
          inBlockComment = false;
        }
      } else if (quote) {
        if (escaped) {
          escaped = false;
        } else if (ch === '\\') {
          escaped = true;
        } else if (ch === quote) {
          quote = '';
        }
      } else {
        if (ch === '/' && next === '/') {
          rhs += next;
          cursor++;
          inLineComment = true;
        } else if (ch === '/' && next === '*') {
          rhs += next;
          cursor++;
          inBlockComment = true;
        } else if (ch === '"' || ch === "'" || ch === '`') {
          quote = ch;
        } else if (ch === '(' || ch === '[' || ch === '{') {
          depth++;
        } else if (ch === ')' || ch === ']' || ch === '}') {
          if (depth > 0) depth--;
        } else if (ch === ';' && depth === 0) {
          break;
        }
      }

      cursor++;
    }

    assignments.push({ start, rhs });
  }

  return assignments;
}

function isUnsafeInnerHtmlAssignment(rhs) {
  const trimmed = rhs.trim().replace(/;\s*$/, '').trim();
  if (!trimmed) return false;

  // Static literals are fine when explicitly allowed by the nearby comment.
  if (/^[`'"][\s\S]*[`'"]$/.test(trimmed) && !trimmed.includes('${')) return false;

  const hasSanitizer = /escapeAttr\s*\(|escapeHtml\s*\(|sanitizeHtml\s*\(/.test(trimmed);
  if (hasSanitizer) return false;

  if (trimmed.includes('${')) return true;
  if (trimmed.includes('+')) return true;

  const dynamicTokens = [
    'err', 'error', 'data', 'row', 'message', 'label',
    'title', 'value', 'status', 'response', 'result'
  ];

  return dynamicTokens.some(token => new RegExp(`\\b${token}\\b`).test(trimmed));
}

function isUnsafeInsertAdjacentHtml(line) {
  return line.includes('.insertAdjacentHTML(');
}

const violations = [];

for (const file of walk(root)) {
  const rel = path.relative(path.resolve(__dirname, '..'), file);
  const source = fs.readFileSync(file, 'utf8');
  const lines = source.split(/\r?\n/);

  for (const assignment of collectInnerHtmlAssignments(source)) {
    const lineNumber = source.slice(0, assignment.start).split(/\r?\n/).length;
    const allowedStatic = previousLineAllowsStaticHtml(lines, lineNumber - 1);
    if (isUnsafeInnerHtmlAssignment(assignment.rhs) && !allowedStatic) {
      violations.push(`${rel}:${lineNumber}: unsafe dynamic innerHTML usage`);
    }
  }

  lines.forEach((line, index) => {
    const allowedStatic = previousLineAllowsStaticHtml(lines, index);

    if (isUnsafeInsertAdjacentHtml(line) && !allowedStatic) {
      violations.push(`${rel}:${index + 1}: unsafe insertAdjacentHTML usage`);
    }
  });
}

assert.strictEqual(
  violations.length,
  0,
  [
    'Unsafe dynamic HTML construction detected.',
    'Use createElement/appendChild/replaceChildren and assign dynamic text with textContent.',
    '',
    ...violations
  ].join('\n')
);
