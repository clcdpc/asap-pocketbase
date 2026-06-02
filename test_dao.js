const path = require('path');
global.__hooks = path.resolve(__dirname, 'pb_hooks');
global.Record = function() {};

// To see if PocketBase's `$app.dao()` is exposed, let's look at pocketbase documentation or examples in the codebase.
