'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { createHash } = require('node:crypto');

// Used by the real application journeys, rather than an independently styled
// fixture. Keep the served stylesheet receipt beside the measured geometry.
module.exports = function settingsSpacingRecorder(page, artifactRoot) {
  const assets = [], pending = [], states = [];
  const digest = value => createHash('sha256').update(value).digest('hex');
  const source = fs.readFile(path.join(__dirname, '../../src/Asap.Web/Frontend/staff/styles.css')).then(digest);
  page.on('response', response => {
    if (new URL(response.url()).pathname !== '/staff/styles.css') return;
    pending.push(response.body().then(body => {
      const asset = { url: response.url(), status: response.status(), sha256: digest(body) };
      if (!assets.some(item => item.url === asset.url && item.sha256 === asset.sha256)) assets.push(asset);
    }));
  });
  return async state => {
    if (!await page.locator('#settings-form').isVisible()) return;
    await Promise.all(pending);
    const geometry = await page.evaluate(() => {
      const visible = node => node && !node.closest('[hidden]') && !node.classList.contains('sr-only') &&
        node.getClientRects().length > 0 && node.getBoundingClientRect().height > 0;
      const name = node => node.id || node.dataset.settingKey || node.dataset.templateKey ||
        node.dataset.organizationId || node.dataset.ruleCode || node.className || node.localName;
      const bounds = node => {
        const box = node.getBoundingClientRect();
        return Object.fromEntries(['top', 'right', 'bottom', 'left', 'width', 'height'].map(key => [key, Math.round(box[key] * 100) / 100]));
      };
      const layout = node => {
        const style = getComputedStyle(node);
        return { display: style.display, rowGap: style.rowGap, columnGap: style.columnGap,
          columns: style.gridTemplateColumns, position: style.position,
          marginTop: style.marginTop, marginBottom: style.marginBottom, hidden: node.hidden };
      };
      const panel = document.querySelector('.settings-panel:not([hidden])');
      const containers = [...document.querySelectorAll(
        '#settings-form, .settings-panel, .settings-domain-block, .settings-panel > .settings-fieldset, #patron-public-links, ' +
        '.patron-code-summary, .patron-public-notes, .settings-grid, .settings-timeout-rule, .settings-timeout-subgroup, ' +
        '.settings-staff-roster, .settings-staff-editor, .settings-organization-group, ' +
        '.settings-organization-reference-group, .settings-template-list, .settings-save-bar, ' +
        '.settings-staff-controls, .settings-template-details, .settings-template-admin, .settings-template-layout, ' +
        '.settings-template-fields, .settings-rule-details, .settings-rule-section, .settings-rule-fields')].filter(visible);
      const stacks = containers.map(container => {
        const children = [...container.children].filter(node => visible(node) && node.localName !== 'legend');
        return { container: name(container), bounds: bounds(container),
          domain: container.classList.contains('settings-domain-block'),
          grid: container.classList.contains('settings-grid'),
          layout: layout(container), children: children.map(node => ({ name: name(node), bounds: bounds(node), layout: layout(node) })),
          gaps: children.slice(1).map((node, index) => {
            const before = children[index].getBoundingClientRect(), after = node.getBoundingClientRect();
            const beforePosition = getComputedStyle(children[index]).position, afterPosition = getComputedStyle(node).position;
            return { before: name(children[index]), after: name(node),
              beforePosition, afterPosition,
              normalFlow: ![beforePosition, afterPosition].some(position => ['sticky', 'fixed', 'absolute'].includes(position)),
              vertical: after.left < before.right && before.left < after.right,
              gap: Math.round((after.top - before.bottom) * 100) / 100 };
          }) };
      });
      const actions = [...document.querySelectorAll('.settings-domain-heading, .settings-inline-actions, .settings-row-actions, .settings-staff-actions')]
        .filter(visible).map(group => {
          const children = [...group.children].filter(visible);
          return { group: name(group), gaps: children.slice(1).map((node, index) => {
            const before = children[index].getBoundingClientRect(), after = node.getBoundingClientRect();
            const horizontal = after.top < before.bottom && before.top < after.bottom;
            return { before: name(children[index]), after: name(node), direction: horizontal ? 'horizontal' : 'vertical',
              gap: Math.round((horizontal ? after.left - before.right : after.top - before.bottom) * 100) / 100 };
          }) };
        });
      const controls = [...document.querySelectorAll('#settings-form input:not([type="hidden"]), #settings-form textarea, #settings-form select, #settings-form button')].filter(visible).map(control => {
        const popup = control.closest('[popover]');
        const container = popup || control.closest('.settings-field, .settings-domain-field, .settings-template-field, .settings-staff-editor, .settings-fieldset, .settings-save-bar') || panel;
        let scroll = null;
        for (let ancestor = control.parentElement; popup && ancestor && popup.contains(ancestor); ancestor = ancestor.parentElement) {
          const style = getComputedStyle(ancestor);
          if (!['auto', 'scroll'].includes(style.overflowY) || ancestor.scrollHeight <= ancestor.clientHeight + 1) continue;
          const box = bounds(ancestor), scale = ancestor.getBoundingClientRect().width / ancestor.offsetWidth;
          const left = box.left + ancestor.clientLeft * scale, top = box.top + ancestor.clientTop * scale;
          scroll = { owner: name(ancestor), bounds: box, port: { left, top,
            right: left + ancestor.clientWidth * scale, bottom: top + ancestor.clientHeight * scale },
          top: ancestor.scrollTop, height: ancestor.scrollHeight, clientHeight: ancestor.clientHeight, overflowY: style.overflowY };
          break;
        }
        const box = bounds(control), x = (box.left + box.right) / 2, y = (box.top + box.bottom) / 2;
        const hit = document.elementFromPoint(x, y);
        return { control: control.id || control.getAttribute('aria-label') || name(control), bounds: bounds(control),
          container: name(container), containerBounds: bounds(container),
          position: getComputedStyle(control).position, scroll, focused: control === document.activeElement,
          centerInViewport: x >= 0 && x < innerWidth && y >= 0 && y < innerHeight,
          centerOwned: hit === control || control.contains(hit),
          disabled: control.matches(':disabled') };
      });
      const popups = [...panel.querySelectorAll('[popover]')].filter(visible).map(popup => ({ name: name(popup), bounds: bounds(popup) }));
      const disclosures = [...panel.querySelectorAll('.settings-rule-row, .settings-template-card, .settings-staff-row')].filter(visible).flatMap(card => {
        const heading = card.querySelector('.settings-rule-heading, .settings-template-toggle, .settings-staff-summary');
        const details = card.querySelector('.settings-rule-details, .settings-template-details, .settings-staff-editor');
        const content = details && [...details.children].find(visible);
        return heading && visible(details) && content ? [{ card: name(card), gap: bounds(content).top - bounds(heading).bottom }] : [];
      });
      const hiddenLeaks = [...document.querySelectorAll('#settings-form [hidden]')]
        .filter(node => node.getClientRects().length > 0).map(name);
      return { panel: panel.id, scope: document.getElementById('settings-scope').value,
        viewport: { width: innerWidth, height: innerHeight, devicePixelRatio, cssZoom: getComputedStyle(document.documentElement).zoom,
          dark: matchMedia('(prefers-color-scheme: dark)').matches, scrollWidth: document.documentElement.scrollWidth },
        stylesheets: [...document.styleSheets].map(sheet => sheet.href).filter(Boolean),
        draft: { dirty: !document.getElementById('settings-discard').hidden,
          title: document.getElementById('settings-save-title').textContent,
          detail: document.getElementById('settings-save-detail').textContent,
          attention: document.querySelector('.settings-save-bar').classList.contains('attention'),
          saveDisabled: document.getElementById('settings-save').disabled,
          inert: document.getElementById('settings-form').inert,
          busy: document.getElementById('settings-form').getAttribute('aria-busy') },
        hiddenLeaks, disclosures, stacks, actions, controls, popups };
    });
    states.push({ state, ...geometry });
    await fs.writeFile(path.join(artifactRoot, 'settings-spacing.json'), JSON.stringify({ sourceStylesheetSha256: await source, assets, states }, null, 2));
    const description = `${state}/${geometry.scope}/${geometry.panel}`;
    assert.ok(assets.length > 0, `${description}: actual stylesheet response is recorded`);
    for (const asset of assets) {
      assert.equal(asset.status, 200, description);
      assert.equal(asset.sha256, await source, `${description}: served CSS matches the measured source`);
    }
    assert.ok(geometry.stylesheets.some(url => assets.some(asset => asset.url === url)), `${description}: recorded CSS is loaded`);
    assert.ok(geometry.viewport.scrollWidth <= geometry.viewport.width, `${description}: page overflow`);
    assert.deepEqual(geometry.hiddenLeaks, [], `${description}: hidden controls remain hidden`);
    const scale = parseFloat(geometry.viewport.cssZoom) || 1;
    for (const stack of geometry.stacks) {
      // Sticky/fixed/absolute rectangles describe their current viewport
      // position, not a normal-flow sibling gap. Retain both positions in the
      // receipt; control ownership and popup/focus checks still apply below.
      for (const gap of stack.gaps.filter(gap => gap.vertical && gap.normalFlow)) {
        // The roster's column headings and rows intentionally share a border.
        if (stack.container === 'settings-staff-roster' &&
            gap.before === 'settings-staff-columns settings-staff-column-headings' && gap.after === 'settings-staff-users-list') continue;
        const detail = `${description}/${stack.container}: ${gap.before} → ${gap.after} (${gap.gap / scale}px)`;
        assert.ok(gap.gap / scale >= 7.8, `${detail}: independent vertical groups need at least 8px`);
        if (stack.container === 'settings-form' && gap.after.startsWith('settings-save-bar')) {
          assert.ok(gap.gap / scale >= 17.8, `${detail}: the static save footer keeps its 18px separation`);
        }
        if (stack.domain || ['patron-code-summary', 'patron-public-links'].includes(stack.container)) {
          assert.ok(gap.gap / scale <= 12.2, `${detail}: empty children must not reserve extra stack gaps`);
        }
      }
      if (stack.grid) {
        for (const child of stack.children.filter(child => child.name.startsWith('settings-domain-block'))) {
          assert.equal(child.layout.marginTop, '0px', `${description}: domain sections reuse their parent grid gap`);
          assert.equal(stack.layout.rowGap, '14px', `${description}: the Settings grid keeps its 14px gap`);
        }
      }
    }
    for (const group of geometry.actions) {
      for (const gap of group.gaps) {
        const minimum = group.group === 'settings-domain-heading' && gap.direction === 'horizontal' ? 14 : 8;
        assert.ok(gap.gap / scale >= minimum - .2, `${description}/${group.group}: ${gap.direction} action gap ${gap.gap / scale}px (minimum ${minimum}px)`);
      }
    }
    for (const disclosure of geometry.disclosures) {
      assert.ok(disclosure.gap / scale >= 7.8, `${description}/${disclosure.card}: expanded content has clear separation`);
    }
    for (const control of geometry.controls) {
      const box = control.bounds, owner = control.containerBounds;
      assert.ok(box.left >= -1 && box.right <= geometry.viewport.width + 1 &&
        box.left >= owner.left - 1 && box.right <= owner.right + 1,
      `${description}: ${control.control} fits its horizontal owner and viewport (${JSON.stringify(control)})`);
      if (control.scroll) {
        const port = control.scroll.port;
        assert.ok(box.left >= port.left - 1 && box.right <= port.right + 1,
          `${description}: ${control.control} fits its horizontal scrollport`);
        if (control.focused) {
          assert.ok(box.top >= Math.max(0, port.top) - 1 && box.bottom <= Math.min(geometry.viewport.height, port.bottom) + 1 && control.centerOwned,
            `${description}: the focused scroll option is fully visible and unobscured (${JSON.stringify(control)})`);
        }
      } else {
        assert.ok(box.top >= owner.top - 1 && box.bottom <= owner.bottom + 1,
          `${description}: ${control.control} fits its vertical owner (${JSON.stringify(control)})`);
      }
      if (control.focused && control.centerInViewport) {
        assert.equal(control.centerOwned, true, `${description}: the focused control is unobscured (${JSON.stringify(control)})`);
      }
    }
    for (const popup of geometry.popups) {
      const box = popup.bounds;
      assert.ok(box.left >= -1 && box.right <= geometry.viewport.width + 1 && box.top >= -1 && box.bottom <= geometry.viewport.height + 1,
        `${description}: ${popup.name} fits the viewport (${JSON.stringify(popup)})`);
    }
    return geometry;
  };
};
