const Module = require('module');
const path = require('path');

// Guarantee __hooks global is resolved consistently for the test suite
global.__hooks = path.resolve(__dirname, '../../pb_hooks');

class MockRecord {
  constructor(data) {
    this.data = data || {};
  }

  get(key) {
    return this.data[key];
  }

  getBool(key) {
    return !!this.data[key];
  }

  set(key, value) {
    this.data[key] = value;
  }

  get id() {
    return this.data.id;
  }
}

function createMockApp(options) {
  options = options || {};
  return {
    logger: function() {
      return {
        info: options.onInfo || function() {},
        warn: options.onWarn || function() {},
        error: options.onError || function() {}
      };
    },
    save: function(record) {
      if (options.onSave) {
        options.onSave(record);
      }
    },
    findCollectionByNameOrId: function(name) {
      return { name: name };
    },
    findRecordById: function(collectionName, id) {
      if (options.onFindRecordById) {
        return options.onFindRecordById(collectionName, id);
      }
      return new MockRecord({ id: id });
    },
    findFirstRecordByData: function(collection, field, value) {
      if (options.onFindFirstRecordByData) {
        return options.onFindFirstRecordByData(collection, field, value);
      }
      return null;
    },
    findFirstRecordByFilter: function(collection, filter, params) {
      if (options.onFindFirstRecordByFilter) {
        return options.onFindFirstRecordByFilter(collection, filter, params);
      }
      return null;
    },
    findRecordsByFilter: function(collection, filter, sort, limit, offset, params) {
      if (options.onFindRecordsByFilter) {
        return options.onFindRecordsByFilter(collection, filter, sort, limit, offset, params);
      }
      return [];
    },
    newMailClient: function() {
      return {
        send: function(message) {
          if (options.onMailSend) {
            options.onMailSend(message);
          }
        }
      };
    }
  };
}

function interceptRequire(mappings) {
  const originalRequire = Module.prototype.require;
  Module.prototype.require = function(moduleName) {
    const keys = Object.keys(mappings);
    for (var i = 0; i < keys.length; i++) {
      const key = keys[i];
      if (moduleName.includes(key)) {
        const target = mappings[key];
        return typeof target === 'function' ? target() : target;
      }
    }
    return originalRequire.apply(this, arguments);
  };
}

module.exports = {
  MockRecord: MockRecord,
  createMockApp: createMockApp,
  interceptRequire: interceptRequire,
};
