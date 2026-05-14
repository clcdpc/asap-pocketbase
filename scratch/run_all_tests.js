const fs = require('fs');
const path = require('path');
const { execSync } = require('child_process');

const testsDir = path.join(__dirname, '../tests');
const files = fs.readdirSync(testsDir);
const testFiles = files.filter(f => f.endsWith('.test.js')).sort();

let totalPassedSuites = 0;
let totalFailedSuites = 0;
const failures = [];

console.log(`\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━`);
console.log(` GSD ► TEST RUNNER`);
console.log(` Found ${testFiles.length} test suites.`);
console.log(`━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n`);

testFiles.forEach((file, index) => {
  const filePath = path.join(testsDir, file);
  process.stdout.write(`[${index + 1}/${testFiles.length}] Running ${file}... `);
  
  try {
    execSync(`node "${filePath}"`, { stdio: 'pipe' });
    console.log('✅');
    totalPassedSuites++;
  } catch (err) {
    console.log('❌');
    totalFailedSuites++;
    failures.push({
      file,
      error: err.stdout ? err.stdout.toString() : err.message
    });
  }
});

console.log(`\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━`);
console.log(` SUMMARY`);
console.log(` Total Suites:  ${testFiles.length}`);
console.log(` Passed:        ${totalPassedSuites}`);
console.log(` Failed:        ${totalFailedSuites}`);
console.log(`━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n`);

if (failures.length > 0) {
  console.log(`FAILURE DETAILS:`);
  failures.forEach(f => {
    console.log(`\n--- ${f.file} ---`);
    console.log(f.error);
  });
  process.exit(1);
} else {
  console.log(`All test suites passed! ✨`);
  process.exit(0);
}
