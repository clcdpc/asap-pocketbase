'use strict';
const assert = require('node:assert/strict');

async function settleActions(page) {
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
}

async function activateRowAction(row, key, activation = 'click') {
  const button = row.locator(`[data-request-action="${key}"]`);
  await button.waitFor({ state: 'attached' });
  if (await button.evaluate(node => Boolean(node.closest('[popover]')))) {
    const more = row.locator('.request-row-more');
    if (await more.getAttribute('aria-expanded') !== 'true') await more.click();
  }
  if (activation === 'click') await button.click();
  else await button.press(activation);
}

async function actionGeometry(row) {
  const result = await row.locator('.request-row-actions').evaluate(root => {
    const rect = node => node.getBoundingClientRect().toJSON();
    const controls = [...root.querySelectorAll(':scope > button')];
    return { bounds: rect(root), cell: rect(root.closest('td') || root), clientWidth: root.clientWidth,
      scrollWidth: root.scrollWidth, keys: [...root.querySelectorAll('[data-request-action]')].map(n => n.dataset.requestAction),
      inline: controls.filter(n => n.dataset.requestAction).map(n => n.dataset.requestAction),
      overflow: [...root.querySelectorAll('[popover] button')].map(n => n.dataset.requestAction),
      more: root.querySelectorAll('.request-row-more').length,
      controls: controls.map(node => {
        const range = document.createRange(); range.selectNodeContents(node);
        return { key: node.dataset.requestAction || 'more', bounds: rect(node),
          text: [...range.getClientRects()].map(r => r.toJSON()) };
      }) };
  });
  assert.equal(new Set(result.keys).size, result.keys.length, 'No duplicate commands');
  assert.equal(result.more, result.overflow.length ? 1 : 0, 'More exists only with actual overflow');
  assert.ok(!result.keys.length || result.inline.length >= 1, 'A meaningful action remains direct');
  assert.ok(result.scrollWidth <= result.clientWidth + 1, 'Controls do not overflow their action area');
  for (const { bounds: b, text } of result.controls) {
    assert.ok(b.left >= result.bounds.left - 1 && b.right <= result.bounds.right + 1, 'Button fits actual container');
    assert.ok(b.height >= 36, 'Compact touch target is preserved');
    for (const r of text) assert.ok(r.left >= b.left - 1 && r.right <= b.right + 1 && r.top >= b.top - 1 && r.bottom <= b.bottom + 1,
      'Visible label is complete');
  }
  for (let i = 0; i < result.controls.length; i++) {
    const a = result.controls[i].bounds;
    for (const { bounds: b } of result.controls.slice(i + 1)) {
      assert.ok(Math.min(a.right, b.right) - Math.max(a.left, b.left) <= 1 ||
        Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top) <= 1, 'Controls never overlap');
    }
  }
  return result;
}

async function actionColumn(page, percent = null) {
  await page.locator('#request-grid th').evaluateAll((headers, percent) => {
    headers.forEach((header, index) => {
      if (percent === null) {
        if (header.hasAttribute('data-i403-width')) {
          header.style.width = header.getAttribute('data-i403-width'); header.removeAttribute('data-i403-width');
        }
      } else {
        if (!header.hasAttribute('data-i403-width')) header.setAttribute('data-i403-width', header.style.width);
        header.style.width = `${index === headers.length - 1 ? percent : (100 - percent) / (headers.length - 1)}%`;
      }
    });
  }, percent);
  await settleActions(page);
}
module.exports = { settleActions, activateRowAction, actionGeometry, actionColumn };
