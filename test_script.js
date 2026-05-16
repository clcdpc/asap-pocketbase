const fs = require('fs');
const source = fs.readFileSync('lib/staff_routes.js', 'utf8');

const functionsToFind = [
  "staffLogin",
  "staffProfileUpdate",
  "looksLikeBarcodeCandidate",
  "staffLookupPatron",
  "staffUsersList",
];

// Let's print out what module.exports exports
const exportMatch = source.match(/module\.exports\s*=\s*\{([\s\S]*?)\};/);
if (exportMatch) {
  console.log("Exports:");
  console.log(exportMatch[1].trim().split('\n').map(l => l.trim()).join('\n'));
}
