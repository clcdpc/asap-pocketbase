const assert = require("assert");

const embedSecurity = require("../lib/embed_security.js");

console.log("Running embed security tests...");

const normalized = embedSecurity.normalizeEmbedAllowedOrigins([
  "https://www.library.org",
  "https://www.library.org/",
  "https://*.branches.library.org",
  "http://localhost:8090"
].join("\n"));

assert.strictEqual(
  normalized,
  [
    "https://www.library.org",
    "https://*.branches.library.org",
    "http://localhost:8090"
  ].join("\n"),
  "Expected origins to normalize and deduplicate"
);

assert.strictEqual(
  embedSecurity.frameAncestorsCsp(normalized),
  "frame-ancestors 'self' https://www.library.org https://*.branches.library.org http://localhost:8090",
  "Expected CSP frame-ancestors to include normalized origins"
);

assert.throws(
  () => embedSecurity.normalizeEmbedAllowedOrigins("http://www.library.org"),
  /https/,
  "Non-local http origins should be rejected"
);

assert.throws(
  () => embedSecurity.normalizeEmbedAllowedOrigins("https://www.library.org/page"),
  /origins only/,
  "Origins with paths should be rejected"
);

assert.throws(
  () => embedSecurity.normalizeEmbedAllowedOrigins("https://www.library.org; frame-src *"),
  /whitespace|semicolons/,
  "Arbitrary CSP directives should be rejected"
);

console.log("All embed security tests passed!");
