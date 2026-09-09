const assert = require("assert");
const templates = require("../lib/mail/templates.js");

console.log("Running mail template rendering tests...");

const body = "Thank you for suggesting {{title}} by {{author}} in {{format}} format.";

assert.strictEqual(
  templates.replacePlaceholders(body, {
    title: "Test Video Game",
    author: "",
    format: "Video Game"
  }, false),
  "Thank you for suggesting Test Video Game in Video Game format."
);

assert.strictEqual(
  templates.replacePlaceholders(body, {
    title: "A Book",
    author: "An Author",
    format: "Book"
  }, false),
  "Thank you for suggesting A Book by An Author in Book format."
);

assert.strictEqual(
  templates.formatLabel("videogame", { videogame: "Video Game" }),
  "Video Game"
);
assert.strictEqual(templates.formatLabel("ebook", {}), "eBook");
assert.strictEqual(templates.formatLabel("unknown_format", {}), "unknown_format");

console.log("All mail template rendering tests passed!");
