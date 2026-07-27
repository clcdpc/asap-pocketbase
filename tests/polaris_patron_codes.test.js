const assert = require("assert");
const path = require("path");

global.__hooks = path.resolve(__dirname, "../pb_hooks");

const Module = require("module");
const originalRequire = Module.prototype.require;

Module.prototype.require = function(moduleName) {
  if (moduleName.includes("../config.js")) {
    return {
      polaris: () => ({
        host: "https://polaris.example.org",
        accessId: "PAPI",
        apiKey: "PAPIKEY",
        langId: "1033",
        appId: "100",
        orgId: "3"
      })
    };
  }
  if (moduleName.includes("../crypto.js")) {
    return { hmacSha1Base64: () => "sig" };
  }
  return originalRequire.apply(this, arguments);
};

const patronCodes = require("../lib/polaris/patron_codes.js");
Module.prototype.require = originalRequire;

let calls = [];
global.$http = {
  send: args => {
    calls.push(args);
    return {
      statusCode: 200,
      json: {
        PatronCodesGetRows: {
          PatronCodesGetRow: [
            { PatronCodeID: 91, Description: "Adult" }
          ]
        }
      }
    };
  }
};

const staff = {
  AccessToken: "papi-token",
  AccessSecret: "papi-secret"
};
const rows = patronCodes.patronCodes(staff, {
  host: "https://polaris.example.org",
  accessId: "PAPI",
  apiKey: "PAPIKEY",
  langId: "1033",
  appId: "100",
  orgId: "3"
});

assert.strictEqual(calls.length, 1);
assert.strictEqual(calls[0].method, "GET");
assert.strictEqual(
  calls[0].url,
  "https://polaris.example.org/PAPIService/REST/public/v1/1033/100/1/patroncodes"
);
assert.ok(String(calls[0].headers.Authorization || "").indexOf("PWS PAPI:") === 0);
assert.strictEqual(calls[0].headers["X-PAPI-AccessToken"], "papi-token");
assert.deepStrictEqual(rows, [{ PatronCodeID: 91, Description: "Adult" }]);

assert.deepStrictEqual(
  patronCodes.normalizePatronCodeRows({
    PatronCodeGetRows: {
      PatronCodeGetRow: { PatronCodeID: "92", Description: "" }
    }
  }),
  [{ PatronCodeID: "92", Description: "" }]
);

assert.deepStrictEqual(
  patronCodes.normalizePatronCodeRows({
    PAPIErrorCode: 0,
    ErrorMessage: "",
    PatronCodesRows: {
      PatronCodesRow: [
        { PatronCodeID: 1, Description: "Full Access" },
        { PatronCodeID: 28, Description: "Video/VG Restricted" }
      ]
    }
  }),
  [
    { PatronCodeID: 1, Description: "Full Access" },
    { PatronCodeID: 28, Description: "Video/VG Restricted" }
  ]
);

console.log("polaris_patron_codes.test.js passed.");
