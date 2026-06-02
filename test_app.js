const assert = require('assert');
const path = require('path');
global.__hooks = path.resolve(__dirname, 'pb_hooks');
global.Record = function() {};
const settingsRoutes = require('./lib/staff/settings_routes.js');
console.log("Testing complete");
