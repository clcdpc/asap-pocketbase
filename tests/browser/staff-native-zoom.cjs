'use strict';
const { rejectNativeDialogs } = require('./confirmation.cjs');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');
const verifyMixedFieldLayout = require('./form-layout.cjs');
const { providerGeometry } = require('../acceptance/helpers/workflow-settings.cjs');
const { actionGeometry, activateRowAction } = require('../acceptance/helpers/request-actions.cjs');
const { formatRuleGeometry } = require('../acceptance/helpers/format-rules.cjs');
const { geometry: emailTemplateGeometry } = require('./email-template-cards.cjs');

// Chromium's tab API changes actual browser zoom. CSS zoom and pinch scaling
// do not exercise the same viewport, native controls or top-layer geometry.
module.exports = async function verifyStaffNativeZoom({ baseURL, artifacts, headers, sourceId, library, barcode }) {
  const states = [], errors = [], externalRequests = [];
  const extension = path.join(__dirname, 'native-zoom-extension');
  const axe = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  for (const width of [1280, 1440]) {
    const context = await chromium.launchPersistentContext(path.join(artifacts, `zoom-profile-${width}`), {
      channel: 'chromium', headless: true, viewport: null, extraHTTPHeaders: headers,
      args: [`--disable-extensions-except=${extension}`, `--load-extension=${extension}`, `--window-size=${width},900`]
    });
    try {
      await context.route('**/*', route => {
        if (new URL(route.request().url()).origin === new URL(baseURL).origin) return route.continue();
        externalRequests.push(route.request().url()); return route.abort();
      });
      const worker = context.serviceWorkers()[0] || await context.waitForEvent('serviceworker');
      const page = context.pages()[0] || await context.newPage();
      page.on('pageerror', error => errors.push(error.message)); rejectNativeDialogs(page, errors);
      await page.goto(`${baseURL}/staff/?scope=${library}`, { waitUntil: 'networkidle' });
      const ratio = await page.evaluate(() => devicePixelRatio);
      const applyNativeZoom = async () => {
        const zoom = await worker.evaluate(async origin => {
          const tab = (await chrome.tabs.query({})).find(tab => tab.url.startsWith(origin));
          if (!tab) throw new Error('The actual application tab must exist.');
          await chrome.tabs.setZoomSettings(tab.id, { mode: 'automatic', scope: 'per-tab' });
          await chrome.tabs.setZoom(tab.id, 2);
          return chrome.tabs.getZoom(tab.id);
        }, new URL(baseURL).origin);
        assert.equal(zoom, 2);
        await page.waitForFunction(prior => Math.abs(devicePixelRatio / prior - 2) < 0.01, ratio);
        return zoom;
      };
      const zoom = await applyNativeZoom();
      const scan = async (name, visible = null, viewportOnly = false) => {
        await page.evaluate(axe);
        const result = await page.evaluate(async () => ({
          violations: (await axe.run()).violations.filter(item => ['serious', 'critical'].includes(item.impact)),
          innerWidth, devicePixelRatio, visualViewportScale: visualViewport.scale,
          htmlZoom: getComputedStyle(document.documentElement).zoom, bodyZoom: getComputedStyle(document.body).zoom,
          overflow: document.documentElement.scrollWidth > innerWidth,
          images: [...document.images].filter(node => node.getClientRects().length).every(node => node.complete && node.naturalWidth > 0)
        }));
        assert.deepEqual(result.violations, [], `${width}/${name}: axe`);
        assert.equal(result.overflow, false, `${width}/${name}: page overflow`);
        assert.equal(result.images, true);
        assert.equal(result.visualViewportScale, 1);
        assert.equal(result.htmlZoom, '1'); assert.equal(result.bodyZoom, '1');
        assert.ok(Math.abs(result.devicePixelRatio / ratio - 2) < 0.01);
        if (visible) {
          // Axe may scroll the horizontal grid while inspecting other cells.
          // Capture this named workflow checkpoint with its action in view.
          await visible.scrollIntoViewIfNeeded();
          const bounds = await visible.boundingBox(), viewport = await page.evaluate(() => ({ width: innerWidth, height: innerHeight }));
          assert.ok(bounds && bounds.x >= 0 && bounds.y >= 0 && bounds.x + bounds.width <= viewport.width && bounds.y + bounds.height <= viewport.height);
        }
        const session = await context.newCDPSession(page);
        const metrics = await session.send('Page.getLayoutMetrics');
        assert.equal(metrics.visualViewport.zoom, 2);
        const capture = await session.send('Page.captureScreenshot', viewportOnly
          ? { format: 'png', captureBeyondViewport: false }
          : { format: 'png', captureBeyondViewport: true,
            clip: { x: 0, y: 0, width: metrics.contentSize.width, height: metrics.contentSize.height, scale: 1 } });
        await fs.writeFile(path.join(artifacts, `native-zoom-${width}-${name}.png`), Buffer.from(capture.data, 'base64'));
        await session.detach();
        states.push({ physicalWidth: width, name, actualBrowserZoom: zoom, ...result });
      };
      const title = page.locator(`a[data-request-id="${sourceId}"]`);
      await title.waitFor();
      const wrapper = page.locator('#request-grid .gridjs-wrapper');
      await wrapper.focus(); await page.keyboard.press('ArrowRight');
      assert.equal(await wrapper.getAttribute('role'), 'region');
      const more = page.getByRole('button', { name: `More actions for request ${sourceId}`, exact: true });
      const row = title.locator('xpath=ancestor::tr');
      const geometry = await actionGeometry(row);
      assert.equal(geometry.inline[0], 'purchase', 'Suggestions lead with their eligible workflow step');
      assert.ok(geometry.inline.length >= 1 && geometry.overflow.length >= 1, 'Zoom retains direct commands plus only the remainder');
      await more.focus(); await page.keyboard.press('Enter');
      assert.equal(await more.getAttribute('aria-expanded'), 'true');
      await page.evaluate(axe);
      assert.deepEqual(await page.evaluate(async id => (await axe.run(document.getElementById(`request-row-${id}-actions`)))
        .violations.filter(item => ['serious', 'critical'].includes(item.impact)), sourceId), []);
      await page.keyboard.press('Escape');
      assert.equal(await more.evaluate(node => node === document.activeElement), true);
      await more.press('Space');
      assert.equal(await more.evaluate(node => {
        const wrapper = node.closest('.gridjs-wrapper');
        wrapper.scrollLeft = 0;
        return node.getBoundingClientRect().left >= wrapper.getBoundingClientRect().right;
      }), true, 'The native-zoom grid scroll fully clips the open menu trigger');
      await page.waitForFunction(id => document.querySelector(`#request-row-${id}-actions`)
        ?.matches(':popover-open') === false, sourceId);
      assert.equal(await more.evaluate(node => node === document.activeElement), true);
      assert.equal(await more.evaluate(node => {
        const bounds = node.getBoundingClientRect(), clip = node.closest('.gridjs-wrapper').getBoundingClientRect();
        const hit = document.elementFromPoint((bounds.left + bounds.right) / 2, (bounds.top + bounds.bottom) / 2);
        return bounds.left >= Math.max(0, clip.left) && bounds.right <= Math.min(innerWidth, clip.right) &&
          bounds.top >= Math.max(0, clip.top) && bounds.bottom <= Math.min(innerHeight, clip.bottom) &&
          (hit === node || node.contains(hit));
      }), true, 'Dismissal restores visible, unobscured trigger focus after full clipping');
      // The global axe scan may scroll the table and correctly dismiss an
      // anchored popup. Verify Escape before that independent full-page audit.
      await scan('table');
      await page.locator('#status-tabs [data-status="closed"]').click();
      await page.locator('#request-search').fill('Status transition closed 00');
      const closedTitle = page.locator('#request-grid .request-title-link').filter({ hasText: 'Status transition closed 00' });
      await closedTitle.waitFor();
      const closedRow = closedTitle.locator('xpath=ancestor::tr');
      assert.deepEqual((await actionGeometry(closedRow)).inline, ['reopen']);
      assert.equal(await closedRow.locator('.request-row-more, [popover]').count(), 0);
      await scan('closed-actions', closedRow.locator('[data-request-action="reopen"]'));
      await activateRowAction(closedRow, 'reopen', 'Space');
      await page.locator('#confirmation-dialog[open]').getByRole('button', { name: 'Cancel', exact: true }).click();
      await page.keyboard.press('Escape'); await page.locator('#request-dialog').waitFor({ state: 'hidden' });
      await page.locator('#status-tabs [data-status="suggestion"]').click();
      await page.locator('#request-search').fill(''); await title.waitFor();
      await title.focus(); await page.keyboard.press('Enter');
      const detail = page.locator('#request-dialog[open]');
      await detail.waitFor();
      await detail.getByLabel('Title', { exact: true }).fill('Unsaved native zoom draft');
      await detail.getByRole('tab', { name: 'Activity', exact: true }).click();
      assert.equal(await detail.locator('.request-activity li').count(), 80);
      assert.equal(await detail.locator('.request-save-state').textContent(), 'Unsaved changes');
      const footer = await detail.locator('.request-save-footer').boundingBox();
      assert.ok(footer.y >= 0 && footer.y + footer.height <= await page.evaluate(() => innerHeight), 'The save footer remains in the actual zoomed viewport.');
      await scan('detail');
      await detail.getByRole('button', { name: 'Revert changes', exact: true }).click();
      const confirmation = page.locator('#confirmation-dialog[open]');
      await confirmation.waitFor();
      assert.equal(await confirmation.getByRole('button', { name: 'Keep editing', exact: true }).evaluate(node => node === document.activeElement), true);
      await scan('confirmation');
      await page.keyboard.press('Tab');
      assert.equal(await confirmation.getByRole('button', { name: 'Revert changes', exact: true }).evaluate(node => node === document.activeElement), true);
      await page.keyboard.press('Tab');
      assert.equal(await confirmation.getByRole('button', { name: 'Keep editing', exact: true }).evaluate(node => node === document.activeElement), true);
      await page.keyboard.press('Escape');
      await confirmation.waitFor({ state: 'hidden' });
      assert.equal(await detail.getByRole('button', { name: 'Revert changes', exact: true }).evaluate(node => node === document.activeElement), true);
      assert.equal(await detail.locator('.request-save-state').textContent(), 'Unsaved changes');
      await detail.getByRole('button', { name: 'Revert changes', exact: true }).click();
      await confirmation.getByRole('button', { name: 'Revert changes', exact: true }).click();
      await page.waitForFunction(() => document.querySelector('#request-dialog .request-save-state').textContent === 'All changes saved');
      assert.equal(await detail.locator('.request-save-state').textContent(), 'All changes saved');
      await page.keyboard.press('Escape');
      await page.getByRole('button', { name: `New suggestion for patron ${barcode} from request ${sourceId}`, exact: true }).click();
      const suggestion = page.locator('#staff-suggestion-dialog[open]');
      await suggestion.getByRole('button', { name: 'Create suggestion', exact: true }).waitFor();
      await verifyMixedFieldLayout(page, '#staff-suggestion-body');
      await scan('new-suggestion');
      await page.keyboard.press('Escape');
      await page.goto(`${baseURL}/staff/?stage=settings&settingsScope=system#settings-patron`, { waitUntil: 'networkidle' });
      // Chromium resets per-tab zoom on a full navigation. Exercise Settings
      // at actual 200% as well, with the same DPR/CDP assertions below.
      await applyNativeZoom();
      await page.waitForFunction(() => document.querySelector('#settings-version').value && !document.querySelector('#settings-form').inert);
      await page.locator('#settings-nav-workflow').click();
      await providerGeometry(page);
      const inherited = page.locator('#patron-codes-use-system');
      if (await inherited.isVisible() && await inherited.isChecked()) await inherited.uncheck();
      const picker = page.locator('#patron-code-picker');
      await picker.fill('Adult');
      await picker.press('ArrowDown');
      await scan('workflow-picker');
      await picker.press('Escape');
      assert.equal(await picker.evaluate(node => node === document.activeElement), true);
      await page.locator('#settings-nav-patron').click();
      const formatRule = page.locator('#format-rules-editor [data-rule-code]').first();
      await formatRule.evaluate(node => node.scrollIntoView({ block: 'start' }));
      await scan('format-rules-collapsed');
      await formatRule.locator('[data-rule-toggle]').press('Enter');
      await formatRuleGeometry(page, 1);
      await scan('format-rules-expanded');
      const message = page.locator('#patron-success-message');
      await message.fill('Before selected after');
      await message.evaluate(node => node.setSelectionRange(7, 15));
      const insert = page.getByRole('button', { name: 'Insert placeholder into Success message', exact: true });
      await insert.focus(); await page.keyboard.press('Enter');
      await page.locator('#patron-success-message-placeholders [data-patron-placeholder="barcode_label"]').press('Enter');
      assert.equal(await message.inputValue(), 'Before {{barcode_label}} after');
      assert.equal(await message.evaluate(node => node === document.activeElement), true);
      await insert.focus(); await page.keyboard.press('Enter');
      await scan('patron-placeholder');
      await page.keyboard.press('Escape');
      assert.equal(await insert.evaluate(node => node === document.activeElement), true);
      await page.locator('#settings-nav-templates').click();
      const emailCard = page.locator('[data-template-key="suggestion_submitted"]');
      assert.equal(await emailCard.locator('[data-template-toggle]').getAttribute('aria-expanded'), 'false');
      await emailCard.locator('[data-template-toggle]').press('Enter');
      await page.locator('#email-submit-subject').fill('An example for {{name}}: {{title}}');
      await page.locator('#email-submit-body').fill('Hello {{firstName}},\n\nYour suggestion: {{title}}\n{{unknown}}');
      assert.equal(await emailCard.locator('.settings-template-preview-body').textContent(),
        'Hello Alex,\n\nYour suggestion: Example title\n{{unknown}}');
      try { await emailTemplateGeometry(emailCard, true); }
      catch (error) {
        const diagnosticSession = await context.newCDPSession(page);
        const capture = await diagnosticSession.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
        await fs.writeFile(path.join(artifacts, `native-zoom-${width}-email-template-geometry-failure.png`), Buffer.from(capture.data, 'base64'));
        await diagnosticSession.detach();
        throw error;
      }
      const emailSubject = page.locator('#email-submit-subject');
      const emailBody = page.locator('#email-submit-body');
      const bodyInsert = emailCard.getByRole('button', { name: 'Insert placeholder into Submission confirmation Body', exact: true });
      await emailSubject.focus();
      const subjectFocused = await emailSubject.evaluate(node => node === document.activeElement);
      await page.keyboard.press('Tab');
      const bodyTriggerFocused = await bodyInsert.evaluate(node => node === document.activeElement);
      await page.keyboard.press('Tab');
      const focusDiagnostic = await emailBody.evaluate(node => {
        const rect = element => {
          const bounds = element.getBoundingClientRect();
          return { left: bounds.left, right: bounds.right, top: bounds.top, bottom: bounds.bottom,
            width: bounds.width, height: bounds.height };
        };
        const field = rect(node), barNode = document.querySelector('.settings-save-bar'), bar = rect(barNode);
        const style = getComputedStyle(node), lineHeight = parseFloat(style.lineHeight) || parseFloat(style.fontSize) * 1.2;
        const lines = node.value.slice(0, node.selectionEnd).split('\n');
        const canvas = document.createElement('canvas'), measure = canvas.getContext('2d');
        measure.font = `${style.fontStyle} ${style.fontWeight} ${style.fontSize} ${style.fontFamily}`;
        const point = (x, y) => {
          const hit = document.elementFromPoint(x, y);
          return { x, y, inViewport: x >= 0 && x < innerWidth && y >= 0 && y < innerHeight,
            owned: hit === node || node.contains(hit),
            hit: hit ? { tag: hit.localName, id: hit.id, className: String(hit.className) } : null };
        };
        const caretLeft = field.left + parseFloat(style.borderLeftWidth) + parseFloat(style.paddingLeft) +
          measure.measureText(lines.at(-1)).width - node.scrollLeft;
        const caretTop = field.top + parseFloat(style.borderTopWidth) + parseFloat(style.paddingTop) +
          (lines.length - 1) * lineHeight - node.scrollTop;
        const overlapWidth = Math.max(0, Math.min(field.right, bar.right) - Math.max(field.left, bar.left));
        const overlapHeight = Math.max(0, Math.min(field.bottom, bar.bottom) - Math.max(field.top, bar.top));
        return { activeBody: document.activeElement === node, field,
          fieldSample: point((field.left + field.right) / 2, (field.top + field.bottom) / 2),
          caret: { left: caretLeft, top: caretTop, height: lineHeight,
            sample: point(caretLeft + 1, caretTop + lineHeight / 2) },
          selection: [node.selectionStart, node.selectionEnd], scrollTop: node.scrollTop, scrollLeft: node.scrollLeft,
          textFitsWithoutWrapping: lines.every(line => measure.measureText(line).width <=
            node.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight)),
          stickyBar: { ...bar, position: getComputedStyle(barNode).position,
            fieldOverlapFraction: overlapWidth * overlapHeight / (field.width * field.height) },
          viewport: { width: innerWidth, height: innerHeight, devicePixelRatio, scale: visualViewport.scale,
            htmlZoom: getComputedStyle(document.documentElement).zoom, bodyZoom: getComputedStyle(document.body).zoom } };
      });
      const focusSession = await context.newCDPSession(page);
      const focusMetrics = await focusSession.send('Page.getLayoutMetrics');
      const focusCapture = await focusSession.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
      await fs.writeFile(path.join(artifacts, `native-zoom-${width}-email-template-focus.png`), Buffer.from(focusCapture.data, 'base64'));
      const focusReceipt = { physicalWidth: width, browserZoom: focusMetrics.visualViewport.zoom,
        subjectFocused, bodyTriggerFocused, ...focusDiagnostic };
      await fs.writeFile(path.join(artifacts, `native-zoom-${width}-email-template-focus.json`), JSON.stringify(focusReceipt, null, 2));
      await focusSession.detach();
      const focusDetails = JSON.stringify(focusReceipt);
      assert.equal(subjectFocused, true, focusDetails);
      assert.equal(bodyTriggerFocused, true, focusDetails);
      assert.equal(focusDiagnostic.activeBody, true, focusDetails);
      assert.equal(focusMetrics.visualViewport.zoom, 2, focusDetails);
      assert.ok(Math.abs(focusDiagnostic.viewport.devicePixelRatio / ratio - 2) < .01, focusDetails);
      assert.equal(focusDiagnostic.viewport.scale, 1, focusDetails);
      assert.equal(focusDiagnostic.viewport.htmlZoom, '1', focusDetails);
      assert.equal(focusDiagnostic.viewport.bodyZoom, '1', focusDetails);
      assert.equal(focusDiagnostic.textFitsWithoutWrapping, true, focusDetails);
      assert.equal(focusDiagnostic.fieldSample.inViewport && focusDiagnostic.fieldSample.owned, true, focusDetails);
      assert.equal(focusDiagnostic.caret.sample.inViewport && focusDiagnostic.caret.sample.owned, true, focusDetails);
      await scan('email-template-preview', null, true);
      const emailInsert = emailCard.getByRole('button', { name: 'Insert placeholder into Submission confirmation Subject', exact: true });
      await emailInsert.scrollIntoViewIfNeeded();
      await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      await emailInsert.focus(); await emailInsert.press('Enter');
      const emailPopup = page.locator('#email-submit-subject-placeholders');
      assert.equal(await emailPopup.evaluate(node => node.matches(':popover-open')), true);
      const pickerScroll = await emailPopup.evaluate(node => {
        const before = node.scrollTop;
        node.scrollTop = node.scrollHeight;
        return { before, after: node.scrollTop, max: node.scrollHeight - node.clientHeight };
      });
      assert.ok(pickerScroll.max > 0 && pickerScroll.after > pickerScroll.before,
        'The real 200% picker has a scrollable list/reference.');
      await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      assert.equal(await emailPopup.evaluate(node => node.matches(':popover-open')), true,
        'Scrolling inside the real narrow picker preserves its open state.');
      await emailPopup.evaluate(node => { node.scrollTop = 0; });
      await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      const emailBounds = await emailPopup.boundingBox();
      const viewport = await page.evaluate(() => ({ width: innerWidth, height: innerHeight }));
      assert.ok(emailBounds && emailBounds.x >= 0 && emailBounds.y >= 0 &&
        emailBounds.x + emailBounds.width <= viewport.width && emailBounds.y + emailBounds.height <= viewport.height);
      await scan('email-template-picker', null, true);
      assert.equal(await emailPopup.evaluate(node => node.matches(':popover-open')), true,
        'The actual 200% viewport image includes the open email picker.');
      await page.keyboard.press('Escape');
      assert.equal(await emailInsert.evaluate(node => node === document.activeElement), true);
    } finally { await context.close(); }
  }
  assert.deepEqual(errors, []); assert.deepEqual(externalRequests, []);
  return states;
};

