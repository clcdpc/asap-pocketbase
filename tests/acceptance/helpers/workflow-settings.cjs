'use strict';
const assert = require('node:assert/strict');

// Shared measurements run against the shipped Settings DOM in Kestrel and WSLC.
async function providerGeometry(page) {
  const geometry = await page.locator('#external-search-provider-editor').evaluate(root => {
    const rect = node => {
      const r = node.getBoundingClientRect();
      return { left: r.left, top: r.top, right: r.right, bottom: r.bottom, width: r.width, height: r.height };
    };
    return { paneWidth: root.clientWidth, viewportWidth: innerWidth, rows: [...root.children]
      .filter(row => row.matches('.settings-provider-row')).map(row => ({
        bounds: rect(row), library: Boolean(row.querySelector('.settings-domain-override')),
        controls: [row.querySelector('input[type="text"]'), row.querySelector('input[type="url"]'),
          row.querySelector('.settings-domain-check'), row.querySelector('.settings-domain-override'),
          ...row.querySelectorAll('.settings-row-actions button')].filter(Boolean).map(rect),
        buttons: [...row.querySelectorAll('.settings-row-actions button')].map(rect),
        scrollWidth: row.scrollWidth, clientWidth: row.clientWidth
      })) };
  });
  for (const row of geometry.rows) {
    assert.ok(row.scrollWidth <= row.clientWidth + 1, 'Provider card has no horizontal overflow');
    for (const control of row.controls) {
      assert.ok(control.width > 0 && control.height > 0 && control.left >= row.bounds.left &&
        control.right <= row.bounds.right + 1 && control.top >= row.bounds.top && control.bottom <= row.bounds.bottom + 1,
      'Each provider control fits inside its card');
    }
    const tops = row.controls.map(control => control.top);
    if (geometry.paneWidth >= (row.library ? 820 : 720)) {
      assert.ok(Math.max(...tops) - Math.min(...tops) <= 4, 'Wide provider controls share one row');
      assert.ok(row.controls[1].width >= 240, 'Wide provider URL remains readable');
    } else {
      assert.ok(row.controls[1].width >= Math.min(240, row.bounds.width - 28), 'Wrapped provider URL uses available width');
      assert.ok(Math.max(...tops) - Math.min(...tops) > 4, 'Narrow provider controls deliberately wrap');
    }
    for (let index = 0; index < row.buttons.length; index++) {
      const button = row.buttons[index];
      assert.ok(button.width >= 40 && button.height >= 40, 'Provider actions retain their hit targets');
      if (index) {
        assert.ok(button.left - row.buttons[index - 1].right >= 7, 'Provider action buttons have visible gaps');
        assert.ok(Math.abs(button.top - row.buttons[index - 1].top) <= 1, 'Provider actions remain together');
      }
    }
  }
  return geometry;
}

async function chipGeometry(page) {
  const chips = await page.locator('#patron-codes-editor').evaluate(root => {
    const bounds = root.getBoundingClientRect();
    return [...root.children].map(chip => {
      const r = chip.getBoundingClientRect();
      return { id: chip.dataset.codeId, width: r.width, height: r.height,
        fits: r.left >= bounds.left - 1 && r.right <= bounds.right + 1 };
    });
  });
  assert.ok(chips.every(chip => chip.fits && chip.width > 0 && chip.height >= 40), 'Chips wrap inside their group');
  return chips;
}

module.exports = { providerGeometry, chipGeometry };
