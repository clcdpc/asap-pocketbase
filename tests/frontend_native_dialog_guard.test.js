const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const root = path.join(__dirname, '../src/Asap.Web/Frontend');
const failures = [];
let files = 0;
function inspect(directory) {
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    if (entry.name === 'vendor') continue;
    const filename = path.join(directory, entry.name);
    if (entry.isDirectory()) { inspect(filename); continue; }
    if (!/\.(?:js|html)$/.test(entry.name)) continue;
    files++;
    const source = fs.readFileSync(filename, 'utf8');
    // Includes bare calls and window/globalThis/self access, including brackets.
    // Owner methods such as context.confirm are the asynchronous app contract.
    const native = /\b(?:window|globalThis|self)\s*(?:\??\.\s*(?:confirm|alert|prompt)\b|(?:\?\.)?\s*\[\s*['"](?:confirm|alert|prompt)['"]\s*\])|(?<![\w.$])(?:confirm|alert|prompt)\s*(?:\?\.)?\s*\(/g;
    if (native.test(source)) failures.push(path.relative(root, filename));
  }
}
inspect(root);
assert.deepEqual(failures, [], 'Application-owned native dialogs are forbidden. Keep the event-based beforeunload safeguard.');
console.log(`${files} production frontend files checked for native dialogs`);
