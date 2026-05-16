// lib/staff/compose_exports.js
function composeExports(modules) {
  var merged = {};
  var owners = {};

  modules.forEach(function (entry) {
    var moduleName = entry.name;
    var exportsObject = entry.exports || {};

    Object.keys(exportsObject).forEach(function (key) {
      if (Object.prototype.hasOwnProperty.call(merged, key)) {
        throw new Error(
          "Duplicate staff export '" + key + "' from " + moduleName +
          "; already exported by " + owners[key]
        );
      }
      merged[key] = exportsObject[key];
      owners[key] = moduleName;
    });
  });

  return merged;
}

module.exports = {
  composeExports
};
