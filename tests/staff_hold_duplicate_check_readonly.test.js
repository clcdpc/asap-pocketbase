const assert = require('assert');
const fs = require('fs');
const path = require('path');

const source = fs.readFileSync(path.resolve(__dirname, '../lib/staff_routes.js'), 'utf8');

assert.ok(
  source.includes('polaris.patronHasHoldForBib(staffAuth, barcode, bibid)'),
  'pending-hold transition duplicate check must use read-only patron hold lookup'
);
assert.ok(
  source.includes('polaris.patronHasHoldForBib(staffAuth, barcode, bibId)'),
  'BIB lookup duplicate check must use read-only patron hold lookup'
);
assert.ok(
  !source.includes('polaris.placeHold(staffAuth, bibid, pPatron.PatronID, true)'),
  'pending-hold transition duplicate check must not create a hold while probing'
);
assert.ok(
  !source.includes('polaris.placeHold(staffAuth, bibId, patron.PatronID, true)'),
  'BIB lookup duplicate check must not create a hold while probing'
);

console.log('Staff hold duplicate checks are read-only.');
