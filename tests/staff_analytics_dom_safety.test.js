const assert = require('assert');
const fs = require('fs');
const path = require('path');

const source = fs.readFileSync(path.join(__dirname, '../pb_public/staff/js/analytics.js'), 'utf8');

assert.ok(source.includes('export async function loadAnalytics(container)'), 'analytics.js should still export loadAnalytics');
assert.ok(!/\.innerHTML\s*=/.test(source), 'analytics.js should not assign innerHTML');
assert.ok(!source.includes('function escapeHtml('), 'analytics.js should not define a local escapeHtml helper');
assert.ok(source.includes('document.createElement('), 'analytics.js should build DOM nodes directly');
assert.ok(source.includes('replaceChildren('), 'analytics.js should replace container contents with DOM nodes');

console.log('staff analytics DOM safety test passed.');
