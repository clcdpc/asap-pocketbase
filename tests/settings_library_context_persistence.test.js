const assert = require('assert');
const fs = require('fs');
const path = require('path');

const source = fs.readFileSync(path.join(__dirname, '../pb_public/staff/js/settings/library-context.js'), 'utf8');

console.log('Running settings library context persistence tests...');

assert.ok(source.includes("const SUPER_ADMIN_LIBRARY_CONTEXT_STORAGE_KEY = 'asap.superAdmin.settings.libraryContextOrgId';"));
assert.ok(source.includes('function readSavedSuperAdminLibraryContext()'));
assert.ok(source.includes('window.localStorage.getItem(SUPER_ADMIN_LIBRARY_CONTEXT_STORAGE_KEY)'));
assert.ok(source.includes('function saveSuperAdminLibraryContext(orgId)'));
assert.ok(source.includes('window.localStorage.setItem(SUPER_ADMIN_LIBRARY_CONTEXT_STORAGE_KEY'));
assert.ok(source.includes('const selectedOrgId = savedOrgId || currentLibraryContextOrgId || select.value || \'system\';'));
assert.ok(source.includes('saveSuperAdminLibraryContext(select.value);'));
assert.ok(source.includes('saveSuperAdminLibraryContext(nextOrgId);'));

console.log('All settings library context persistence tests passed!');
