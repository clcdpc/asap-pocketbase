'use strict';
const assert = require('node:assert/strict');

module.exports = async function verifySortKeyboard(page, grid) {
  const receipts = [];
  for (const header of (await grid.locator('th').all()).slice(0, 7)) {
    const name = await header.locator('.gridjs-th-content').textContent();
    const button = header.locator('button.gridjs-sort');
    const assertSort = async (direction, focused) => {
      await page.waitForFunction(({ name, direction }) => [...document.querySelectorAll('#request-grid th')]
        .find(node => node.querySelector('.gridjs-th-content').textContent === name)?.getAttribute('aria-sort') === direction, { name, direction });
      assert.equal(await button.getAttribute('aria-label'), `Sort ${name} ${direction === 'ascending' ? 'descending' : 'ascending'}`);
      assert.equal(await focused.evaluate(node => node === document.activeElement), true, `${name}: activation retains focus`);
      // Wait for the vendored Grid's debounced pipeline and DOM effects before
      // another real key press; do not repair focus or synthesize DOM events.
      await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => setTimeout(resolve, 120)))));
      return grid.locator('tbody .request-title-link').allTextContents();
    };
    await header.focus();
    await page.keyboard.press('Enter'); await assertSort('ascending', header);
    await page.keyboard.press('Enter'); await assertSort('descending', header);
    await page.keyboard.press('Shift+Enter'); await assertSort('none', header);
    // A pointer click focuses Grid's inner native button even though it is
    // omitted from the tab order. Enter must not also sort its parent header.
    await button.click();
    const ascending = await assertSort('ascending', button);
    await page.keyboard.press('Enter');
    const descending = await assertSort('descending', button);
    await page.keyboard.press('Enter');
    assert.deepEqual(await assertSort('ascending', button), ascending);
    await page.keyboard.press('Space');
    assert.deepEqual(await assertSort('descending', button), descending);
    if (name === 'Bibliographic details') {
      assert.ok(ascending.length > 1);
      assert.deepEqual(descending, [...ascending].reverse(), 'The actual rows change order, beyond ARIA state.');
    }
    await page.keyboard.press('Shift+Enter'); await assertSort('none', button);
    receipts.push({ name, headerEnter: true, buttonRepeatedEnter: true, space: true, pointer: true });
  }
  const actions = grid.locator('th').last();
  assert.equal(await actions.getAttribute('aria-sort'), null);
  assert.equal(await actions.locator('button').count(), 0, 'Actions remains unsortable.');
  return receipts;
};
