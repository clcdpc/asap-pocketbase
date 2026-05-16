# Plan 16.3 Summary

- Implemented `processAdditionalCopyTimeout(app, result)` in `lib/jobs.js` to automatically close open additional copy requests older than the configured timeout.
- Integrated it into `runScheduledHoldCheck(app)`.
