const assert = require("assert");
const path = require("path");

global.__hooks = path.resolve(__dirname, "../pb_hooks");

const configPath = path.resolve(__dirname, "../lib/config.js");
const orgsPath = path.resolve(__dirname, "../lib/orgs.js");
const configRoutesPath = path.resolve(__dirname, "../lib/config_routes.js");

function loadConfigRoutesWithMocks(configMock, orgsMock) {
  require.cache[configPath] = {
    id: configPath,
    filename: configPath,
    loaded: true,
    exports: configMock
  };
  require.cache[orgsPath] = {
    id: orgsPath,
    filename: orgsPath,
    loaded: true,
    exports: orgsMock
  };
  delete require.cache[configRoutesPath];
  return require(configRoutesPath);
}

function makeEvent(libraryOrgId) {
  const responseState = { code: null, payload: null };
  return {
    app: {
      logger() {
        return { error() {} };
      }
    },
    request: {
      url: {
        query() {
          return {
            get(key) {
              if (key !== "libraryOrgId") return "";
              return libraryOrgId || "";
            }
          };
        }
      }
    },
    json(code, payload) {
      responseState.code = code;
      responseState.payload = payload;
      return responseState;
    }
  };
}

function runTests() {
  const systemSettings = {
    enabledLibraryOrgIds: "100",
    ui_text: { welcome: "hello" },
    workflow: {
      externalSearch4Enabled: true,
      externalSearch4Label: "Search Local Catalog",
      externalSearch4UrlTemplate: "https://catalog.example.test/search?q={{title}}"
    }
  };

  const libraryEnabledSettings = {
    ui_text: { welcome: "library hello" },
    workflow: {
      externalSearch4Enabled: true,
      externalSearch4Label: "Search Local Catalog",
      externalSearch4UrlTemplate: "https://catalog.example.test/search?q={{title}}"
    }
  };

  const libraryDisabledSettings = {
    ui_text: { systemNotEnabledMessage: "{{library}} does not currently participate in this suggestion service." },
    workflow: {}
  };

  const configMock = {
    getSettings() {
      return systemSettings;
    },
    librarySettings(app, orgId) {
      if (String(orgId) === "100") return libraryEnabledSettings;
      return libraryDisabledSettings;
    }
  };

  const orgsMock = {
    findOrganization(app, orgId) {
      return {
        get(name) {
          if (name === "displayName") return "North Branch";
          return "North Branch";
        }
      };
    }
  };

  const { publicConfig } = loadConfigRoutesWithMocks(configMock, orgsMock);

  const systemEvent = makeEvent("");
  const systemResult = publicConfig(systemEvent);
  assert.strictEqual(systemResult.code, 200);
  assert.strictEqual(systemResult.payload.welcome, "hello");
  assert.strictEqual(systemResult.payload.externalSearch4Enabled, true);
  assert.strictEqual(systemResult.payload.externalSearch4Label, "Search Local Catalog");
  assert.strictEqual(systemResult.payload.externalSearch4UrlTemplate, "https://catalog.example.test/search?q={{title}}");
  assert.strictEqual(systemResult.payload.systemNotEnabled, undefined);

  const enabledLibraryEvent = makeEvent("100");
  const enabledLibraryResult = publicConfig(enabledLibraryEvent);
  assert.strictEqual(enabledLibraryResult.code, 200);
  assert.strictEqual(enabledLibraryResult.payload.welcome, "library hello");
  assert.strictEqual(enabledLibraryResult.payload.systemNotEnabled, undefined);

  const disabledLibraryEvent = makeEvent("999");
  const disabledLibraryResult = publicConfig(disabledLibraryEvent);
  assert.strictEqual(disabledLibraryResult.code, 200);
  assert.strictEqual(disabledLibraryResult.payload.systemNotEnabled, true);
  assert.strictEqual(disabledLibraryResult.payload.systemNotEnabledMessage, "North Branch does not currently participate in this suggestion service.");

  console.log("config_routes publicConfig behavior tests passed.");
}

runTests();
