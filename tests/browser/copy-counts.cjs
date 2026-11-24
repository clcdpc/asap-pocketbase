'use strict';
const assert = require('node:assert/strict');

// Arm before a mutation so a stale label that happens to match cannot pass.
module.exports = function nextCopyCounts(page) {
  const response = page.waitForResponse(result =>
    new URL(result.url()).pathname === '/api/asap/staff/additional-copies/counts' && result.ok());
  return async () => {
    const { scope, openCount, closedCount } = await (await response).json();
    await page.waitForFunction(({ openCount, closedCount }) =>
      [...document.querySelectorAll('[data-copy-count]')].every(node =>
        node.textContent === ` (${node.dataset.copyCount === 'open' ? openCount : closedCount})`), { openCount, closedCount });
    assert.equal(await page.locator('#library-scope').inputValue(), scope);
  };
};
