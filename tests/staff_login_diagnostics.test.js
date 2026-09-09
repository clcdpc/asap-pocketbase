const assert = require('assert');
global.__hooks = __dirname + '/../pb_hooks';
const routeUtils = require('../lib/route_utils.js');
const config = require('../lib/config.js');
const records = require('../lib/records.js');
const polaris = require('../lib/polaris.js');
const orgs = require('../lib/orgs.js');
let allowed = false;
let calls = 0;
let active = true;
let upstream = Object.assign(new Error('secret-password secret-token'), { papiErrorCode: -8001 });
routeUtils.requireSuperAdminStaff = () => allowed ? {} : null;
routeUtils.body = () => ({ username: 'WESTERVILLELIBRARY.ORG\\rsomerfeldt', password: 'secret-password' });
config.polaris = () => ({ host: 'https://polaris.example.org', accessId: 'access', apiKey: 'secret-key', staffDomain: 'DEFAULT' });
config.enabledLibraryOrgIds = () => '2';
records.findStaffByIdentity = (app, key) => {
  assert.strictEqual(key, 'westervillelibrary.org\\rsomerfeldt');
  return { getBool: () => active, get: () => 'staff' };
};
polaris.staffAuth = (username, password, cfg, domain) => {
  calls++;
  assert.strictEqual(username, 'rsomerfeldt');
  assert.strictEqual(domain, 'WESTERVILLELIBRARY.ORG');
  assert.strictEqual(password, 'secret-password');
  if (upstream instanceof Error) throw upstream;
  return upstream;
};
orgs.resolveParentLibrary = (app, branch, options) => {
  assert.strictEqual(options.syncIfMissing, false);
  return { libraryOrgId: '2' };
};
const e = {
  app: { logger: () => ({ warn() {} }), save: () => { throw new Error('Must not save'); } },
  response: { header: () => ({ set() {} }) },
  json: (status, body) => ({ status, body })
};
const { staffLoginDiagnostics } = require('../lib/staff/login_diagnostics.js');
assert.strictEqual(staffLoginDiagnostics(e).status, 403);
assert.strictEqual(calls, 0);
allowed = true;
let result = staffLoginDiagnostics(e);
assert.strictEqual(result.body.stage, 'polaris_authentication');
assert.strictEqual(result.body.papiErrorCode, -8001);
assert.strictEqual(result.body.request.url, 'https://polaris.example.org/PAPIService/REST/protected/v1/1033/100/1/authenticator/staff');
assert.ok(!JSON.stringify(result).includes('secret-'));
active = false;
assert.strictEqual(staffLoginDiagnostics(e).body.stage, 'local_account');
assert.strictEqual(calls, 1);
active = true;
upstream = { AccessToken: 'secret-token', AccessSecret: 'secret-access', BranchID: 3, PolarisUserID: 4 };
result = staffLoginDiagnostics(e);
assert.strictEqual(result.body.ok, true);
assert.strictEqual(result.body.stage, 'complete');
assert.ok(!JSON.stringify(result).includes('secret-'));
console.log('staff_login_diagnostics.test.js passed');
