const assert = require('assert');
const fs = require('fs');
const path = require('path');

// Extract escapeAttr from staff app
const staffAppContent = fs.readFileSync(path.join(__dirname, '../pb_public/staff/app.js'), 'utf8');
const escapeAttrMatch = staffAppContent.match(/function escapeAttr\(value\) {[\s\S]*?}/);
if (!escapeAttrMatch) {
  throw new Error("Could not find escapeAttr in staff/app.js");
}
const escapeAttr = new Function('value', `
  ${escapeAttrMatch[0]}
  return escapeAttr(value);
`);

// Extract escapeHtml from patron app
const patronAppContent = fs.readFileSync(path.join(__dirname, '../pb_public/patron/app.js'), 'utf8');
const escapeHtmlMatch = patronAppContent.match(/function escapeHtml\(str\) {[\s\S]*?}/);
if (!escapeHtmlMatch) {
  throw new Error("Could not find escapeHtml in patron/app.js");
}
const escapeHtml = new Function('str', `
  ${escapeHtmlMatch[0]}
  return escapeHtml(str);
`);

function runTests() {
  const testCases = [
    { input: "normal text", expected: "normal text" },
    { input: null, expected: "" },
    { input: undefined, expected: "" },
    { input: "<script>alert(1)</script>", expected: "&lt;script&gt;alert(1)&lt;/script&gt;" },
    { input: "\" onload=\"alert(1)", expected: "&quot; onload=&quot;alert(1)" },
    { input: "' onload='alert(1)", expected: "&#39; onload=&#39;alert(1)" },
    { input: "& < > \" '", expected: "&amp; &lt; &gt; &quot; &#39;" }
  ];

  let passed = 0;
  let failed = 0;

  for (const { input, expected } of testCases) {
    try {
      assert.strictEqual(escapeAttr(input), expected, `escapeAttr failed for input: ${input}`);
      assert.strictEqual(escapeHtml(input), expected, `escapeHtml failed for input: ${input}`);
      passed++;
    } catch (error) {
      console.error(error.message);
      failed++;
    }
  }

  console.log(`Tests finished: ${passed} passed, ${failed} failed.`);
  if (failed > 0) {
    process.exit(1);
  }
}

runTests();
