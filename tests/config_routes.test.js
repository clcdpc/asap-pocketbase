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
  const app = {
    logger() {
      return { error() {} };
    }
  };
  return {
    app: app,
    requestInfo() {
      return {
        query: {
          libraryOrgId: libraryOrgId || ""
        }
      };
    },
    json(code, payload) {
      responseState.code = code;
      responseState.payload = payload;
      return responseState;
    }
  };
}

function runTests() {
  const originalUiText = { loginTitle: "Login" };
  const settings = {
    ui_text: originalUiText,
    workflow: { commonAuthorsEnabled: true }
  };

  const configMock = {
    getSettings(app) {
      return settings;
    },
    librarySettings(app, orgId) {
      return settings;
    }
  };

  const orgsMock = {};

  const { publicConfig } = loadConfigRoutesWithMocks(configMock, orgsMock);

  const event = makeEvent("");
  const result = publicConfig(event);

  assert.strictEqual(result.code, 200);
  assert.strictEqual(result.payload.loginTitle, "Login");
  assert.strictEqual(result.payload.commonAuthorsEnabled, true);

  // original settings.ui_text object is unchanged after calling publicConfig.
  assert.strictEqual(originalUiText.commonAuthorsEnabled, undefined);
  assert.strictEqual(originalUiText.externalSearch1UrlTemplate, undefined);

  console.log("config_routes publicConfig mutation tests passed.");
}

const originalConfigCache = require.cache[configPath];
const originalOrgsCache = require.cache[orgsPath];
const originalConfigRoutesCache = require.cache[configRoutesPath];

try {
  runTests();
} finally {
  if (originalConfigCache) require.cache[configPath] = originalConfigCache;
  else delete require.cache[configPath];

  if (originalOrgsCache) require.cache[orgsPath] = originalOrgsCache;
  else delete require.cache[orgsPath];

  if (originalConfigRoutesCache) require.cache[configRoutesPath] = originalConfigRoutesCache;
  else delete require.cache[configRoutesPath];
}
