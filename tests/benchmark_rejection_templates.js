const assert = require("assert");
const path = require("path");
const { performance } = require("perf_hooks");

global.__hooks = path.resolve(__dirname, "../pb_hooks");
global.Record = function Record() {
  return record("saved_tpl_456", {});
};

const routes = require("../lib/staff_routes.js");

function record(id, fields) {
  return {
    id,
    get: function (key) {
      return fields[key];
    },
    set: function (key, value) {
      fields[key] = value;
    },
  };
}

const templatesCount = 1000;
const templates = [];
for (let i = 0; i < templatesCount; i++) {
  templates.push({ id: `tpl_${i}`, name: `Template ${i}`, subject: `Subject ${i}`, body: `Body ${i}` });
}

let appFindRecordByIdCalls = 0;
let appFindRecordsByFilterCalls = 0;

const app = {
  logger: () => ({ warn: () => {} }),
  findRecordById: function (collection, id) {
    appFindRecordByIdCalls++;
    // Simulate DB delay
    let start = performance.now();
    while (performance.now() - start < 0.1) {}
    return record(id, {});
  },
  findCollectionByNameOrId: function () {
    return {};
  },
  findRecordsByFilter: function (collection, filter, sort, limit, offset, params) {
    appFindRecordsByFilterCalls++;
    // Simulate DB delay (slightly more overhead but fewer calls)
    let start = performance.now();
    while (performance.now() - start < 1) {}
    let results = [];
    if (params) {
        Object.keys(params).forEach(k => {
            results.push(record(params[k], {}));
        });
    }
    return results;
  },
  findFirstRecordByFilter: function () {
    throw new Error("not found");
  },
  save: function () {},
  delete: function (row) {},
};

console.log(`Running baseline benchmark with ${templatesCount} templates...`);

const start = performance.now();
routes.saveRejectionTemplates(app, "system", "", templates);
const end = performance.now();

console.log(`findRecordById calls: ${appFindRecordByIdCalls}`);
console.log(`findRecordsByFilter calls: ${appFindRecordsByFilterCalls}`);
console.log(`Time taken: ${(end - start).toFixed(2)} ms`);
