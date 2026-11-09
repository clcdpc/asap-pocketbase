'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');
const verifyMixedFieldLayout = require('./form-layout.cjs');

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
      page.on('pageerror', error => errors.push(error.message));
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
      const captureNativeImage = async name => {
        const session = await context.newCDPSession(page);
        const metrics = await session.send('Page.getLayoutMetrics');
        assert.equal(metrics.visualViewport.zoom, 2);
        const capture = await session.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true,
          clip: { x: 0, y: 0, width: metrics.contentSize.width, height: metrics.contentSize.height, scale: 1 } });
        await fs.writeFile(path.join(artifacts, `native-zoom-${width}-${name}.png`), Buffer.from(capture.data, 'base64'));
        await session.detach();
      };
      const scan = async name => {
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
        await captureNativeImage(name);
        states.push({ physicalWidth: width, name, actualBrowserZoom: zoom, ...result });
      };
      const title = page.locator(`a[data-request-id="${sourceId}"]`);
      await title.waitFor();
      const wrapper = page.locator('#request-grid .gridjs-wrapper');
      await wrapper.focus(); await page.keyboard.press('ArrowRight');
      assert.equal(await wrapper.getAttribute('role'), 'region');
      const more = page.getByRole('button', { name: `More actions for request ${sourceId}`, exact: true });
      await more.focus(); await page.keyboard.press('Enter');
      await scan('table');
      await page.keyboard.press('Escape');
      assert.equal(await more.evaluate(node => node === document.activeElement), true);
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
      await detail.getByRole('tab', { name: 'Details', exact: true }).click();
      for (const position of ['top', 'bottom']) {
        const geometry = await detail.evaluate((root, position) => {
          const middle = root.querySelector('.request-middle');
          middle.scrollTop = position === 'top' ? 0 : middle.scrollHeight;
          return { middle: middle.getBoundingClientRect().toJSON(),
            footer: root.querySelector('.request-save-footer').getBoundingClientRect().toJSON(),
            scrollTop: middle.scrollTop, scrollHeight: middle.scrollHeight, clientHeight: middle.clientHeight,
            height: innerHeight };
        }, position);
        assert.ok(geometry.footer.top >= 0 && geometry.footer.bottom <= geometry.height, `${width}/${position}: footer stays visible.`);
        assert.ok(geometry.middle.bottom <= geometry.footer.top + 1, `${width}/${position}: footer cannot cover content.`);
        assert.ok(geometry.scrollHeight > geometry.clientHeight, 'Long Details content must genuinely scroll.');
        assert.ok(Math.abs(geometry.scrollTop - (position === 'top' ? 0 : geometry.scrollHeight - geometry.clientHeight)) <= 1);
      }
      const notes = detail.getByLabel('Notes', { exact: true });
      await notes.focus();
      await page.keyboard.press('Shift+Tab'); await page.keyboard.press('Tab');
      assert.equal(await notes.evaluate(node => node === document.activeElement), true);
      // Native focus scrolling may expose only the textarea's caret line.
      // The scroll pane must also allow the whole control above the footer.
      await notes.scrollIntoViewIfNeeded();
      const notesGeometry = await notes.evaluate(node => ({ notes: node.getBoundingClientRect().toJSON(),
        middle: node.closest('.request-middle').getBoundingClientRect().toJSON() }));
      await captureNativeImage('details-bottom');
      assert.ok(notesGeometry.notes.top >= notesGeometry.middle.top - 1 && notesGeometry.notes.bottom <= notesGeometry.middle.bottom + 1,
        `${width}: scrolling brings the final Notes control fully above the footer: ${JSON.stringify(notesGeometry)}`);
      page.once('dialog', dialog => dialog.accept());
      await detail.getByRole('button', { name: 'Revert changes', exact: true }).click();
      assert.equal(await detail.locator('.request-save-state').textContent(), 'All changes saved');
      await page.keyboard.press('Escape');
      await page.getByRole('button', { name: `New suggestion for patron ${barcode} from request ${sourceId}`, exact: true }).click();
      const suggestion = page.locator('#staff-suggestion-dialog[open]');
      await suggestion.getByRole('button', { name: 'Create suggestion', exact: true }).waitFor();
      await verifyMixedFieldLayout(page, '#staff-suggestion-body');
      const date = suggestion.getByLabel('Exact publication date', { exact: true });
      const initialDate = await date.inputValue();
      await date.fill('2026-02-18');
      await date.focus(); await date.press('ArrowUp');
      assert.notEqual(await date.inputValue(), '2026-02-18', 'Native date segments remain keyboard editable.');
      assert.equal(await date.evaluate(node => node.type === 'date' && node.validity.valid), true);
      await date.fill(initialDate);
      await scan('new-suggestion');
      await page.keyboard.press('Escape');
      await page.goto(`${baseURL}/staff/?stage=settings&settingsScope=system#settings-patron`, { waitUntil: 'networkidle' });
      // Chromium resets per-tab zoom on a full navigation. Exercise Settings
      // at actual 200% as well, with the same DPR/CDP assertions below.
      await applyNativeZoom();
      await page.waitForFunction(() => document.querySelector('#settings-version').value && !document.querySelector('#settings-form').inert);
      await page.locator('#settings-nav-patron').click();
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
    } finally { await context.close(); }
  }
  assert.deepEqual(errors, []); assert.deepEqual(externalRequests, []);
  return states;
};
