const assert = require('assert');
const { spawnSync } = require('child_process');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');

const modules = [
  './pb_public/patron/js/custom-fields.js',
  './pb_public/patron/js/form-ui.js',
  './pb_public/staff/js/settings-additional-fields.js',
  './pb_public/staff/js/request-custom-fields.js',
];

const env = { ...process.env };
delete env.NODE_NO_WARNINGS;
if (env.NODE_OPTIONS) {
  env.NODE_OPTIONS = env.NODE_OPTIONS
    .split(/\s+/)
    .filter((option) => {
      return option !== '--no-warnings' &&
        option !== '--disable-warning=MODULE_TYPELESS_PACKAGE_JSON' &&
        option !== '--disable-warning=ExperimentalWarning';
    })
    .join(' ');
  if (!env.NODE_OPTIONS) delete env.NODE_OPTIONS;
}

const script = `
const modules = ${JSON.stringify(modules)};
function isExpectedBrowserEvaluationError(err) {
  if (!err) return false;
  const message = String(err.message || '');
  return message.includes('document is not defined') ||
    message.includes('window is not defined') ||
    message.includes('HTMLElement is not defined') ||
    message.includes('customElements is not defined');
}

function isModuleLoadFailure(err) {
  if (!err) return false;
  const message = String(err.message || '');
  return err.code === 'ERR_MODULE_NOT_FOUND' ||
    err.code === 'ERR_UNKNOWN_FILE_EXTENSION' ||
    message.includes('Cannot find module') ||
    message.includes('does not provide an export named');
}

for (const specifier of modules) {
  try {
    await import(specifier);
  } catch (err) {
    if (isModuleLoadFailure(err) || !isExpectedBrowserEvaluationError(err)) {
      throw err;
    }
  }
}
`;

const result = spawnSync(process.execPath, ['--input-type=module', '--eval', script], {
  cwd: ROOT,
  env,
  encoding: 'utf8',
});

assert.strictEqual(
  result.status,
  0,
  `module warning probe should exit cleanly\nstdout:\n${result.stdout}\nstderr:\n${result.stderr}`
);

assert.ok(
  !result.stderr.includes('MODULE_TYPELESS_PACKAGE_JSON'),
  `frontend ES module imports should not emit MODULE_TYPELESS_PACKAGE_JSON warnings\nstderr:\n${result.stderr}`
);

console.log('module type warning regression test passed.');
