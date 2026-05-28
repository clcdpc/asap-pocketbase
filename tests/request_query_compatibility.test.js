const fs = require('fs');
const path = require('path');
const assert = require('assert');

const root = path.resolve(__dirname, '..');
const targets = [
  path.join(root, 'lib', 'staff', 'users_routes.js'),
  path.join(root, 'pb_hooks', 'main.pb.js')
];

const forbidden = /request\.url\.query\(\)\.get\(/;

for (const file of targets) {
  const source = fs.readFileSync(file, 'utf8');
  assert.ok(
    !forbidden.test(source),
    `${file} should use route_utils.queryValue() instead of request.url.query().get()`
  );
}

console.log('request_query_compatibility.test.js passed.');
