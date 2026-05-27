function registerRoutes(routes) {
  (routes || []).forEach(function (route) {
    routerAdd(route.method, route.path, function (e) {
      if (typeof route.customHandler === "function") {
        return route.customHandler(e);
      }
      return require(`${__hooks}/../lib/${route.module}`)[route.handler](e);
    });
  });
}

module.exports = {
  registerRoutes: registerRoutes
};
