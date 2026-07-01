const assert = require("assert");
const fs = require("fs");
const path = require("path");

const templates = fs.readFileSync(path.join(__dirname, "../pb_public/staff/js/settings-templates.js"), "utf8");

console.log("Running rejection template body UI tests...");

assert.ok(
  /<textarea[^>]*class="[^"]*\bjs-update-rejection-template\b[^"]*"[^>]*data-field="body"/.test(templates),
  "rejection template body textarea should update the in-memory template state"
);

console.log("All rejection template body UI tests passed!");
