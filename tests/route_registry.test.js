const assert = require("assert");
const path = require("path");

global.__hooks = path.resolve(__dirname, "../pb_hooks");

const routeRegistryPath = path.resolve(__dirname, "../lib/route_registry.js");
const setupRoutesPath = path.resolve(__dirname, "../lib/setup_routes.js");
const patronRoutesPath = path.resolve(__dirname, "../lib/patron_routes.js");

function runTests() {
  const calls = [];
  global.routerAdd = function (method, routePath, handlerFn) {
    calls.push({ method, routePath, handlerFn });
  };

  const setupMock = {
    setupStatus(e) {
      return e.json(200, { route: "one" });
    }
  };
  const patronMock = {
    patronLogin(e) {
      return e.json(200, { route: "two" });
    }
  };

  const originalSetupCache = require.cache[setupRoutesPath];
  const originalPatronCache = require.cache[patronRoutesPath];
  const originalRouteRegistryCache = require.cache[routeRegistryPath];

  try {
    require.cache[setupRoutesPath] = {
      id: setupRoutesPath,
      filename: setupRoutesPath,
      loaded: true,
      exports: setupMock
    };
    require.cache[patronRoutesPath] = {
      id: patronRoutesPath,
      filename: patronRoutesPath,
      loaded: true,
      exports: patronMock
    };
    delete require.cache[routeRegistryPath];
    const routeRegistry = require(routeRegistryPath);

    routeRegistry.registerRoutes([
      { method: "GET", path: "/one", module: "setup_routes.js", handler: "setupStatus" },
      { method: "POST", path: "/two", module: "patron_routes.js", handler: "patronLogin" },
      {
        method: "GET",
        path: "/custom",
        customHandler(e) {
          return e.json(405, { message: "custom" });
        }
      }
    ]);
    // Regression: route callbacks must not depend on an outer `route` variable
    // or mutable route object fields at execution time.
    const mutableRoute = { method: "GET", path: "/mutable", module: "setup_routes.js", handler: "setupStatus" };
    routeRegistry.registerRoutes([mutableRoute]);
    mutableRoute.module = "patron_routes.js";
    mutableRoute.handler = "patronLogin";

    assert.strictEqual(calls.length, 4);
    assert.strictEqual(calls[0].method, "GET");
    assert.strictEqual(calls[0].routePath, "/one");
    assert.strictEqual(calls[1].method, "POST");
    assert.strictEqual(calls[1].routePath, "/two");
    assert.strictEqual(calls[2].method, "GET");
    assert.strictEqual(calls[2].routePath, "/custom");
    assert.strictEqual(calls[3].method, "GET");
    assert.strictEqual(calls[3].routePath, "/mutable");

    const event = {
      app: {
        logger() {
          return {
            error() {}
          };
        }
      },
      json(code, payload) {
        return { code, payload };
      }
    };
    const resultOne = calls[0].handlerFn(event);
    const resultTwo = calls[1].handlerFn(event);
    const resultCustom = calls[2].handlerFn(event);
    const resultMutable = calls[3].handlerFn(event);
    assert.deepStrictEqual(resultOne, { code: 200, payload: { route: "one" } });
    assert.deepStrictEqual(resultTwo, { code: 200, payload: { route: "two" } });
    assert.deepStrictEqual(resultCustom, { code: 405, payload: { message: "custom" } });
    assert.deepStrictEqual(resultMutable, { code: 200, payload: { route: "one" } });
  } finally {
    delete global.routerAdd;
    if (originalSetupCache) require.cache[setupRoutesPath] = originalSetupCache;
    else delete require.cache[setupRoutesPath];
    if (originalPatronCache) require.cache[patronRoutesPath] = originalPatronCache;
    else delete require.cache[patronRoutesPath];

    if (originalRouteRegistryCache) require.cache[routeRegistryPath] = originalRouteRegistryCache;
    else delete require.cache[routeRegistryPath];
  }

  console.log("route_registry tests passed.");
}

runTests();
