'use strict';
const assert = require('node:assert/strict');

module.exports = async function materialFormatsLayout(page) {
  const geometry = await page.evaluate(() => {
    const rect = element => element.getBoundingClientRect().toJSON();
    return ['material-formats', 'publication-options'].map(name => {
      const editor = document.getElementById(`${name}-editor`);
      return { name, width: editor.clientWidth, rows: [...editor.querySelectorAll(':scope > [data-domain-row]')].map(row => {
        const label = row.querySelector('input[type="text"]'), shown = row.querySelector('input[type="checkbox"]');
        const actions = row.querySelector('.settings-row-actions');
        return { row: rect(row), label: rect(label), shown: rect(shown.closest('label')),
          actions: rect(actions), buttons: [...actions.querySelectorAll('button')].map(button => ({ rect: rect(button), name: button.getAttribute('aria-label') })),
          overflow: row.scrollWidth > row.clientWidth + 1 };
      }) };
    });
  });
  for (const editor of geometry) {
    assert.ok(editor.rows.length > 0, `${editor.name}: fixture contains rows`);
    for (const row of editor.rows) {
      assert.equal(row.overflow, false, `${editor.name}: row has no overflow`);
      assert.ok(row.buttons.every(button => button.name && button.rect.width >= 39 && button.rect.height >= 39), `${editor.name}: named comfortable action targets`);
      if (editor.width >= 440) {
        assert.ok(row.actions.left >= row.label.right && row.actions.left >= row.shown.right, `${editor.name}: actions share the controls' row`);
        assert.ok(Math.abs(row.actions.top - row.label.top) <= 4, `${editor.name}: actions align with label input: ${JSON.stringify(row)}`);
      } else {
        assert.ok(row.actions.top >= row.shown.bottom - 1, `${editor.name}: actions intentionally wrap below the fields`);
      }
    }
  }
  const formats = geometry[0], publications = geometry[1];
  if (formats.width >= 440 && publications.width >= 440 &&
      !await page.locator('#material-formats-editor > .settings-format-row > .settings-inheritance').count()) {
    assert.ok(formats.rows[0].row.height <= publications.rows[0].row.height + 4, 'System format rows are as compact as Publication options.');
  }
  return geometry;
};
