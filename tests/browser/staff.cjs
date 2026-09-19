'use strict';

const path = require('node:path');
const { spawnSync } = require('node:child_process');

function run(scriptName, args) {
  const result = spawnSync(
    process.execPath,
    [path.join(__dirname, scriptName), ...args],
    { encoding: 'utf8', env: process.env }
  );
  if (result.stdout) process.stdout.write(result.stdout);
  if (result.stderr) process.stderr.write(result.stderr);
  if (result.error) throw result.error;
  if (result.status !== 0) {
    process.exitCode = result.status || 1;
    return false;
  }
  return true;
}

const args = process.argv.slice(2);
if (run('staff-legacy.cjs', args)) {
  run('staff-next.cjs', args);
}
