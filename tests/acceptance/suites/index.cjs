'use strict';
module.exports = Object.fromEntries(require('./registry.json').map(name => [name, require('./' + name + '.cjs')]));
