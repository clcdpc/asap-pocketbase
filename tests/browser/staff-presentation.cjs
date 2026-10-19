'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');
const [baseURL, artifacts, staffId, tenantId, email] = process.argv.slice(2);

(async () => {
  const axe = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  const report = { externalRequests: [], pageErrors: [], states: [] };
  for (const persistent of [false, true]) {
    const browser = await chromium.launch({ headless: true, ...(persistent ? { ignoreDefaultArgs: ['--hide-scrollbars'] } : {}) });
    try {
      for (const width of [1280, 390]) {
        const context = await browser.newContext({ viewport: { width, height: 900 },
          extraHTTPHeaders: { 'X-ASAP-Test-Staff-Id': staffId, 'X-ASAP-Test-Tenant-Id': tenantId, 'X-ASAP-Test-Staff-Email': email } });
        await context.route('**/*', route => {
          if (new URL(route.request().url()).origin === new URL(baseURL).origin) return route.continue();
          report.externalRequests.push(route.request().url()); return route.abort();
        });
        const page = await context.newPage();
        page.on('pageerror', error => report.pageErrors.push(error.message));
        const quietFeedback = async () => {
          await page.waitForFunction(() => document.querySelector('#app-status').textContent === '');
          const feedback = await page.locator('#app-status').evaluate(node => ({
            height: node.getBoundingClientRect().height,
            top: getComputedStyle(node).marginTop, bottom: getComputedStyle(node).marginBottom,
            role: node.getAttribute('role'), live: node.getAttribute('aria-live')
          }));
          assert.deepEqual(feedback, { height: 0, top: '0px', bottom: '0px', role: 'status', live: 'polite' });
        };
        const scan = async state => {
          await page.evaluate(axe);
          const result = await page.evaluate(async () => {
            const accessibility = (await axe.run()).violations.filter(item => ['serious', 'critical'].includes(item.impact));
            const strips = [...document.querySelectorAll('.view-tabs, .status-tabs')]
              .filter(node => node.getClientRects().length).map(node => ({
                id: node.id || node.className, height: node.clientHeight, scrollHeight: node.scrollHeight,
                overflowY: getComputedStyle(node).overflowY, width: node.clientWidth, scrollWidth: node.scrollWidth
              }));
            return { accessibility, strips, width: innerWidth, scrollWidth: document.documentElement.scrollWidth,
              images: [...document.images].filter(node => node.getClientRects().length).every(node => node.complete && node.naturalWidth > 0) };
          });
          assert.deepEqual(result.accessibility, [], `${state}: axe`);
          assert.ok(result.scrollWidth <= result.width, `${state}: page overflow`);
          assert.equal(result.images, true);
          for (const strip of result.strips) {
            assert.equal(strip.overflowY, 'hidden', `${state}/${strip.id}: horizontal-only strip`);
            assert.ok(strip.scrollHeight <= strip.height, `${state}/${strip.id}: latent vertical overflow`);
          }
          await page.screenshot({ path: path.join(artifacts, `${persistent ? 'persistent' : 'overlay'}-${width}-${state}.png`), fullPage: true });
          report.states.push({ persistent, width, state, ...result });
        };
        await page.goto(`${baseURL}/staff/`);
        await page.locator('#workspace:not([hidden])').waitFor();
        await page.locator('#refresh-queue:not([disabled])').waitFor();
        await quietFeedback();
        await page.locator('#refresh-queue').click();
        await page.locator('#refresh-queue:not([disabled])').waitFor();
        await quietFeedback();
        assert.equal(await page.locator('#queue-empty').isVisible(), true);
        assert.equal(await page.locator('#queue-empty').getAttribute('role'), 'status');
        assert.equal(await page.locator('#queue-empty').getAttribute('aria-live'), 'polite');
        assert.match(await page.locator('#queue-empty').textContent(), /No requests match/);
        assert.equal(await page.locator('.view-tabs [data-view="profile"]').count(), 0);
        assert.equal(await page.locator('header #profile-button').count(), 1);
        assert.equal(await page.locator('#profile-button').getAttribute('aria-controls'), 'profile-view');
        await scan('queue');
        await page.locator('#profile-button').focus();
        await page.keyboard.press('Enter');
        await page.locator('#profile-view:not([hidden])').waitFor();
        assert.equal(await page.evaluate(() => document.activeElement.id), 'profile-title');
        assert.equal(await page.locator('#profile-button').getAttribute('aria-current'), 'page');
        await page.locator('#weekly-email').fill('presentation-draft@example.org');
        page.once('dialog', dialog => dialog.dismiss());
        await page.locator('[data-view="queue"]').click();
        assert.equal(await page.locator('#profile-view').isVisible(), true, 'header route retains draft departure guard');
        assert.equal(await page.locator('#weekly-email').inputValue(), 'presentation-draft@example.org');
        page.once('dialog', dialog => dialog.accept());
        await page.locator('[data-view="queue"]').click();
        await page.locator('#queue-view:not([hidden])').waitFor();
        assert.equal(await page.locator('#profile-button').getAttribute('aria-current'), null);
        await page.locator('#profile-button').focus(); await page.keyboard.press('Space');
        await page.locator('#profile-view:not([hidden])').waitFor();
        await page.goBack(); await page.locator('#queue-view:not([hidden])').waitFor();
        await page.goForward(); await page.locator('#profile-view:not([hidden])').waitFor();
        await scan('profile');

        const sortedSelect = async selector => {
          const options = await page.locator(`${selector} option`).evaluateAll(nodes => nodes.map(node => ({ id: node.value, name: node.textContent })));
          const fixture = options.filter(item => ['383091', '383092', '383093'].includes(item.id));
          assert.deepEqual(fixture.map(item => item.id), ['383092', '383093', '383091'], selector);
          return options;
        };
        await page.locator('[data-view="queue"]').click();
        assert.equal((await sortedSelect('#library-scope'))[0].id, 'all');
        await page.locator('[data-view="additional-copies"]').click();
        await page.locator('#additional-copy-library-scope option[value="383091"]').waitFor({ state: 'attached' });
        await page.locator('#refresh-additional-copies:not([disabled])').waitFor();
        await quietFeedback();
        assert.equal((await sortedSelect('#additional-copy-library-scope'))[0].id, 'all');
        await page.locator('[data-view="operations"]').click();
        await page.locator('#operations-scope option[value="383091"]').waitFor({ state: 'attached' });
        await page.locator('#refresh-operations:not([disabled])').waitFor();
        await quietFeedback();
        assert.equal((await sortedSelect('#operations-scope'))[0].id, 'all');
        await page.locator('[data-view="settings"]').click();
        await page.locator('#settings-form:not([hidden])').waitFor();
        assert.equal((await sortedSelect('#settings-scope'))[0].id, 'system');
        await page.locator('#settings-nav-workflow').click();
        const measureHeading = async buttonId => {
          const geometry = await page.locator(buttonId).evaluate(button => {
            const label = button.parentElement.querySelector('strong');
            const a = label.getBoundingClientRect(), b = button.getBoundingClientRect();
            return { horizontal: b.left - a.right, vertical: b.top - a.bottom, outlineOffset: getComputedStyle(button).outlineOffset };
          });
          assert.ok(geometry.horizontal >= 7 || geometry.vertical >= 7, `${buttonId}: natural heading/action gap`);
        };
        await measureHeading('#add-common-creator');
        await page.locator('#settings-nav-templates').click();
        await measureHeading('#add-email-template');
        await page.locator('#add-email-template').evaluate(button => {
          button.parentElement.querySelector('strong').textContent = 'Additional templates with a deliberately long heading for a narrow viewport';
        });
        await measureHeading('#add-email-template');
        await scan('headings');
        if (width === 1280) {
          await page.evaluate(() => { document.documentElement.style.zoom = '2'; });
          await measureHeading('#add-email-template'); await scan('css-zoom-200');
          await page.evaluate(() => { document.documentElement.style.zoom = ''; });
        }
        await page.locator('[data-view="queue"]').click();
        const lastTab = page.locator('.view-tabs button:not([hidden])').last();
        await page.keyboard.press('Tab');
        await lastTab.focus();
        assert.equal(await lastTab.evaluate(node => node.matches(':focus-visible')), true);
        assert.equal(await lastTab.evaluate(node => getComputedStyle(node).outlineOffset), '-4px');
        if (width === 390) {
          assert.ok(await page.locator('.view-tabs').evaluate(node => node.scrollWidth > node.clientWidth && node.scrollLeft > 0), 'last tab remains horizontally reachable');
          await page.locator('#status-tabs [data-status="suggestion"]').focus();
          await page.keyboard.press('End');
          assert.equal(await page.evaluate(() => document.activeElement.dataset.status), 'closed');
        }
        await context.close();
      }
    } finally { await browser.close(); }
  }
  assert.deepEqual(report.externalRequests, []); assert.deepEqual(report.pageErrors, []);
  await fs.writeFile(path.join(artifacts, 'browser-results.json'), JSON.stringify(report, null, 2));
  console.log('Staff header, spacing, lookup sorting and horizontal-only tab geometry passed.');
})().catch(error => { console.error(error); process.exitCode = 1; });
