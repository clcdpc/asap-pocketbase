const assert = require('assert');
const path = require('path');

// Mock __hooks globally for the required modules
global.__hooks = path.resolve(__dirname, "../pb_hooks");

const Module = require('module');
const originalRequire = Module.prototype.require;

let configMock = {
  getSettings: () => ({ enabledLibraryOrgIds: "1,2" }),
  librarySettings: (app, orgId) => ({
    workflow: {},
    ui_text: {
      systemNotEnabledMessage: "{{library}} does not currently participate in this suggestion service."
    }
  })
};

let polarisMock = {
  adminStaffAuth: () => ({}),
  authenticatePatron: (barcode, password, staffAuth) => ({})
};
let pickupContextMock = {
  buildPickupPreferenceContext: () => ({
    pickupBranches: [],
    pickupBranchesRefreshedAt: "",
    currentPreferredPickupBranchId: "",
    currentPreferredPickupBranchName: "",
    selectedPickupBranchId: "",
    selectedPickupBranchName: "",
    currentPreferenceAllowed: false,
    pickupBranchWarning: ""
  })
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
    return {
      upsertPatronUser: () => ({
        id: "patron1",
        newAuthToken: () => "token"
      })
    };
  }
  if (moduleName.includes("lib/patron_session_contexts.js")) {
    return {
      createPatronSessionContext: () => ({ id: "ctx1" })
    };
  }
  if (moduleName.includes("lib/polaris/pickup_preference_context.js")) {
    return pickupContextMock;
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
    workflow: {},
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
    workflow: {},
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

runTest('pickup context load failure does not block valid login', () => {
  polarisMock.authenticatePatron = () => ({
    LibraryOrgID: 2,
    LibraryOrgName: "Anytown Library",
    PatronOrgID: "2"
  });
  configMock.getSettings = () => ({ enabledLibraryOrgIds: "2,3" });
  pickupContextMock.buildPickupPreferenceContext = () => {
    throw new Error("cache unavailable");
  };

  const { e, getResponse } = createEventMock({ barcode: '123', pin: '456' });
  patronRoutes.patronLogin(e);
  const { status, payload } = getResponse();
  assert.strictEqual(status, 200);
  assert.ok(Array.isArray(payload.pickupBranches));
  assert.strictEqual(payload.currentPreferenceAllowed, false);

  pickupContextMock.buildPickupPreferenceContext = () => ({
    pickupBranches: [],
    pickupBranchesRefreshedAt: "",
    currentPreferredPickupBranchId: "",
    currentPreferredPickupBranchName: "",
    selectedPickupBranchId: "",
    selectedPickupBranchName: "",
    currentPreferenceAllowed: false,
    pickupBranchWarning: ""
  });
});

runTest('patron code allow-list permits matching PatronCodeID', () => {
  polarisMock.authenticatePatron = () => ({
    LibraryOrgID: 2,
    LibraryOrgName: "Anytown Library",
    PatronOrgID: "2",
    PatronCodeID: 91
  });
  configMock.getSettings = () => ({ enabledLibraryOrgIds: "2,3" });
  configMock.librarySettings = () => ({
    workflow: {
      patronCodeEligibilityEnabled: true,
      allowedPatronCodeIds: "91,92",
      patronCodeEligibilityMessage: "Not eligible."
    },
    ui_text: {
      systemNotEnabledMessage: "{{library}} does not currently participate in this suggestion service."
    }
  });

  const { e, getResponse } = createEventMock({ barcode: '123', pin: '456' });
  patronRoutes.patronLogin(e);
  const { status } = getResponse();
  assert.strictEqual(status, 200);
});

runTest('patron code allow-list blocks non-matching PatronCodeID', () => {
  polarisMock.authenticatePatron = () => ({
    LibraryOrgID: 2,
    LibraryOrgName: "Anytown Library",
    PatronOrgID: "2",
    PatronCodeID: "44"
  });
  configMock.getSettings = () => ({ enabledLibraryOrgIds: "2,3" });
  configMock.librarySettings = () => ({
    workflow: {
      patronCodeEligibilityEnabled: true,
      allowedPatronCodeIds: "91,92",
      patronCodeEligibilityMessage: "This card cannot use suggestions."
    },
    ui_text: {
      systemNotEnabledMessage: "{{library}} does not currently participate in this suggestion service."
    }
  });

  const { e, getResponse } = createEventMock({ barcode: '123', pin: '456' });
  patronRoutes.patronLogin(e);
  const { status, payload } = getResponse();
  assert.strictEqual(status, 403);
  assert.strictEqual(payload.message, "This card cannot use suggestions.");
});

runTest('patron code eligibility allows login when Polaris omits PatronCodeID', () => {
  let warned = false;
  polarisMock.authenticatePatron = () => ({
    LibraryOrgID: 2,
    LibraryOrgName: "Anytown Library",
    PatronOrgID: "2"
  });
  configMock.getSettings = () => ({ enabledLibraryOrgIds: "2,3" });
  configMock.librarySettings = () => ({
    workflow: {
      patronCodeEligibilityEnabled: true,
      allowedPatronCodeIds: "91",
      patronCodeEligibilityMessage: "Not eligible."
    },
    ui_text: {
      systemNotEnabledMessage: "{{library}} does not currently participate in this suggestion service."
    }
  });

  const { e, getResponse } = createEventMock({ barcode: '123', pin: '456' });
  e.app.logger = () => ({
    error: () => {},
    warn: () => { warned = true; }
  });
  patronRoutes.patronLogin(e);
  const { status } = getResponse();
  assert.strictEqual(status, 200);
  assert.strictEqual(warned, true);
});

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
