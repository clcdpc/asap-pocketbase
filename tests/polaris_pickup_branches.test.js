const assert = require('assert');
const Module = require('module');

const originalRequire = Module.prototype.require;
Module.prototype.require = function(moduleName) {
  if (moduleName.includes('config.js')) {
    return {
      polaris: function() {
        return {
          host: 'api.polaris.example.com',
          accessId: 'TEST_ID',
          apiKey: 'TEST_KEY',
          langId: '1033',
          appId: '100',
          orgId: '1',
          pickupOrgId: '0',
          requestingOrgId: '3',
          workstationId: '99',
          userId: '42',
        };
      }
    };
  }
  if (moduleName.includes('crypto.js')) {
    return { hmacSha1Base64: () => 'mock_signature' };
  }
  return originalRequire.apply(this, arguments);
};

global.$app = { logger: () => ({ error() {}, info() {} }) };
let httpSendArgs = null;
global.$http = {
  send(args) {
    httpSendArgs = args;
    return {
      statusCode: 200,
      json: {
        PickupBranchesRows: {
          PickupBranchRow: [
            { OrganizationID: '3', DisplayName: 'Main Library' },
            { OrgID: '4', OrganizationName: 'Branch Library' },
            { PickupBranchID: '5', BranchName: 'Pickup Desk' },
          ]
        }
      }
    };
  }
};

const polaris = require('../lib/polaris.js');
const patron = require('../lib/polaris/patron.js');

try {
  const normalized = patron.normalizePickupBranches([
    { OrganizationID: '3', DisplayName: 'Main Library' },
    { OrgID: '4', OrganizationName: 'Branch Library' },
    { PickupBranchID: '5', BranchName: 'Pickup Desk' },
    { PickupBranchID: '5', BranchName: 'Duplicate Pickup Desk' },
    { PickupBranchID: '', BranchName: 'Missing ID' },
    { BranchID: '6' },
  ]);

  assert.deepStrictEqual(normalized, [
    { id: '6', label: 'Branch 6' },
    { id: '4', label: 'Branch Library' },
    { id: '3', label: 'Main Library' },
    { id: '5', label: 'Pickup Desk' },
  ]);

  const rows = polaris.getPickupBranches({ AccessToken: 'token', AccessSecret: 'secret' }, '77');
  assert.strictEqual(httpSendArgs.method, 'GET');
  assert.ok(httpSendArgs.url.includes('/public/v1/1033/100/77/pickupbranches'));
  assert.ok(!httpSendArgs.url.includes('/public/v1/1033/100/1/pickupbranches'));
  assert.strictEqual(httpSendArgs.headers['X-PAPI-AccessToken'], 'token');
  assert.deepStrictEqual(rows, [
    { id: '4', label: 'Branch Library' },
    { id: '3', label: 'Main Library' },
    { id: '5', label: 'Pickup Desk' },
  ]);

  httpSendArgs = null;
  polaris.updatePatronPreferredPickupBranch({ AccessToken: 'token', AccessSecret: 'secret' }, '2900', '55', { polarisUserId: 'actor7' });
  assert.strictEqual(httpSendArgs.method, 'PUT');
  assert.ok(httpSendArgs.url.includes('/protected/v1/1033/100/1/token/patron/2900/registration'));
  assert.ok(httpSendArgs.body.includes('<LogonBranchID>1</LogonBranchID>'));
  assert.ok(httpSendArgs.body.includes('<LogonUserID>actor7</LogonUserID>'));
  assert.ok(httpSendArgs.body.includes('<LogonWorkstationID>99</LogonWorkstationID>'));
  assert.ok(httpSendArgs.body.includes('<RequestPickupBranchID>55</RequestPickupBranchID>'));

  polaris.updatePatronPreferredPickupBranch({ AccessToken: 'token', AccessSecret: 'secret' }, '2900', '56', {});
  assert.ok(httpSendArgs.body.includes('<LogonUserID>42</LogonUserID>'));

  assert.throws(() => polaris.updatePatronPreferredPickupBranch(null, '', '56'), /Missing patron barcode/);
  assert.throws(() => polaris.updatePatronPreferredPickupBranch(null, '2900', ''), /Missing pickup branch ID/);

  console.log('Polaris pickup branch wrapper tests passed.');
} finally {
  Module.prototype.require = originalRequire;
}
