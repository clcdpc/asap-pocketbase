const assert = require("assert");
global.__hooks = __dirname + "/../pb_hooks";
const crypto = require("../lib/crypto.js");
crypto.hmacSha1Base64 = () => "test-signature";
const helpers = require("../lib/polaris/helpers.js");
global.$app = { logger: () => ({ error() {} }) };
const config = { apiKey: "test-key", accessId: "test-access" };
const endpoint = { full: "https://example.org/authenticator/staff", signature: "https://example.org/authenticator/staff" };
for (const code of [-8001, "-8001"]) {
  global.$http = { send: () => ({ statusCode: 200, json: { PAPIErrorCode: code } }) };
  assert.throws(() => helpers.send("POST", endpoint, "{}", null, null, config), error => {
    assert.strictEqual(error.papiErrorCode, -8001);
    assert.match(error.message, /Code: -8001/);
    return true;
  });
}
global.$http = { send: () => ({ statusCode: 200, json: { PAPIErrorCode: 0, AccessToken: "test-token" } }) };
assert.strictEqual(helpers.send("POST", endpoint, "{}", null, null, config).AccessToken, "test-token");
console.log("polaris_application_error.test.js passed");
