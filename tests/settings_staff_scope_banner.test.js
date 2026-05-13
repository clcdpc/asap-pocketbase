const assert = require('assert');
const fs = require('fs');
const path = require('path');

const source = fs.readFileSync(path.join(__dirname, '../pb_public/staff/js/api.js'), 'utf8');

function extractFunction(name) {
  const start = source.indexOf('function ' + name + '(');
  if (start < 0) throw new Error('Could not find function ' + name);
  const bodyStart = source.indexOf('{', start);
  let depth = 0;
  for (let i = bodyStart; i < source.length; i++) {
    if (source[i] === '{') depth++;
    if (source[i] === '}') depth--;
    if (depth === 0) return source.slice(start, i + 1);
  }
  throw new Error('Could not extract function ' + name);
}

function makeAlert(initialClasses) {
  const classes = new Set(initialClasses || []);
  const element = {
    className: Array.from(classes).join(' '),
    classList: {
      contains(name) {
        return classes.has(name);
      },
      toggle(name, force) {
        const enabled = force === undefined ? !classes.has(name) : !!force;
        if (enabled) {
          classes.add(name);
        } else {
          classes.delete(name);
        }
        this._sync();
      },
      _sync() {
        element.className = Array.from(classes).join(' ');
      }
    }
  };
  return element;
}

function loadHelper(alert) {
  const document = {
    getElementById(id) {
      return id === 'library-override-status' ? alert : null;
    }
  };
  const helperSource = extractFunction('updateLibraryOverrideStatusVisibility');
  return new Function(
    'document',
    'settingsSectionIds',
    'libraryOverrideStatusSections',
    'currentLibraryContextOrgId',
    helperSource + '\nreturn updateLibraryOverrideStatusVisibility;'
  )(
    document,
    ['start', 'polaris', 'staff', 'smtp', 'workflow', 'patron', 'templates'],
    ['workflow', 'patron', 'templates'],
    'system'
  );
}

console.log('Running staff access scope banner tests...');

assert.ok(
  source.includes("export const libraryContextSections = libraryOverrideStatusSections.concat(['staff']);"),
  'Staff access should keep the library context selector without becoming an override-status section'
);

let alert = makeAlert(['alert']);
let updateLibraryOverrideStatusVisibility = loadHelper(alert);
updateLibraryOverrideStatusVisibility('staff', '2');
assert.ok(alert.classList.contains('hidden'), 'Staff access should hide library override/default messaging in a library context');

alert = makeAlert(['alert', 'hidden']);
updateLibraryOverrideStatusVisibility = loadHelper(alert);
updateLibraryOverrideStatusVisibility('workflow', '2');
assert.ok(!alert.classList.contains('hidden'), 'Library-scoped workflow settings should show library override/default messaging');

alert = makeAlert(['alert']);
updateLibraryOverrideStatusVisibility = loadHelper(alert);
updateLibraryOverrideStatusVisibility('patron', 'system');
assert.ok(alert.classList.contains('hidden'), 'System context should not show library override/default messaging');

console.log('All staff access scope banner tests passed!');
