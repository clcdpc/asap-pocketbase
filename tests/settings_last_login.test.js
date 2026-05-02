const assert = require("assert");
const fs = require("fs");
const path = require("path");

function loadSettingsSource() {
  const file = fs.readFileSync(path.resolve(__dirname, "../pb_public/staff/js/settings-users.js"), "utf8");
  const match = file.match(/const source = ([\s\S]*?\]\.join\('\\n'\));/);
  if (!match) throw new Error("Could not find settings source array.");
  return Function(`return ${match[1]}`)();
}

function extractFunction(source, name) {
  const marker = `${name} = function ${name}(`;
  const start = source.indexOf(marker);
  if (start < 0) throw new Error(`Could not find ${name}.`);
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
      if (opened && depth === 0) return source.slice(start, index + 1);
    }
  }
  throw new Error(`Could not parse ${name}.`);
}

const env = { formatLastLogin: null };
new Function("env", `with (env) {\n${extractFunction(loadSettingsSource(), "formatLastLogin")}\n}`)(env);

assert.strictEqual(env.formatLastLogin(null), "Never");
assert.strictEqual(env.formatLastLogin(""), "Never");
const formatted = env.formatLastLogin("2026-04-30T13:14:00.000Z");
assert.notStrictEqual(formatted, "Never");
assert.match(formatted, /2026/);

console.log("Settings last-login tests passed.");
