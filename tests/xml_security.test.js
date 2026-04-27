const assert = require('assert');

function escapeXml(value) {
  return String(value === undefined || value === null ? "" : value)
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&apos;");
}

function buildXml(root, data) {
  var xml = '<?xml version="1.0" encoding="UTF-8"?>';
  xml += '<' + root + '>';
  for (var key in data) {
    if (Object.prototype.hasOwnProperty.call(data, key)) {
      xml += '<' + key + '>' + escapeXml(data[key]) + '</' + key + '>';
    }
  }
  xml += '</' + root + '>';
  return xml;
}

console.log('Running tests for XML security...');

// Test escapeXml
try {
  assert.strictEqual(escapeXml('foo & bar'), 'foo &amp; bar');
  assert.strictEqual(escapeXml('<script>'), '&lt;script&gt;');
  assert.strictEqual(escapeXml('"quoted"'), '&quot;quoted&quot;');
  assert.strictEqual(escapeXml("'single'"), '&apos;single&apos;');
  assert.strictEqual(escapeXml(null), '');
  assert.strictEqual(escapeXml(undefined), '');
  assert.strictEqual(escapeXml(123), '123');
  console.log('✅ escapeXml tests passed');
} catch (err) {
  console.error('❌ escapeXml tests failed:', err.message);
  process.exit(1);
}

// Test buildXml
try {
  const data = {
    PatronID: '123',
    BibID: '456',
    Injection: '</PatronID><BibID>MALICIOUS</BibID>'
  };
  const xml = buildXml('HoldRequestCreateData', data);

  assert.ok(xml.startsWith('<?xml version="1.0" encoding="UTF-8"?>'));
  assert.ok(xml.includes('<PatronID>123</PatronID>'));
  assert.ok(xml.includes('<BibID>456</BibID>'));
  // This is the critical security check: the injection attempt should be escaped
  assert.ok(xml.includes('<Injection>&lt;/PatronID&gt;&lt;BibID&gt;MALICIOUS&lt;/BibID&gt;</Injection>'));
  assert.ok(xml.endsWith('</HoldRequestCreateData>'));

  console.log('✅ buildXml security tests passed');
} catch (err) {
  console.error('❌ buildXml security tests failed:', err.message);
  process.exit(1);
}

// Test specific structure for placeHold
try {
  const data = {
    PatronID: 'P123',
    BibID: 'B456',
    PickupOrgID: 'O789',
    WorkstationID: 'W1',
    UserID: 'U1',
    RequestingOrgID: 'R3'
  };
  const xml = buildXml('HoldRequestCreateData', data);
  const expected = '<?xml version="1.0" encoding="UTF-8"?><HoldRequestCreateData><PatronID>P123</PatronID><BibID>B456</BibID><PickupOrgID>O789</PickupOrgID><WorkstationID>W1</WorkstationID><UserID>U1</UserID><RequestingOrgID>R3</RequestingOrgID></HoldRequestCreateData>';
  assert.strictEqual(xml, expected);
  console.log('✅ buildXml placeHold structure test passed');
} catch (err) {
  console.error('❌ buildXml placeHold structure test failed:', err.message);
  process.exit(1);
}

// Test specific structure for replyToHold
try {
  const data = {
    TxnGroupQualifier: 'TGQ',
    TxnQualifier: 'TQ',
    RequestingOrgID: 'R3',
    Answer: '1',
    State: '3'
  };
  const xml = buildXml('HoldRequestReplyData', data);
  const expected = '<?xml version="1.0" encoding="UTF-8"?><HoldRequestReplyData><TxnGroupQualifier>TGQ</TxnGroupQualifier><TxnQualifier>TQ</TxnQualifier><RequestingOrgID>R3</RequestingOrgID><Answer>1</Answer><State>3</State></HoldRequestReplyData>';
  assert.strictEqual(xml, expected);
  console.log('✅ buildXml replyToHold structure test passed');
} catch (err) {
  console.error('❌ buildXml replyToHold structure test failed:', err.message);
  process.exit(1);
}

console.log('\nAll XML security tests finished successfully.');
