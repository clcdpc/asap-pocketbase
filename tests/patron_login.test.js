const assert = require('assert');
const path = require('path');

// Mock __hooks globally for the required modules
global.__hooks = path.resolve(__dirname, "../pb_hooks");

const Module = require('module');
const originalRequire = Module.prototype.require;

let configMock = {
  getSettings: () => ({ enabledLibraryOrgIds: "1,2" }),
  librarySettings: (app, orgId) => ({
    ui_text: {
      systemNotEnabledMessage: "{{library}} does not currently participate in this suggestion service."
    }
  })
};

let polarisMock = {
  adminStaffAuth: () => ({}),
  authenticatePatron: (barcode, password, staffAuth) => ({})
};

let orgsMock = {
  attachPatronScope: (app, patron, staffAuth, logger) => patron
};

let routeUtilsMock = {
  body: (e) => e.requestBody || {}
};

Module.prototype.require = function(moduleName) {
  if (moduleName.includes("lib/config.js")) {
    return configMock;
  }
  if (moduleName.includes("lib/format_rules.js")) {
    return {};
  }
  if (moduleName.includes("lib/mail.js")) {
    return {};
  }
  if (moduleName.includes("lib/orgs.js")) {
    return orgsMock;
  }
  if (moduleName.includes("lib/polaris.js")) {
    return polarisMock;
  }
  if (moduleName.includes("lib/records.js")) {
    return {};
  }
  if (moduleName.includes("lib/route_utils.js")) {
    return routeUtilsMock;
  }
  if (moduleName.includes("lib/format_claim_rules.js")) {
    return {};
  }
  return originalRequire.apply(this, arguments);
};

const patronRoutes = require('../lib/patron_routes.js');

// Restore original require after importing the module under test
Module.prototype.require = originalRequire;

let passed = 0;
let failed = 0;

function runTest(name, testFn) {
  try {
    testFn();
    console.log(`✅ Test case "${name}" passed`);
    passed++;
  } catch (err) {
    console.error(`❌ Test case "${name}" failed`);
    console.error(`   ${err.stack || err.message || err}`);
    failed++;
  }
}

// Helper to mock the event object (e)
function createEventMock(requestBody) {
  let jsonStatus = null;
  let jsonPayload = null;

  const e = {
    requestBody: requestBody,
    app: {
      logger: () => ({
        error: () => {},
        warn: () => {}
      })
    },
    json: (status, payload) => {
      jsonStatus = status;
      jsonPayload = payload;
      return 'response';
    }
  };

  return {
    e: e,
    getResponse: () => ({ status: jsonStatus, payload: jsonPayload })
  };
}

runTest('patronLogin returns 400 when missing both barcode and PIN', () => {
  const { e, getResponse } = createEventMock({});
  patronRoutes.patronLogin(e);
  const { status } = getResponse();
  assert.strictEqual(status, 400);
});

runTest('participation warning replaces {{library}} placeholder', () => {
  polarisMock.authenticatePatron = () => ({
    LibraryOrgID: 3,
    LibraryOrgName: "Anytown Library"
  });

  const { e, getResponse } = createEventMock({ barcode: '123', pin: '456' });
  patronRoutes.patronLogin(e);
  
  const { status, payload } = getResponse();
  assert.strictEqual(status, 403);
  assert.strictEqual(payload.message, "Anytown Library does not currently participate in this suggestion service.");
});

runTest('participation warning replaces "Your library" for backward compatibility', () => {
  polarisMock.authenticatePatron = () => ({
    LibraryOrgID: 3,
    LibraryOrgName: "Anytown Library"
  });
  
  configMock.librarySettings = () => ({
    ui_text: {
      systemNotEnabledMessage: "Your library does not currently participate in this suggestion service."
    }
  });

  const { e, getResponse } = createEventMock({ barcode: '123', pin: '456' });
  patronRoutes.patronLogin(e);
  
  const { status, payload } = getResponse();
  assert.strictEqual(status, 403);
  assert.strictEqual(payload.message, "Anytown Library does not currently participate in this suggestion service.");
});

runTest('participation warning falls back to "Your library" if name is missing', () => {
  polarisMock.authenticatePatron = () => ({
    LibraryOrgID: 3,
    LibraryOrgName: ""
  });
  
  configMock.librarySettings = () => ({
    ui_text: {
      systemNotEnabledMessage: "{{library}} does not currently participate in this suggestion service."
    }
  });

  const { e, getResponse } = createEventMock({ barcode: '123', pin: '456' });
  patronRoutes.patronLogin(e);
  
  const { status, payload } = getResponse();
  assert.strictEqual(status, 403);
  assert.strictEqual(payload.message, "Your library does not currently participate in this suggestion service.");
});

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
