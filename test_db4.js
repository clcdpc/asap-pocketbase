const assert = require('assert');
const path = require('path');
global.__hooks = path.resolve(__dirname, 'pb_hooks');
global.Record = function() {};
const mockPb = require('./tests/helpers/mock_pb.js');

const app = mockPb.createMockApp();
console.log(app.db ? "Has app.db()" : "No app.db()");
console.log(app.dao ? "Has app.dao()" : "No app.dao()");
