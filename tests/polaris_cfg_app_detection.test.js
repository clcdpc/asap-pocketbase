const assert = require("assert");
const path = require("path");
const Module = require("module");

global.__hooks = path.resolve(__dirname, "../pb_hooks");

const originalRequire = Module.prototype.require;
let receivedApp = null;

Module.prototype.require = function(moduleName) {
  if (moduleName === "../config.js") {
    return {
      polaris(app) {
        receivedApp = app;
        return {
          host: "https://example.org",
          accessId: "access",
          apiKey: "secret",
          staffDomain: "domain",
          adminUser: "user",
          adminPassword: "pass",
          langId: "1033",
          appId: "100",
          orgId: "1",
          pickupOrgId: "0",
          requestingOrgId: "3",
          workstationId: "1",
          userId: "1",
        };
      },
    };
  }
  if (moduleName === "../crypto.js") {
    return { hmacSha1Base64() { return "signature"; } };
  }
  return originalRequire.apply(this, arguments);
};

delete require.cache[require.resolve("../lib/polaris/helpers.js")];
const helpers = require("../lib/polaris/helpers.js");
Module.prototype.require = originalRequire;

const pocketBaseAppLike = {
  findRecordById() {},
  findRecordsByFilter() {},
};

const cfg = helpers.cfg(pocketBaseAppLike);
const endpoint = helpers.endpoint("protected", "authenticator/staff", cfg);

assert.strictEqual(receivedApp, pocketBaseAppLike);
assert.strictEqual(endpoint.full, "https://example.org/PAPIService/REST/protected/v1/1033/100/1/authenticator/staff");

console.log("Polaris config app detection test passed.");
