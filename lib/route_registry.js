function registerRoutes(routes) {
  (routes || []).forEach(function (route) {
    routerAdd(route.method, route.path, function (e) {
      return require(`${__hooks}/../lib/${route.module}`)[route.handler](e);
    });
  });
}

module.exports = {
  registerRoutes: registerRoutes
};
