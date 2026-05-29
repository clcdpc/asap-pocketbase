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

function isUnsafeInnerHtmlLine(line) {
  if (!line.includes('.innerHTML')) return false;
  // This regex matches assignments like ` = ` or ` += `
  if (!/innerHTML\s*[+]?=/.test(line)) return false;

  const rhs = line.split(/innerHTML\s*[+]?=/)[1] || '';

  // Skip lines that are just static strings or empty
  if (/^\s*['"`]<\w+[^>]*>[^<]*<\/\w+>['"`]\s*;?\s*$/.test(rhs)) return false;
  if (/^\s*['"`]['"`]\s*;?\s*$/.test(rhs)) return false;

  if (rhs.includes('${')) return true;
  if (rhs.includes('+')) return true;
  if (rhs.includes('escapeHtml(')) return true;

  const dynamicTokens = [
    'err', 'error', 'data', 'row', 'message', 'label',
    'title', 'value', 'status', 'response', 'result'
  ];

  return dynamicTokens.some(token => new RegExp(`\\b${token}\\b`).test(rhs));
}

function isUnsafeInsertAdjacentHtml(line) {
  return line.includes('.insertAdjacentHTML(');
}

const violations = [];

for (const file of walk(root)) {
  const rel = path.relative(path.resolve(__dirname, '..'), file);
  const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/);

  lines.forEach((line, index) => {
    const allowedStatic = previousLineAllowsStaticHtml(lines, index);

    if (isUnsafeInnerHtmlLine(line) && !allowedStatic) {
      violations.push(`${rel}:${index + 1}: unsafe dynamic innerHTML usage`);
    }

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
