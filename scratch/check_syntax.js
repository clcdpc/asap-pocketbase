const path = require('path');
global.__hooks = path.resolve(__dirname, '../pb_hooks');
try {
  const staffRoutes = require('../lib/staff_routes.js');
  console.log('Successfully loaded staff_routes.js');
} catch (err) {
  console.error('Failed to load staff_routes.js:', err);
  process.exit(1);
}
