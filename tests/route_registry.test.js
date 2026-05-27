const assert = require("assert");
const path = require("path");

global.__hooks = path.resolve(__dirname, "../pb_hooks");

const routeRegistryPath = path.resolve(__dirname, "../lib/route_registry.js");
const setupRoutesPath = path.resolve(__dirname, "../lib/setup_routes.js");

function runTests() {
  const calls = [];
  global.routerAdd = function (method, routePath, handlerFn) {
    calls.push({ method, routePath, handlerFn });
  };

  const setupMock = {
    setupStatus(e) {
      return e.json(200, { ok: true });
    }
  };

  const originalSetupCache = require.cache[setupRoutesPath];
  const originalRouteRegistryCache = require.cache[routeRegistryPath];

  try {
    require.cache[setupRoutesPath] = {
      id: setupRoutesPath,
      filename: setupRoutesPath,
      loaded: true,
      exports: setupMock
    };
    delete require.cache[routeRegistryPath];
    const routeRegistry = require(routeRegistryPath);

    routeRegistry.registerRoutes([
      { method: "GET", path: "/api/asap/setup/status", module: "setup_routes.js", handler: "setupStatus" }
    ]);

    assert.strictEqual(calls.length, 1);
    assert.strictEqual(calls[0].method, "GET");
    assert.strictEqual(calls[0].routePath, "/api/asap/setup/status");

    const result = calls[0].handlerFn({
      json(code, payload) {
        return { code, payload };
      }
    });
    assert.deepStrictEqual(result, { code: 200, payload: { ok: true } });
  } finally {
    delete global.routerAdd;
    if (originalSetupCache) require.cache[setupRoutesPath] = originalSetupCache;
    else delete require.cache[setupRoutesPath];

    if (originalRouteRegistryCache) require.cache[routeRegistryPath] = originalRouteRegistryCache;
    else delete require.cache[routeRegistryPath];
  }

  console.log("route_registry tests passed.");
}

runTests();