// The Staff roster runs from its own browser journey, using the same real
// extension zoom boundary without rerunning the unrelated workflow scenarios.
module.exports.verifyRoster = async function verifyStaffRosterNativeZoom({ baseURL, artifacts, headers, activeId, inactiveId }) {
  const states = [], errors = [], externalRequests = [];
  const extension = path.join(__dirname, 'native-zoom-extension');
  const axe = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  for (const width of [1280, 1440]) {
    const context = await chromium.launchPersistentContext(path.join(artifacts, `roster-zoom-profile-${width}`), {
      channel: 'chromium', headless: true, viewport: null, extraHTTPHeaders: headers,
      args: [`--disable-extensions-except=${extension}`, `--load-extension=${extension}`, `--window-size=${width},900`]
    });
    try {
      await context.route('**/*', route => {
        if (new URL(route.request().url()).origin === new URL(baseURL).origin) return route.continue();
        externalRequests.push(route.request().url()); return route.abort();
      });
      const worker = context.serviceWorkers()[0] || await context.waitForEvent('serviceworker');
      const page = context.pages()[0] || await context.newPage();
      page.on('pageerror', error => errors.push(error.message)); rejectNativeDialogs(page, errors);
      await page.goto(`${baseURL}/staff/?stage=settings&settingsScope=system#settings-staff`, { waitUntil: 'networkidle' });
      await page.waitForFunction(() => document.getElementById('settings-version').value && !document.getElementById('staff-refresh').disabled);
      const ratio = await page.evaluate(() => devicePixelRatio);
      const zoom = await worker.evaluate(async origin => {
        const tab = (await chrome.tabs.query({})).find(tab => tab.url.startsWith(origin));
        if (!tab) throw new Error('The actual application tab must exist.');
        await chrome.tabs.setZoomSettings(tab.id, { mode: 'automatic', scope: 'per-tab' });
        await chrome.tabs.setZoom(tab.id, 2);
        return chrome.tabs.getZoom(tab.id);
      }, new URL(baseURL).origin);
      assert.equal(zoom, 2);
      await page.waitForFunction(prior => Math.abs(devicePixelRatio / prior - 2) < .01, ratio);
      const row = page.locator(`.settings-staff-row[data-staff-id="${activeId}"]`);
      const inactive = page.locator(`.settings-staff-row[data-staff-id="${inactiveId}"]`);
      const filter = page.getByRole('checkbox', { name: 'Show inactive accounts', exact: true });
      const scanRoster = async (name, focused) => {
        await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
        // Capture the keyboard/editing result before axe can scroll the page.
        // A manual scroll here would hide focus occlusion from the dirty bar.
        const focus = await focused.evaluate(control => {
          const bounds = control.getBoundingClientRect(), x = (bounds.left + bounds.right) / 2, y = (bounds.top + bounds.bottom) / 2;
          const hit = document.elementFromPoint(x, y);
          const style = getComputedStyle(control), outlineExtent = control.matches(':focus-visible')
            ? Math.max(0, (parseFloat(style.outlineWidth) || 0) + (parseFloat(style.outlineOffset) || 0)) : 0;
          return { active: control === document.activeElement, inViewport: x >= 0 && x < innerWidth && y >= 0 && y < innerHeight,
            unobscured: hit === control || control.contains(hit), center: { x, y },
            bounds: { left: bounds.left, right: bounds.right, top: bounds.top, bottom: bounds.bottom },
            outlineExtent, fullVerticalRequired: bounds.height + outlineExtent * 2 <= innerHeight,
            fullVertical: bounds.top - outlineExtent >= 0 && bounds.bottom + outlineExtent <= innerHeight,
            hit: hit ? { tag: hit.localName, id: hit.id, className: String(hit.className) } : null };
        });
        const session = await context.newCDPSession(page), metrics = await session.send('Page.getLayoutMetrics');
        const capture = await session.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
        await fs.writeFile(path.join(artifacts, `native-zoom-${width}-${name}.png`), Buffer.from(capture.data, 'base64'));
        await session.detach();
        await page.evaluate(axe);
        const violations = await page.evaluate(async () => (await axe.run()).violations.filter(item => ['serious', 'critical'].includes(item.impact)));
        const geometry = await page.evaluate(() => {
          const controls = [...document.querySelectorAll('.settings-staff-roster input, .settings-staff-roster select, .settings-staff-roster button')]
            .filter(control => control.getBoundingClientRect().height > 0).map(control => {
              const bounds = control.getBoundingClientRect();
              const owner = control.closest('.settings-staff-row, .settings-staff-toolbar, .settings-staff-filter, #staff-add-form').getBoundingClientRect();
              return { name: control.getAttribute('aria-label') || control.id || control.textContent.trim(),
                contained: bounds.left >= owner.left - 1 && bounds.right <= owner.right + 1 && bounds.left >= -1 && bounds.right <= innerWidth + 1 };
            });
          return { controls, width: innerWidth, height: innerHeight, devicePixelRatio, visualViewportScale: visualViewport.scale,
            htmlZoom: getComputedStyle(document.documentElement).zoom, bodyZoom: getComputedStyle(document.body).zoom,
            overflow: document.documentElement.scrollWidth > innerWidth,
            images: [...document.images].filter(image => image.getClientRects().length).every(image => image.complete && image.naturalWidth > 0) };
        });
        const receipt = { physicalWidth: width, name, actualBrowserZoom: zoom, cdpZoom: metrics.visualViewport.zoom,
          violations, ...geometry, focus };
        const details = `${width}/${name}: ${JSON.stringify(receipt)}`;
        assert.equal(metrics.visualViewport.zoom, 2, details);
        assert.ok(Math.abs(geometry.devicePixelRatio / ratio - 2) < .01, details);
        assert.equal(geometry.visualViewportScale, 1, details); assert.equal(geometry.htmlZoom, '1', details); assert.equal(geometry.bodyZoom, '1', details);
        assert.deepEqual(violations, [], details); assert.equal(geometry.overflow, false, details); assert.equal(geometry.images, true, details);
        assert.ok(geometry.controls.length > 0, details); assert.deepEqual(geometry.controls.filter(control => !control.contained), [], details);
        assert.equal(focus.active, true, details); assert.equal(focus.inViewport, true, details); assert.equal(focus.unobscured, true, details);
        if (focus.fullVerticalRequired) assert.equal(focus.fullVertical, true, details);
        states.push(receipt);
      };
      assert.equal(await filter.isChecked(), false); assert.equal(await inactive.isHidden(), true);
      assert.equal(await page.locator('.settings-staff-summary[aria-expanded="true"]').count(), 0);
      const summary = row.locator('.settings-staff-summary');
      await summary.focus(); await scanRoster('staff-roster-collapsed', summary);
      await page.keyboard.press('Enter'); await page.keyboard.press('Tab');
      const email = row.getByLabel(/^Email address/);
      assert.equal(await email.evaluate(input => input === document.activeElement), true);
      await email.fill('native.roster.draft@example.org');
      await scanRoster('staff-roster-expanded', email);
      await filter.focus(); await page.keyboard.press('Space');
      await inactive.locator('.settings-staff-summary').press('Enter');
      const inactiveEmail = inactive.getByLabel(/^Email address/), mounted = await inactiveEmail.elementHandle();
      await inactiveEmail.fill('invalid-native-inactive');
      await filter.evaluate(input => { input.checked = false; input.dispatchEvent(new Event('change', { bubbles: true })); });
      assert.equal(await inactive.isHidden(), true); assert.equal(await mounted.evaluate(input => input.willValidate), false);
      await scanRoster('staff-roster-filter-hidden', filter);
      await page.keyboard.press('Space');
      assert.equal(await inactiveEmail.inputValue(), 'invalid-native-inactive');
      assert.equal(await mounted.evaluate(input => input === document.querySelector(`[data-staff-id="${input.closest('article').dataset.staffId}"] input[type="email"]`)), true);
      assert.equal(await inactive.locator('fieldset').evaluate(fieldset => fieldset.disabled), false);
      await inactive.getByRole('button', { name: 'Cancel', exact: true }).click(); await page.keyboard.press('Escape');
      const add = page.locator('#staff-add-toggle'); await add.press('Enter');
      const createEmail = page.locator('#staff-add-email');
      assert.equal(await createEmail.evaluate(input => input === document.activeElement), true);
      await createEmail.fill('native.create.draft@example.org');
      await scanRoster('staff-roster-add', createEmail);
      await page.keyboard.press('Escape');
      assert.equal(await add.evaluate(button => button === document.activeElement), true);
      await add.press('Enter'); assert.equal(await createEmail.inputValue(), 'native.create.draft@example.org');
      await page.locator('#staff-add-cancel').click();
      await row.getByRole('button', { name: 'Cancel', exact: true }).click();
      assert.equal(await page.locator('#settings-save-title').textContent(), 'No changes');
    } finally { await context.close(); }
  }
  assert.deepEqual(errors, []); assert.deepEqual(externalRequests, []);
  return states;
};
