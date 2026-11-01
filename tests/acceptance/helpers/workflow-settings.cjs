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

// Deliberately recreate native focus scrolling's blind spot: an input that is
// in the viewport, but half covered by the dirty Settings footer.
async function obscurePicker(page) {
  const overlap = await page.locator('#patron-code-picker').evaluate(input => {
    input.blur();
    const footer = document.querySelector('.settings-save-bar.attention');
    if (!footer) throw new Error('The regression requires an active dirty-state footer');
    const r = input.getBoundingClientRect();
    window.scrollBy(0, r.top - (footer.getBoundingClientRect().top - r.height / 2));
    return { input: input.getBoundingClientRect().toJSON(), footer: footer.getBoundingClientRect().toJSON(), scrollY };
  });
  assert.ok(overlap.input.bottom - overlap.footer.top > 1,
    'The regression explicitly starts with the input behind the sticky footer: ' + JSON.stringify(overlap));
}

async function pickerGeometry(page, { open = true, active = true } = {}) {
  const geometry = await page.locator('#patron-code-picker').evaluate(input => {
    const rect = node => {
      const r = node.getBoundingClientRect();
      return { left: r.left, top: r.top, right: r.right, bottom: r.bottom, width: r.width, height: r.height };
    };
    const visible = node => {
      const r = rect(node);
      return [0.1, 0.5, 0.9].every(x => [0.1, 0.5, 0.9].every(y =>
        node.contains(document.elementFromPoint(r.left + r.width * x, r.top + r.height * y))));
    };
    const list = document.querySelector('#patron-code-options');
    const footer = document.querySelector('.settings-save-bar.attention');
    const selected = document.getElementById(input.getAttribute('aria-activedescendant'));
    const viewport = window.visualViewport;
    const clipping = [];
    for (let node = list.parentElement; node; node = node.parentElement) {
      const style = getComputedStyle(node);
      if (/(auto|scroll|hidden|clip)/.test(style.overflowY)) clipping.push(rect(node));
    }
    return {
      viewport: { left: viewport?.offsetLeft || 0, top: viewport?.offsetTop || 0,
        right: (viewport?.offsetLeft || 0) + (viewport?.width || innerWidth),
        bottom: (viewport?.offsetTop || 0) + (viewport?.height || innerHeight) },
      input: rect(input), focused: document.activeElement === input, inputReachable: visible(input),
      expanded: input.getAttribute('aria-expanded'), list: list.hidden ? null : rect(list),
      listScrollWidth: list.scrollWidth, listClientWidth: list.clientWidth,
      optionsFit: [...list.children].every(option => option.scrollWidth <= option.clientWidth + 1),
      active: selected ? rect(selected) : null, activeReachable: selected ? visible(selected) : false,
      footer: footer ? rect(footer) : null,
      footerContent: footer ? [...footer.querySelectorAll('strong, span, button')]
        .filter(node => node.getClientRects().length).map(node => ({ ...rect(node), reachable: visible(node) })) : [],
      clipping, scrollX, scrollY, pageOverflow: document.documentElement.scrollWidth > innerWidth
    };
  });
  const fits = (r, bounds) => r.left >= bounds.left - 1 && r.right <= bounds.right + 1 &&
    r.top >= bounds.top - 1 && r.bottom <= bounds.bottom + 1;
  const intersects = (a, b) => a && b && a.left < b.right && a.right > b.left && a.top < b.bottom && a.bottom > b.top;
  assert.ok(geometry.footer, 'The sticky Unsaved changes footer is active');
  assert.ok(fits(geometry.footer, geometry.viewport), 'The complete footer remains in the visible viewport');
  assert.ok(fits(geometry.input, geometry.viewport), 'The complete focused input fits the visual viewport');
  assert.equal(intersects(geometry.input, geometry.footer), false, 'The input does not intersect the sticky footer');
  assert.equal(geometry.focused, true, 'DOM focus stays on the combobox');
  assert.equal(geometry.inputReachable, true, 'The input is unobstructed at its edges and center');
  assert.ok(geometry.footerContent.every(control => control.reachable), 'Footer status and controls remain unobstructed');
  assert.equal(geometry.pageOverflow, false, 'The page has no horizontal overflow');
  if (open) {
    assert.equal(geometry.expanded, 'true');
    assert.ok(geometry.list?.height > 2, 'The dropdown has usable visible height');
    assert.ok(fits(geometry.list, geometry.viewport), 'The dropdown fits the visual viewport');
    assert.equal(intersects(geometry.list, geometry.input), false, 'The dropdown does not cover its input');
    assert.equal(intersects(geometry.list, geometry.footer), false, 'The dropdown does not intersect the footer');
    assert.ok(geometry.clipping.every(bounds => fits(geometry.list, bounds)), 'No overflow ancestor clips the dropdown');
    assert.ok(geometry.listScrollWidth <= geometry.listClientWidth + 1 && geometry.optionsFit,
      'Suggestion text wraps without horizontal clipping');
    if (active) {
      assert.ok(geometry.active && fits(geometry.active, geometry.list) && geometry.activeReachable,
        'The ARIA active suggestion is completely visible and reachable');
    }
  } else {
    assert.equal(geometry.expanded, 'false');
    assert.equal(geometry.list, null);
    assert.equal(geometry.active, null);
  }
  return geometry;
}

module.exports = { providerGeometry, chipGeometry, obscurePicker, pickerGeometry };
