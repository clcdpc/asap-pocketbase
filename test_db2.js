const assert = require('assert');
const path = require('path');
global.__hooks = path.resolve(__dirname, 'pb_hooks');
global.Record = function() {};
const settingsRoutes = require('./lib/staff/settings_routes.js');

const mockApp = {
    db: function() {
        return "db exists";
    }
};

console.log(mockApp.db());
