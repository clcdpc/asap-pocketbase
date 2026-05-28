const assert = require('assert');
const fs = require('fs');
const path = require('path');

const root = path.join(__dirname, '..');
const initial = fs.readFileSync(path.join(root, 'pb_migrations', '0000000000_initial.js'), 'utf8');
const allowAnyMigration = fs.readFileSync(path.join(root, 'pb_migrations', '202605280001_allow_any_registered_card_login.js'), 'utf8');

function sliceBetween(source, startNeedle, endNeedle) {
  const start = source.indexOf(startNeedle);
  assert.ok(start >= 0, `missing ${startNeedle}`);
  const end = source.indexOf(endNeedle, start + startNeedle.length);
  assert.ok(end >= 0, `missing ${endNeedle}`);
  return source.slice(start, end);
}

const unusedPatronFields = [
  'experienceLibraryOrgId',
  'experienceLibraryOrgName',
  'effectiveLibraryOrgId',
  'effectiveLibraryOrgName'
];
const contextFields = [
  ...unusedPatronFields,
  'patronHomeLibraryOrgId',
  'patronHomeLibraryOrgName',
  'expiresAt'
];

const patronUsersInitial = sliceBetween(initial, 'name: "patron_users"', 'name: "patron_session_contexts"');
for (const fieldName of unusedPatronFields) {
  assert.ok(!patronUsersInitial.includes(`field("${fieldName}"`), `initial patron_users should not include ${fieldName}`);
}
assert.ok(patronUsersInitial.includes('field("patronHomeLibraryOrgId"'), 'initial patron_users should keep patronHomeLibraryOrgId');
assert.ok(patronUsersInitial.includes('field("patronHomeLibraryOrgName"'), 'initial patron_users should keep patronHomeLibraryOrgName');

const sessionContextsInitial = sliceBetween(initial, 'name: "patron_session_contexts"', 'name: "title_requests"');
for (const fieldName of contextFields) {
  assert.ok(sessionContextsInitial.includes(`field("${fieldName}"`), `patron_session_contexts should include ${fieldName}`);
}

for (const fieldName of unusedPatronFields) {
  assert.ok(!allowAnyMigration.includes(`addField(patronUsers, field("${fieldName}"`), `allowAny migration should not add patron_users.${fieldName}`);
  assert.ok(!allowAnyMigration.includes(`removeField(patronUsers, "${fieldName}"`), `allowAny migration should not remove patron_users.${fieldName}`);
}
for (const fieldName of contextFields) {
  assert.ok(allowAnyMigration.includes(`field("${fieldName}"`) || allowAnyMigration.includes(`addField(patronUsers, field("${fieldName}"`), `allowAny migration should retain session/home field ${fieldName}`);
}

console.log('Patron context schema cleanup checks passed');
