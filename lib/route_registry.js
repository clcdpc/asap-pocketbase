function registerRoutes(routes) {
  (routes || []).forEach(function (routeDef) {
    var method = routeDef.method;
    var path = routeDef.path;
    var moduleName = routeDef.module || "";
    var handlerName = routeDef.handler || "";
    var customHandler = routeDef.customHandler;

    routerAdd(method, path, function (e) {
      try {
        if (typeof customHandler === "function") {
          return customHandler(e);
        }

        var mod = require(`${__hooks}/../lib/${moduleName}`);
        var handler = mod && mod[handlerName];

        if (typeof handler !== "function") {
          e.app.logger().error(
            "ASAP route handler not found",
            "method", method,
            "path", path,
            "module", moduleName,
            "handler", handlerName
          );
          return e.json(500, {
            message: "ASAP route handler not found.",
            module: moduleName,
            handler: handlerName
          });
        }

        return handler(e);
      } catch (err) {
        e.app.logger().error(
          "ASAP route handler failed",
          "method", method,
          "path", path,
          "module", moduleName,
          "handler", handlerName,
          "error", String(err)
        );
        return e.json(500, {
          message: String(err),
          route: method + " " + path,
          module: moduleName,
          handler: handlerName
        });
      }
    });
  });
}

module.exports = {
  registerRoutes: registerRoutes
};
