'use strict';
const assert = require('node:assert/strict');

async function formatRuleGeometry(page, columns) {
  const geometry = await page.locator('#format-rules-editor').evaluate(editor => {
    const rect = node => {
      const r = node.getBoundingClientRect();
      return { x: r.x, y: r.y, width: r.width, height: r.height, right: r.right };
    };
    return { pane: rect(editor), viewport: innerWidth, scrollWidth: document.documentElement.scrollWidth,
      cards: [...editor.querySelectorAll('[data-rule-code]')].map(row => ({
        code: row.dataset.ruleCode, row: rect(row),
        buttons: [...row.querySelectorAll('.settings-rule-heading button')].map(rect),
        expanded: row.querySelector('[data-rule-toggle]').getAttribute('aria-expanded') === 'true',
        groups: [...row.querySelectorAll('.settings-format-field-group')].map(group => ({
          ...rect(group), mode: rect(group.querySelector('select')), label: rect(group.querySelector('input')),
          scrollWidth: group.scrollWidth, clientWidth: group.clientWidth
        }))
      })) };
  });
  assert.ok(geometry.scrollWidth <= geometry.viewport, 'No page overflow');
  for (const card of geometry.cards) {
    for (const button of card.buttons) {
      assert.ok(button.width >= 40 && button.height >= 40, 'Disclosure and actions have usable touch targets');
      assert.ok(button.x >= card.row.x && button.right <= card.row.right + 1, 'Header controls fit the card');
    }
    if (!card.expanded) continue;
    const groups = card.groups;
    assert.equal(groups.length, 4, 'All built-ins remain mounted in fixed order');
    if (columns === 2) {
      assert.ok(Math.abs(groups[0].y - groups[1].y) < 1 && groups[1].x > groups[0].x, 'Title and Author share the first row');
      assert.ok(Math.abs(groups[2].y - groups[3].y) < 1 && groups[2].y > groups[0].y, 'Identifier and Publication share the second row');
    } else {
      assert.ok(groups.every(group => Math.abs(group.x - groups[0].x) < 1), 'All field blocks use one column');
      assert.ok(groups.slice(1).every((group, index) => group.y > groups[index].y), 'Fields remain in reading order');
    }
    for (const group of groups) {
      assert.ok(group.scrollWidth <= group.clientWidth + 1, 'Field block has no internal overflow');
      assert.ok(group.mode.width >= 160 && group.label.width >= 160, 'Controls have readable widths');
      assert.ok(group.label.y > group.mode.y, 'Requirement and editable label form a vertical block');
      assert.ok(group.mode.right <= group.x + group.width + 1 && group.label.right <= group.x + group.width + 1, 'Controls fit their block');
    }
  }
  return geometry;
}

module.exports = { formatRuleGeometry };
