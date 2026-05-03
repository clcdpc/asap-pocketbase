const assert = require("assert");
const fs = require("fs");
const path = require("path");

function loadSettingsSource() {
  const settingsUsers = fs.readFileSync(path.resolve(__dirname, "../pb_public/staff/js/settings-users.js"), "utf8");
  const settingsTemplates = fs.readFileSync(path.resolve(__dirname, "../pb_public/staff/js/settings-templates.js"), "utf8");
  const settingsApi = fs.readFileSync(path.resolve(__dirname, "../pb_public/staff/js/api.js"), "utf8");
  const settingsSettings = fs.readFileSync(path.resolve(__dirname, "../pb_public/staff/js/settings.js"), "utf8");
  return settingsUsers + '\n' + settingsTemplates + '\n' + settingsApi + '\n' + settingsSettings;

}

function extractFunction(source, name) {
  const marker = `function ${name}(`;
  const start = source.indexOf(marker);
  if (start < 0) throw new Error(`Could not find ${name}.`);

  let functionStart = start;
  while(functionStart > 0 && source[functionStart - 1] !== '\n') {
      functionStart--;
  }

  let depth = 0;
  let opened = false;
  for (let index = start; index < source.length; index++) {
    const char = source[index];
    if (char === "{") {
      depth++;
      opened = true;
    }
    if (char === "}") {
      depth--;
      if (opened && depth === 0) {
          let extracted = source.slice(functionStart, index + 1);
          extracted = extracted.replace(/^export\s+/, '');
          return 'env.' + name + ' = ' + extracted;
      }
    }
  }
  throw new Error(`Could not parse ${name}.`);
}

const env = { formatLastLogin: null };
eval(extractFunction(loadSettingsSource(), "formatLastLogin"));

assert.strictEqual(env.formatLastLogin(null), "Never");
assert.strictEqual(env.formatLastLogin(""), "Never");
const formatted = env.formatLastLogin("2026-04-30T13:14:00.000Z");
assert.notStrictEqual(formatted, "Never");
assert.match(formatted, /2026/);

console.log("Settings last-login tests passed.");
