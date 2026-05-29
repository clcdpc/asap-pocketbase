const assert = require('assert');
const fs = require('fs');
const path = require('path');
const { execSync } = require('child_process');

/**
 * Test to ensure that 'Dao' is not used in the codebase, 
 * as it is deprecated/undefined in PocketBase 0.37+.
 */
function testNoDaoUsage() {
  console.log('Running testNoDaoUsage...');

  const rootDir = path.resolve(__dirname, '..');
  const searchDirs = ['pb_migrations', 'pb_hooks', 'lib'];
  
  let foundDao = false;
  const matches = [];

  searchDirs.forEach(dir => {
    const fullPath = path.join(rootDir, dir);
    if (!fs.existsSync(fullPath)) return;

    try {
      // Search for "new Dao" or "Dao(" (common usage patterns for the deprecated class)
      // We use grep -r to find all occurrences
      const output = execSync(`grep -rnE "new Dao|Dao\\(" ${fullPath} || true`).toString();
      if (output.trim()) {
        foundDao = true;
        matches.push(...output.trim().split('\n'));
      }
    } catch (err) {
      console.error(`Error searching in ${dir}:`, err);
    }
  });

  if (foundDao) {
    console.error('FAIL: Found "Dao" usage in the following files:');
    matches.forEach(match => console.error(`  ${match}`));
    process.exit(1);
  } else {
    console.log('PASS: No "Dao" usage found.');
  }
}

testNoDaoUsage();
