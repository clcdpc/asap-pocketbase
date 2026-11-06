'use strict';
const { decideConfirmation, rejectNativeDialogs } = require('./confirmation.cjs');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');
const { providerGeometry } = require('../acceptance/helpers/workflow-settings.cjs');
const { runSourceComponents } = require('./settings-accessibility.cjs');
const settingsSpacingRecorder = require('./settings-spacing.cjs');
const [baseURL, artifacts, staffId, tenantId, email] = process.argv.slice(2);

async function verifyPolarisSettings(page, width, scope, scan, report) {
  await page.locator('#settings-nav-polaris').click();
  const key = page.locator('#polaris-api-key'), password = page.locator('#polaris-admin-pass');
  const keyStatus = page.locator('#polaris-api-key-status'), passwordStatus = page.locator('#polaris-admin-pass-status');
  assert.equal(await page.getByLabel('API key', { exact: true }).count(), 1);
  assert.equal(await page.getByLabel('System staff password', { exact: true }).count(), 1);
  assert.equal(await key.inputValue(), ''); assert.equal(await password.inputValue(), '');
  assert.equal(await key.getAttribute('aria-describedby'), 'polaris-api-key-status');
  assert.equal(await password.getAttribute('aria-describedby'), 'polaris-admin-pass-status');
  if (scope !== 'system') {
    for (const control of [key, password, page.locator('#btn-test-polaris'), page.locator('#btn-sync-organizations')]) {
      assert.equal(await control.isDisabled(), true, 'Polaris credentials and actions remain System only');
    }
    await scan(`settings-polaris-library-${scope}`);
    return;
  }

  let savedKey = false, savedPassword = false, saveRejected = false, failedReload = false;
  let version = 1, testReply = 'success', syncReply = 'success', releaseTest, releaseSync, testStarted, syncStarted;
  const posted = [];
  const settingsRoute = async route => {
    if (failedReload) return route.fulfill({ status: 503, json: { message: 'Fixture settings reload unavailable.' } });
    const response = await route.fetch();
    assert.equal(response.status(), 200);
    const data = await response.json();
    data.version = `polaris-browser-${version}`;
    const polaris = { ...data.stored.polaris, hasApiKey: savedKey, hasAdminPassword: savedPassword };
    data.stored.polaris = polaris; data.stored.configuredSystem.polaris = polaris;
    await route.fulfill({ response, json: data });
  };
  const saveRoute = async route => {
    const body = route.request().postDataJSON(); posted.push(body);
    if (saveRejected) return route.fulfill({ status: 400, json: { message: 'Polaris replacement rejected.' } });
    if (body.polaris?.apiKey) savedKey = true;
    if (body.polaris?.adminPassword) savedPassword = true;
    await route.fulfill({ status: 200, json: { code: 'saved', data: { orgId: body.orgId, version: `polaris-browser-${++version}` } } });
  };
  const testRoute = async route => {
    if (testReply === 'pending') await new Promise(resolve => { releaseTest = resolve; testStarted?.(); });
    await route.fulfill(testReply === 'failure' ? { status: 502, json: { code: 'polaris_unavailable' } }
      : { status: 200, json: { code: 'polaris_connected', data: { connected: true, organizationCount: 3 } } })
      .catch(error => { if (!/closed|handled|aborted/i.test(error.message)) throw error; });
  };
  const syncRoute = async route => {
    if (syncReply === 'pending') await new Promise(resolve => { releaseSync = resolve; syncStarted?.(); });
    await route.fulfill(syncReply === 'uncertain' ? { status: 503, json: { message: 'Fixture provider unavailable.' } }
      : syncReply === 'rejected' ? { status: 400, json: { message: 'Organizations could not be synchronized because the provider rejected this configuration. Review the saved system integration settings before trying again.' } }
        : { status: 200, json: { code: 'synced', data: { received: 4, changed: 1 } } });
  };
  await page.route('**/api/asap/staff/settings?*', settingsRoute);
  await page.route('**/api/asap/staff/settings', saveRoute);
  await page.route('**/api/asap/staff/polaris/test', testRoute);
  await page.route('**/api/asap/staff/organizations/sync', syncRoute);
  const ready = () => page.waitForFunction(() => !document.getElementById('settings-form').inert &&
    !document.getElementById('settings-refresh').disabled);
  const reload = async () => { await page.locator('#settings-refresh').click(); await ready(); };
  const save = async () => { await page.locator('#settings-save').click(); await ready(); };
  const local = async (id, text) => {
    await page.locator(id).filter({ hasText: text }).waitFor();
    assert.equal(await page.locator('#polaris-action-announcement').textContent(), await page.locator(id).textContent());
    for (const selector of ['#settings-message', '#app-status']) {
      assert.doesNotMatch(await page.locator(selector).textContent(), /Connected;|Polaris connection|Synchronized|synchronization outcome|provider rejected/i,
        'Polaris operation feedback has no duplicate visible global notice');
    }
  };
  const geometry = () => page.locator('.settings-polaris-actions').evaluate(group => {
    const rect = node => { const r = node.getBoundingClientRect(); return { top: r.top, bottom: r.bottom, left: r.left, right: r.right, height: r.height }; };
    return { group: rect(group), buttons: [...group.querySelectorAll('button')].map(rect),
      feedback: rect(group.querySelector('.settings-polaris-feedback')), liveOutsideForm: !document.getElementById('polaris-action-announcement').closest('form'),
      livePosition: getComputedStyle(document.getElementById('polaris-action-announcement')).position };
  });
  try {
    await reload();
    assert.equal(await keyStatus.textContent(), 'No API key saved.');
    assert.equal(await passwordStatus.textContent(), 'No system staff password saved.');
    await key.fill('browser-replacement-key'); await password.fill('browser-replacement-password');
    assert.equal(await keyStatus.textContent(), 'No API key saved.', 'typing leaves the accepted missing indicator unchanged');
    saveRejected = true; await save();
    assert.equal(await key.inputValue(), 'browser-replacement-key');
    assert.equal(await password.inputValue(), 'browser-replacement-password');
    assert.equal(await keyStatus.textContent(), 'No API key saved.', 'a rejected replacement is still unsaved');
    saveRejected = false; failedReload = true;
    await page.locator('#settings-save').click();
    await page.locator('#settings-message').filter({ hasText: 'Settings saved, but current values could not be refreshed.' }).waitFor();
    assert.equal(await keyStatus.textContent(), 'API key replacement saved. Reload settings to refresh the credential status.');
    assert.equal(await passwordStatus.textContent(), 'System staff password replacement saved. Reload settings to refresh the credential status.');
    assert.equal(await key.inputValue(), 'browser-replacement-key');
    assert.equal(await password.inputValue(), 'browser-replacement-password');
    await scan('settings-polaris-replacements-committed-reload-failed');
    failedReload = false; await reload();
    assert.equal(await key.inputValue(), ''); assert.equal(await password.inputValue(), '');
    assert.match(await keyStatus.textContent(), /^API key saved\. Leave blank/);
    assert.match(await passwordStatus.textContent(), /^System staff password saved\. Leave blank/);
    assert.equal(posted.at(-1).polaris.apiKey, 'browser-replacement-key');
    assert.equal(posted.at(-1).polaris.adminPassword, 'browser-replacement-password');
    await page.locator('#polaris-access-id').fill('blank-secret-browser-witness'); await save();
    assert.equal(Object.hasOwn(posted.at(-1).polaris, 'apiKey'), false);
    assert.equal(Object.hasOwn(posted.at(-1).polaris, 'adminPassword'), false);
    await reload();
    const emptyGeometry = await geometry();
    await page.locator('#polaris-system-user-id').focus(); await page.keyboard.press('Tab');
    assert.equal(await page.evaluate(() => document.activeElement.id), 'btn-test-polaris');
    assert.equal(await page.locator('#btn-test-polaris').evaluate(button => button.matches(':focus-visible')), true);
    await page.keyboard.press('Tab');
    assert.equal(await page.evaluate(() => document.activeElement.id), 'btn-sync-organizations');
    const testPending = new Promise(resolve => { testStarted = resolve; });
    testReply = 'pending'; await page.locator('#btn-test-polaris').press('Enter'); await testPending;
    await local('#polaris-test-result', 'Testing saved Polaris connection...');
    await page.waitForFunction(() => document.getElementById('btn-test-polaris').disabled);
    await scan('settings-polaris-test-pending');
    testReply = 'success'; releaseTest();
    await local('#polaris-test-result', 'Connected; 3 organizations available.');
    const connectedGeometry = await geometry();
    assert.equal(connectedGeometry.liveOutsideForm, true); assert.equal(connectedGeometry.livePosition, 'absolute');
    if (width > 760) {
      const { group, buttons, feedback } = connectedGeometry;
      assert.ok(Math.abs(feedback.right - group.right) < 1, 'Polaris action/result group is right aligned');
      assert.ok(buttons[0].right < buttons[1].left && buttons[1].right < feedback.left, 'feedback follows both buttons');
      for (const button of buttons) {
        assert.ok(Math.abs((button.top + button.bottom - feedback.top - feedback.bottom) / 2) < 1, 'feedback and buttons are vertically centered');
      }
      assert.ok(Math.abs(buttons[0].left - emptyGeometry.buttons[0].left) < 1, 'feedback has reserved space before it arrives');
    } else {
      assert.ok(connectedGeometry.feedback.top >= connectedGeometry.buttons.at(-1).bottom, 'mobile feedback wraps below the buttons');
    }
    await scan('settings-polaris-connected');
    testReply = 'failure'; await page.locator('#btn-test-polaris:not([disabled])').click();
    await local('#polaris-test-result', 'Polaris is unavailable.');
    assert.match(await keyStatus.textContent(), /^API key saved\./, 'connection failure cannot change accepted saved presence');
    const syncPending = new Promise(resolve => { syncStarted = resolve; });
    syncReply = 'pending'; await page.locator('#btn-sync-organizations').click(); await syncPending;
    await local('#organizations-sync-result', 'Synchronizing Polaris organizations...');
    assert.equal(await page.locator('#polaris-test-result').textContent(), '');
    assert.equal(await page.locator('#settings-form').evaluate(form => form.inert), true);
    await scan('settings-polaris-sync-pending');
    syncReply = 'success'; releaseSync();
    await local('#organizations-sync-result', 'Synchronized 4 organizations.'); await ready();
    await scan('settings-polaris-synchronized');
    failedReload = true; await page.locator('#btn-sync-organizations').click();
    await local('#organizations-sync-result', 'Synchronized 4 organizations. Current values could not be refreshed. Reload before editing.');
    assert.equal(await page.locator('#settings-form').evaluate(form => form.inert), true);
    await scan('settings-polaris-committed-reload-failed');
    failedReload = false; await reload();
    assert.equal(await page.locator('#organizations-sync-result').textContent(), '');
    syncReply = 'uncertain'; await page.locator('#btn-sync-organizations').click();
    await local('#organizations-sync-result', 'Organization synchronization outcome is uncertain. Reload current values before retrying.');
    assert.equal(await page.locator('#settings-form').evaluate(form => form.inert), true);
    await scan('settings-polaris-uncertain');
    await reload();
    syncReply = 'rejected'; await page.locator('#btn-sync-organizations').click();
    await local('#organizations-sync-result', 'Organizations could not be synchronized because the provider rejected'); await ready();
    if (width === 1280) {
      await page.evaluate(() => { document.documentElement.style.zoom = '2'; });
      await scan('settings-polaris-zoom-200');
      await page.evaluate(() => { document.documentElement.style.zoom = ''; });
    } else { await scan('settings-polaris-rejected'); }
    testReply = 'pending'; releaseTest = null;
    const retiredTestPending = new Promise(resolve => { testStarted = resolve; });
    await page.locator('#btn-test-polaris').click(); await retiredTestPending;
    await local('#polaris-test-result', 'Testing saved Polaris connection...');
    await page.locator('#profile-button').click(); await page.locator('#profile-view:not([hidden])').waitFor();
    testReply = 'success'; releaseTest();
    await page.locator('[data-view="settings"]').click(); await ready();
    assert.equal(await page.locator('#polaris-test-result').textContent(), '', 'retired diagnostic feedback stays cleared after returning');
    assert.equal(await page.locator('#polaris-action-announcement').textContent(), '');
    report.polarisSettings.push({ width, scope, blankReplacements: true, savedFlags: true,
      localFeedback: true, mutationRecovery: true, emptyGeometry, connectedGeometry });
  } finally {
    await page.evaluate(() => { document.documentElement.style.zoom = ''; });
    await page.unroute('**/api/asap/staff/settings?*', settingsRoute);
    await page.unroute('**/api/asap/staff/settings', saveRoute);
    await page.unroute('**/api/asap/staff/polaris/test', testRoute);
    await page.unroute('**/api/asap/staff/organizations/sync', syncRoute);
    await reload();
  }
}

(async () => {
  const axe = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  const report = { externalRequests: [], pageErrors: [], states: [], settingsHeaders: [], brandingPlacements: [], staffUrlSuggestions: [], polarisSettings: [], publicLinks: [], spacingAudits: [] };
  for (const persistent of [false, true]) {
    const browser = await chromium.launch({ headless: true, ...(persistent ? { ignoreDefaultArgs: ['--hide-scrollbars'] } : {}) });
    let restoreStaffUrl;
    try {
      // Own a valid persisted URL so the real API builds the library's portal
      // links. This fixture mutation is separate from the read-only geometry.
      const fixture = await browser.newContext({ extraHTTPHeaders: {
        'X-ASAP-Test-Staff-Id': staffId, 'X-ASAP-Test-Tenant-Id': tenantId, 'X-ASAP-Test-Staff-Email': email
      } });
      const readSystem = async () => {
        const response = await fixture.request.get(`${baseURL}/api/asap/staff/settings?orgId=system`);
        assert.equal(response.status(), 200, await response.text());
        return response.json();
      };
      const originalStaffUrl = (await readSystem()).stored.systemSettings.staffUrl ?? null;
      const setStaffUrl = async value => {
        const session = await fixture.request.get(`${baseURL}/api/asap/staff/session`);
        assert.equal(session.status(), 200, await session.text());
        const current = await readSystem();
        const response = await fixture.request.post(`${baseURL}/api/asap/staff/settings`, {
          headers: { 'X-ASAP-Antiforgery': (await session.json()).antiforgeryToken },
          data: { orgId: 'system', version: current.version, systemSettings: { staffUrl: value } }
        });
        assert.equal(response.status(), 200, await response.text());
        assert.equal((await readSystem()).stored.systemSettings.staffUrl, value);
      };
      restoreStaffUrl = async () => {
        try { await setStaffUrl(originalStaffUrl); } finally { await fixture.close(); }
      };
      await setStaffUrl('https://presentation.example.test/staff/');
      for (const width of [1440, 1280, 390]) {
        const context = await browser.newContext({ viewport: { width, height: 900 },
          extraHTTPHeaders: { 'X-ASAP-Test-Staff-Id': staffId, 'X-ASAP-Test-Tenant-Id': tenantId, 'X-ASAP-Test-Staff-Email': email } });
        await context.route('**/*', route => {
          if (new URL(route.request().url()).origin === new URL(baseURL).origin) return route.continue();
          report.externalRequests.push(route.request().url()); return route.abort();
        });
        const page = await context.newPage();
        const recordSpacing = settingsSpacingRecorder(page, path.join(artifacts, `${persistent ? 'persistent' : 'overlay'}-${width}-spacing`));
        await fs.mkdir(path.join(artifacts, `${persistent ? 'persistent' : 'overlay'}-${width}-spacing`), { recursive: true });
        page.on('pageerror', error => report.pageErrors.push(error.message)); rejectNativeDialogs(page, report.pageErrors);
        const staffSettingsPosts = [], apiWrites = [];
        page.on('request', request => {
          const url = new URL(request.url());
          if (request.method() === 'POST' && url.pathname === '/api/asap/staff/settings') staffSettingsPosts.push(url.pathname);
          if (!['GET', 'HEAD', 'OPTIONS'].includes(request.method()) && url.pathname.startsWith('/api/asap/staff/')) apiWrites.push(url.pathname);
        });
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
          await recordSpacing(state);
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
        // Other journeys retain committed requests; this fixture owns an empty library.
        await page.goto(`${baseURL}/staff/?stage=suggestion&scope=383091`);
        await page.locator('#workspace:not([hidden])').waitFor();
        await page.locator('#refresh-queue:not([disabled])').waitFor();
        await quietFeedback();
        await page.locator('#refresh-queue').click();
        await page.locator('#refresh-queue:not([disabled])').waitFor();
        await quietFeedback();
        assert.equal(await page.locator('#library-scope').inputValue(), '383091');
        assert.equal(new URL(page.url()).searchParams.get('scope'), '383091');
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
        await decideConfirmation(page, () => page.locator('[data-view="queue"]').click(), dialog => dialog.dismiss());
        assert.equal(await page.locator('#profile-view').isVisible(), true, 'header route retains draft departure guard');
        assert.equal(await page.locator('#weekly-email').inputValue(), 'presentation-draft@example.org');
        await decideConfirmation(page, () => page.locator('[data-view="queue"]').click(), dialog => dialog.accept());
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
        await page.locator('#library-scope').selectOption('all');
        await page.locator('#refresh-queue:not([disabled])').waitFor();
        await quietFeedback();
        assert.equal(await page.locator('#library-scope').inputValue(), 'all');
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
        const ensureCommonCreatorsVisible = async () => {
          const enabled = page.locator('#common-authors-enabled');

          if (!await enabled.isChecked()) await enabled.check();
          await page.locator('#common-authors-options:not([hidden])').waitFor();
        };
        const measureActions = async selector => page.locator(`${selector} > button`).evaluateAll(buttons => {
          const bounds = buttons.map(button => button.getBoundingClientRect());
          return bounds.slice(1).map((current, index) => {
            const previous = bounds[index];
            return current.top < previous.bottom && previous.top < current.bottom
              ? current.left - previous.right
              : current.top - previous.bottom;
          });
        });
        const draftStatus = () => page.locator('#settings-form').evaluate(form => ({
          dirty: !document.getElementById('settings-discard').hidden,
          title: document.getElementById('settings-save-title').textContent,
          attention: form.querySelector('.settings-save-bar').classList.contains('attention'),
          saveDisabled: document.getElementById('settings-save').disabled, inert: form.inert
        }));
        const auditSpacing = async state => {
          const before = await draftStatus(), writesBefore = apiWrites.length;
          let geometry;
          try { geometry = await recordSpacing(state); }
          finally { await page.screenshot({ path: path.join(artifacts, `${state}.png`), fullPage: true }); }
          await page.evaluate(axe);
          const result = await page.evaluate(async () => ({
            accessibility: (await axe.run()).violations.filter(item => ['serious', 'critical'].includes(item.impact)),
            images: [...document.images].filter(node => node.getClientRects().length).every(node => node.complete && node.naturalWidth > 0)
          }));
          report.spacingAudits.push({ state, scope: geometry.scope, panel: geometry.panel, viewport: geometry.viewport, ...result });
          await fs.writeFile(path.join(artifacts, 'settings-spacing-audits.json'), JSON.stringify(report.spacingAudits, null, 2));
          assert.deepEqual(result.accessibility, [], `${state}: axe`);
          assert.equal(result.images, true, `${state}: visible images`);
          assert.deepEqual(report.pageErrors, [], `${state}: page errors`);
          assert.deepEqual(report.externalRequests, [], `${state}: external requests`);
          assert.deepEqual(await draftStatus(), before, `${state}: measuring layout preserves all draft owners`);
          assert.equal(apiWrites.length, writesBefore, `${state}: measuring layout never writes an API`);
        };
        const discardSpacingDraft = async () => {
          if (await page.locator('#settings-discard').isVisible()) {
            await decideConfirmation(page, () => page.locator('#settings-discard').click(), dialog => dialog.accept());
          }
          await page.waitForFunction(() => !document.getElementById('settings-form').inert &&
            !document.getElementById('settings-refresh').disabled && document.getElementById('settings-save-title').textContent === 'No changes');
        };
        const auditPickerVariants = async state => {


          while (await page.locator('#patron-codes-editor button').count()) await page.locator('#patron-codes-editor button').first().click();
          await page.locator('#patron-code-eligibility-enabled').check();
          assert.equal(await page.locator('#patron-code-warning').isVisible(), true);
          await auditSpacing(`${state}-warning-empty`);
          const picker = page.locator('#patron-code-picker');
          await picker.fill('Adult'); await picker.press('ArrowDown');
          assert.equal(await page.locator('#patron-code-options').isVisible(), true);
          await auditSpacing(`${state}-filtered-picker`);
          // A full axe scan can scroll an anchored list out of view. Reopen
          // through the real keyboard path before performing the selection.
          await picker.fill('Adult'); await picker.press('ArrowDown'); await picker.press('Enter');
          assert.equal(await page.locator('#patron-codes-editor [data-code-id]').count(), 1);
          assert.equal(await page.locator('#patron-code-warning').isHidden(), true);
          await auditSpacing(`${state}-populated`);
          await page.locator('#patron-codes-editor button').first().click();
          await page.locator('#patron-code-eligibility-enabled').uncheck();
          assert.equal(await page.locator('#patron-code-warning').isHidden(), true);
          await auditSpacing(`${state}-empty-warning-hidden`);
          await discardSpacingDraft();
        };
        const measureSettingsHeader = async scope => {
          const message = page.locator('#settings-message');
          const originalMessage = await message.evaluate(node => ({ text: node.textContent, className: node.className }));
          try {
            await message.evaluate(node => { node.textContent = ''; node.className = 'settings-message'; });
            const compact = await page.evaluate(() => {
              const rect = node => {
                const bounds = node.getBoundingClientRect();
                return { top: bounds.top, right: bounds.right, bottom: bounds.bottom,
                  left: bounds.left, width: bounds.width, height: bounds.height };
              };
              const header = document.querySelector('#settings-view .settings-header');
              const title = document.getElementById('settings-title');
              const toolbar = header.querySelector('.settings-toolbar');
              const status = document.getElementById('settings-message');
              const layout = document.querySelector('#settings-view .settings-layout');
              const titleBounds = title.getBoundingClientRect(), toolbarBounds = toolbar.getBoundingClientRect();
              return {
                header: rect(header), gridRows: getComputedStyle(header).gridTemplateRows.trim().split(/\s+/).filter(Boolean),
                title: rect(title), toolbar: rect(toolbar), status: rect(status), layout: rect(layout),
                titleToolbarOverlap: titleBounds.top < toolbarBounds.bottom && toolbarBounds.top < titleBounds.bottom,
                summaryPresent: header.querySelector('#settings-context-summary') !== null,
                headerText: header.textContent.trim(),
                scopeValue: document.getElementById('settings-scope').value,
                selectedScopeLabel: document.querySelector('#settings-scope option:checked')?.textContent.trim(),
                scopeLabel: document.querySelector('#settings-scope-field > span')?.textContent.trim(),
                scopeVisible: document.getElementById('settings-scope-field').getClientRects().length > 0,
                refreshVisible: document.getElementById('settings-refresh').getClientRects().length > 0,
                statusText: status.textContent.trim(), statusRole: status.getAttribute('role'),
                statusLive: status.getAttribute('aria-live'), statusInToolbar: toolbar.contains(status),
                viewportWidth: innerWidth, scrollWidth: document.documentElement.scrollWidth
              };
            });
            assert.equal(compact.summaryPresent, false, `${width}px/${scope}: no repeated context subtitle`);
            assert.doesNotMatch(compact.headerText, /System level configuration|Library configuration for/,
              `${width}px/${scope}: header does not repeat the selected scope`);
            assert.equal(compact.scopeValue, scope);
            if (scope === 'system') {
              assert.equal(compact.selectedScopeLabel, 'System level');
            } else {
              assert.ok(compact.selectedScopeLabel && compact.selectedScopeLabel !== 'System level',
                `${width}px/${scope}: selected library remains identified by the scope selector`);
            }
            assert.equal(compact.scopeLabel, 'Settings scope');
            assert.equal(compact.scopeVisible, true);
            assert.equal(compact.refreshVisible, true);
            assert.equal(compact.statusText, '');
            assert.equal(compact.statusRole, 'status');
            assert.equal(compact.statusLive, 'polite');
            assert.equal(compact.statusInToolbar, true);
            assert.ok(compact.layout.top >= compact.header.bottom, `${width}px/${scope}: content follows the header`);
            assert.ok(compact.scrollWidth <= compact.viewportWidth, `${width}px/${scope}: compact header has no horizontal overflow`);
            if (width > 760) {
              assert.equal(compact.gridRows.length, 1, `${width}px/${scope}: desktop header has one grid row`);
              assert.ok(compact.header.height <= 50, `${width}px/${scope}: empty status leaves no reserved row (${compact.header.height}px)`);
              assert.equal(compact.titleToolbarOverlap, true, `${width}px/${scope}: title and toolbar share the desktop row`);
            } else {
              assert.equal(compact.gridRows.length, 2, `${width}px/${scope}: mobile stacks only title and toolbar`);
              assert.equal(compact.titleToolbarOverlap, false, `${width}px/${scope}: mobile controls do not collide with the title`);
              assert.ok(compact.toolbar.top >= compact.title.bottom, `${width}px/${scope}: toolbar follows the title`);
            }

            const longStatus = 'Settings could not be loaded for this scope. Please refresh and retry. '.repeat(3).trim();
            await message.evaluate((node, text) => { node.className = 'settings-message error'; node.textContent = text; }, longStatus);
            const expanded = await page.evaluate(() => {
              const rect = node => {
                const bounds = node.getBoundingClientRect();
                return { top: bounds.top, right: bounds.right, bottom: bounds.bottom,
                  left: bounds.left, width: bounds.width, height: bounds.height };
              };
              const header = document.querySelector('#settings-view .settings-header');
              const title = document.getElementById('settings-title');
              const toolbar = header.querySelector('.settings-toolbar');
              const status = document.getElementById('settings-message');
              const layout = document.querySelector('#settings-view .settings-layout');
              const titleBounds = title.getBoundingClientRect(), toolbarBounds = toolbar.getBoundingClientRect();
              return {
                header: rect(header), gridRows: getComputedStyle(header).gridTemplateRows.trim().split(/\s+/).filter(Boolean),
                title: rect(title), toolbar: rect(toolbar), status: rect(status), layout: rect(layout),
                titleToolbarOverlap: titleBounds.top < toolbarBounds.bottom && toolbarBounds.top < titleBounds.bottom,
                statusText: status.textContent.trim(), statusRole: status.getAttribute('role'),
                statusLive: status.getAttribute('aria-live'), statusInToolbar: toolbar.contains(status),
                statusClipped: status.scrollHeight > status.clientHeight + 1,
                viewportWidth: innerWidth, scrollWidth: document.documentElement.scrollWidth
              };
            });
            assert.equal(expanded.statusText, longStatus, `${width}px/${scope}: long error text is retained`);
            assert.equal(expanded.statusRole, 'status');
            assert.equal(expanded.statusLive, 'polite');
            assert.equal(expanded.statusInToolbar, true);
            assert.equal(expanded.statusClipped, false, `${width}px/${scope}: wrapped error is not clipped`);
            assert.ok(expanded.status.height > 0, `${width}px/${scope}: long error remains visible`);
            assert.ok(expanded.header.height >= compact.header.height, `${width}px/${scope}: header grows around its status`);
            assert.ok(expanded.layout.top >= expanded.header.bottom, `${width}px/${scope}: workflow content remains below the expanded header`);
            assert.ok(expanded.scrollWidth <= expanded.viewportWidth, `${width}px/${scope}: long error causes no horizontal overflow`);
            if (width > 760) {
              assert.equal(expanded.gridRows.length, 1, `${width}px/${scope}: wrapped status does not add a desktop grid row`);
            } else {
              assert.equal(expanded.gridRows.length, 2, `${width}px/${scope}: wrapped status stays within the stacked toolbar row`);
              assert.equal(expanded.titleToolbarOverlap, false, `${width}px/${scope}: wrapped status does not collide with the title`);
            }
            report.settingsHeaders.push({ width, scope, emptyHeight: compact.header.height,
              longStatusHeight: expanded.header.height, emptyGridRows: compact.gridRows.length,
              longStatusGridRows: expanded.gridRows.length });
            await scan(`settings-header-long-${scope}`);
          } finally {
            await message.evaluate((node, original) => {
              node.textContent = original.text;
              node.className = original.className;
            }, originalMessage);
          }
        };
        for (const scope of ['system', '383091']) {
          await page.locator('#settings-scope').selectOption(scope);
          await page.waitForFunction(scope => document.getElementById('settings-scope').value === scope &&
            document.getElementById('settings-version').value && !document.getElementById('settings-form').inert &&
            !document.getElementById('settings-refresh').disabled, scope);
          await measureSettingsHeader(scope);
          if (!persistent && width === 1280) {
            const writesBefore = apiWrites.length;
            const draftBefore = await draftStatus();
            for (const config of [
              { name: 'desktop', width: 1280, zoom: '', theme: 'light' },
              { name: '760', width: 760, zoom: '', theme: 'light' },
              { name: '390', width: 390, zoom: '', theme: 'light' },
              { name: 'css-200', width: 1280, zoom: '2', theme: 'light' },
              { name: 'desktop-dark', width: 1280, zoom: '', theme: 'dark' },
              { name: '390-dark', width: 390, zoom: '', theme: 'dark' }
            ]) {
              await page.setViewportSize({ width: config.width, height: 900 });
              await page.emulateMedia({ colorScheme: config.theme });
              await page.evaluate(zoom => { document.documentElement.style.zoom = zoom; }, config.zoom);
              for (const panel of ['workflow', 'start', 'polaris', 'smtp', 'staff', 'patron', 'templates']) {
                await page.locator(`#settings-nav-${panel}`).click();
                if (panel === 'staff') await page.locator('#staff-refresh:not([disabled])').waitFor();
                const state = `audit-${scope}-${config.name}-${panel}`;
                await auditSpacing(state);
                if (panel === 'workflow' && ['desktop', '390', 'css-200'].includes(config.name)) {
                  await auditPickerVariants(state);
                  if (scope === 'system') {
                    while (await page.locator('#external-search-provider-editor [data-domain-row]').count()) {
                      await page.locator('#external-search-provider-editor [data-domain-row]').last().getByRole('button', { name: 'Delete', exact: true }).click();
                    }
                    await auditSpacing(`${state}-providers-empty`);
                    await page.locator('#add-provider').click();
                    await auditSpacing(`${state}-providers-added`);
                    await discardSpacingDraft();
                  }
                  if (config.name === 'desktop') {
                    const unavailable = route => route.fulfill({ status: 503, json: { message: 'Fixture patron-code catalog unavailable.' } });
                    await page.route('**/api/asap/staff/polaris/patron-codes?*', unavailable);
                    try {
                      await page.locator('#settings-refresh').click();
                      await page.locator('#patron-code-retry').waitFor({ state: 'visible' });
                      await page.locator('#settings-refresh:not([disabled])').waitFor();
                      await auditSpacing(`${state}-lookup-unavailable`);
                    } finally { await page.unroute('**/api/asap/staff/polaris/patron-codes?*', unavailable); }
                    await page.locator('#patron-code-retry').click();
                    await page.locator('#patron-code-retry').waitFor({ state: 'hidden' });
                    await auditSpacing(`${state}-lookup-recovered`);
                  }
                }
                if (panel === 'staff') {
                  const summary = page.locator('.settings-staff-summary').first();
                  if (await summary.count()) {
                    await summary.click(); await auditSpacing(`${state}-expanded`); await summary.click();
                  }
                  await page.locator('#staff-add-toggle').click();
                  await auditSpacing(`${state}-add`);
                  await page.locator('#staff-add-submit').click();
                  await page.locator('#staff-access-status').filter({ hasText: 'Email address is required.' }).waitFor();
                  await auditSpacing(`${state}-required`);
                  await page.locator('#staff-add-cancel').click();
                }
                if (panel === 'patron') {
                  assert.equal(await page.locator('#patron-public-links').isVisible(), scope !== 'system',
                    `${state}: only the active library has authoritative public links`);
                  if (scope !== 'system' && config.name === 'desktop') {
                    await context.grantPermissions(['clipboard-read', 'clipboard-write'], { origin: new URL(baseURL).origin });
                    const expectedUrl = 'https://presentation.example.test/patron/?libraryOrgId=383091';
                    assert.equal(await page.locator('#patron-public-url').inputValue(), expectedUrl);
                    for (const [button, field, label] of [
                      ['copy-patron-public-url', 'patron-public-url', 'Patron URL'],
                      ['copy-patron-iframe', 'patron-iframe-markup', 'Iframe markup'],
                      ['copy-patron-loader', 'patron-loader-markup', 'Auto-resizing loader markup']
                    ]) {
                      const expected = await page.locator(`#${field}`).inputValue();
                      await page.locator(`#${button}`).click();
                      await page.locator('#patron-copy-status').filter({ hasText: `${label} copied.` }).waitFor();
                      assert.equal((await page.evaluate(() => navigator.clipboard.readText())).replace(/\r\n/g, '\n'),
                        expected.replace(/\r\n/g, '\n'), 'Clipboard preserves the exact markup apart from native line endings.');
                      await auditSpacing(`${state}-${button}`);
                      report.publicLinks.push({ scope, button, copied: true });
                    }
                  }
                  const rule = page.locator('#format-rules-editor [data-rule-toggle]').first();
                  if (await rule.count()) { await rule.click(); await auditSpacing(`${state}-format-expanded`); await rule.click(); }
                  if (scope !== 'system' && ['desktop', '390', 'css-200'].includes(config.name)) {
                    assert.equal(await page.locator('#add-publication-option').isEnabled(), true);
                    for (const button of ['#add-publication-option', '#add-material-format', '#add-custom-field', '#add-auto-claim-rule']) {
                      await page.locator(button).click();
                    }
                    await auditSpacing(`${state}-rows-added`);
                    const publications = page.locator('#publication-options-editor [data-domain-row]');
                    const order = await publications.evaluateAll(rows => rows.map(row => row.dataset.optionId));
                    const move = publications.last().locator('[data-row-action="up"]');
                    assert.match(await move.getAttribute('aria-label'), /^Move .+ up$/, 'Publication moves retain row-specific names.');
                    if (await move.isEnabled()) {
                      await move.click();
                      [order[order.length - 2], order[order.length - 1]] = [order.at(-1), order.at(-2)];
                      assert.deepEqual(await publications.evaluateAll(rows => rows.map(row => row.dataset.optionId)), order,
                        'Moving a publication option preserves its generated identity and moves the intended row.');
                      await auditSpacing(`${state}-rows-reordered`);
                    }
                    await discardSpacingDraft();
                  }
                }
                if (panel === 'templates') {
                  const template = page.locator('#settings-template-editor [data-template-toggle]').first();
                  await template.click(); await auditSpacing(`${state}-expanded`); await template.click();
                  if (['desktop', '390', 'css-200'].includes(config.name)) {
                    await page.locator('#add-email-template').click();
                    await auditSpacing(`${state}-additional-added`);
                    await discardSpacingDraft();
                  }
                }
              }
            }
            await page.setViewportSize({ width, height: 900 });
            await page.emulateMedia({ colorScheme: 'light' });
            await page.evaluate(() => { document.documentElement.style.zoom = ''; });
            assert.deepEqual(await draftStatus(), draftBefore, 'The audit restores each deliberate draft through its normal owner.');
            assert.equal(apiWrites.length, writesBefore, 'Spacing, copy and deliberate discarded drafts never write an API.');
          }
          await page.locator('#settings-nav-start').click();
          const staffUrlInput = page.locator('#system-staff-url');
          const useCurrentAddress = page.getByRole('button', { name: 'Use current address', exact: true });
          const staffUrlPreview = page.locator('#system-staff-url-candidate');
          assert.equal(await page.getByLabel('Staff URL', { exact: true }).count(), 1,
            `${width}px/${scope}: Staff URL retains its accessible label`);
          assert.equal(await staffUrlPreview.getAttribute('role'), 'status');
          assert.equal(await staffUrlPreview.getAttribute('aria-live'), 'polite');
          assert.equal(await staffUrlPreview.isVisible(), true);
          assert.equal(await useCurrentAddress.isVisible(), true,
            `${width}px/${scope}: a supported current address has an explicit action`);
          const suggestionGeometry = await page.evaluate(() => {
            const input = document.getElementById('system-staff-url').getBoundingClientRect();
            const button = document.getElementById('use-current-staff-url').getBoundingClientRect();
            const preview = document.getElementById('system-staff-url-candidate');
            const bounds = preview.getBoundingClientRect();
            return { width: innerWidth, scrollWidth: document.documentElement.scrollWidth,
              input: { top: input.top, right: input.right, bottom: input.bottom },
              button: { top: button.top, left: button.left, bottom: button.bottom },
              preview: { top: bounds.top, bottom: bounds.bottom, width: bounds.width,
                scrollWidth: preview.scrollWidth, overflowWrap: getComputedStyle(preview).overflowWrap } };
          });
          if (width > 760) {
            assert.ok(suggestionGeometry.button.left >= suggestionGeometry.input.right,
              `${width}px/${scope}: action sits beside Staff URL`);
          } else {
            assert.ok(suggestionGeometry.button.top >= suggestionGeometry.input.bottom,
              `${width}px/${scope}: action stacks below Staff URL on mobile`);
          }
          assert.ok(suggestionGeometry.preview.top >= suggestionGeometry.button.bottom,
            `${width}px/${scope}: candidate is visible below the input/action`);
          assert.ok(suggestionGeometry.scrollWidth <= suggestionGeometry.width,
            `${width}px/${scope}: suggestion controls have no horizontal overflow`);

          if (scope === 'system') {
            const expectedCandidate = await page.evaluate(() => new URL('/staff/', window.location.href).href);
            assert.equal(await staffUrlPreview.textContent(), `Current address candidate: ${expectedCandidate}`,
              'The displayed candidate uses only the browser origin and canonical Staff path.');
            const candidateUrl = new URL(expectedCandidate);
            assert.equal(candidateUrl.pathname, '/staff/');
            assert.equal(candidateUrl.search, '');
            assert.equal(candidateUrl.hash, '');
            assert.equal(candidateUrl.username, '');
            assert.equal(candidateUrl.password, '');
            const originalStaffUrl = await staffUrlInput.inputValue();
            const writesBefore = staffSettingsPosts.length;
            await staffUrlInput.focus();
            await page.keyboard.press('Tab');
            assert.equal(await page.evaluate(() => document.activeElement.id), 'use-current-staff-url',
              `${width}px/${scope}: keyboard reaches the suggestion directly after Staff URL`);
            assert.equal(await useCurrentAddress.evaluate(node => node.matches(':focus-visible')), true,
              `${width}px/${scope}: the suggestion action has visible keyboard focus`);
            await page.keyboard.press('Enter');
            assert.equal(await staffUrlInput.inputValue(), expectedCandidate);
            const changed = originalStaffUrl !== expectedCandidate;
            assert.equal(await page.locator('#settings-save').isEnabled(), changed,
              `${width}px/${scope}: applying the candidate updates only a changed draft`);
            assert.equal(await page.locator('#settings-save-title').textContent(), changed ? 'Unsaved changes' : 'No changes');
            assert.match(await staffUrlPreview.textContent(), changed
              ? /Staff URL draft updated/ : /Staff URL already matches/);
            await scan(`settings-current-address-${scope}`);
            if (width === 390) {
              const originalPreview = await staffUrlPreview.textContent();
              const longEligibleCandidate = `Current address candidate: https://${'long-subdomain.'.repeat(16)}example.org:9443/staff/`;
              try {
                await staffUrlPreview.evaluate((node, text) => { node.textContent = text; }, longEligibleCandidate);
                const wrapped = await page.evaluate(() => {
                  const preview = document.getElementById('system-staff-url-candidate');
                  const bounds = preview.getBoundingClientRect();
                  return { width: innerWidth, scrollWidth: document.documentElement.scrollWidth,
                    previewWidth: bounds.width, previewScrollWidth: preview.scrollWidth,
                    overflowWrap: getComputedStyle(preview).overflowWrap };
                });
                assert.equal(wrapped.overflowWrap, 'anywhere');
                assert.ok(wrapped.previewWidth > 0 && wrapped.previewScrollWidth <= wrapped.previewWidth + 1,
                  'A long eligible origin wraps inside the mobile candidate area.');
                assert.ok(wrapped.scrollWidth <= wrapped.width, 'A long mobile candidate does not cause horizontal overflow.');
                await scan(`settings-current-address-long-${scope}`);
              } finally {
                await staffUrlPreview.evaluate((node, text) => { node.textContent = text; }, originalPreview);
              }
            }
            if (changed) {
              await decideConfirmation(page, () => page.locator('#settings-refresh').click(), dialog => dialog.dismiss());
              assert.equal(await staffUrlInput.inputValue(), expectedCandidate,
                'Canceling refresh preserves the selected candidate draft.');
              assert.equal(await page.locator('#settings-save').isEnabled(), true);
              await decideConfirmation(page, () => page.locator('#settings-discard').click(), dialog => dialog.accept());
              await page.waitForFunction(expected => {
                const form = document.getElementById('settings-form');
                return !form.inert && document.getElementById('settings-save').disabled &&
                  document.getElementById('system-staff-url').value === expected;
              }, originalStaffUrl);
            } else {
              const acceptedReload = page.waitForResponse(response => response.request().method() === 'GET' &&
                new URL(response.url()).pathname === '/api/asap/staff/settings');
              await page.locator('#settings-refresh').click();
              await acceptedReload;
              await page.waitForFunction(() => !document.getElementById('settings-refresh').disabled &&
                !document.getElementById('settings-form').inert);
              await page.waitForFunction(expected => !document.getElementById('settings-form').inert &&
                document.getElementById('system-staff-url').value === expected, originalStaffUrl);
            }
            assert.equal(staffSettingsPosts.length, writesBefore,
              `${width}px/${scope}: choosing, canceling, and discarding a suggestion never sends a save`);
            report.staffUrlSuggestions.push({ width, candidate: expectedCandidate, changed, originalStaffUrl,
              mobileLongCandidateWrapped: width === 390 });
          } else {
            assert.equal(await staffUrlInput.isDisabled(), true, `${width}px/${scope}: the system field remains disabled in Library scope`);
            assert.equal(await useCurrentAddress.evaluate(node => node.matches(':disabled')), true,
              `${width}px/${scope}: the suggestion action is disabled in Library scope`);
          }
          await verifyPolarisSettings(page, width, scope, scan, report);
          await page.locator('#settings-nav-workflow').click();
          assert.equal(await page.locator('#patron-code-eligibility legend').textContent(), 'Patron code eligibility');
          assert.equal(await page.locator('#patron-code-eligibility').getByText(
            'The patron-code eligibility message is displayed as written.', { exact: true }).count(), 0,
          `${scope}: the redundant patron-code eligibility callout is absent`);
          assert.equal(await page.locator('#patron-code-eligibility-message').evaluate(node =>
            node.labels[0].querySelector('span').textContent.trim()), 'Patron-code eligibility message',
          `${scope}: the message editor keeps its label`);
          await providerGeometry(page);
          if (await page.locator('#add-provider').isVisible()) await measureHeading('#add-provider');
          await ensureCommonCreatorsVisible();
          await measureHeading('#add-common-creator');

          await page.locator('#add-common-creator').click();
          await page.locator('#common-creators-editor [data-domain-row]').last().locator('input').fill('Spacing regression creator');
          const rowGaps = await measureActions('#common-creators-editor [data-domain-row]:last-child .settings-row-actions');
          assert.equal(rowGaps.length, 2);
          assert.ok(rowGaps.every(gap => gap >= 7),
            `${scope}: adjacent Settings actions need visible separation: ${JSON.stringify(rowGaps)}`);
          await scan(`workflow-actions-${scope}`);
          await decideConfirmation(page, () => page.locator('#settings-discard').click(), dialog => dialog.accept());
          await page.locator('#settings-discard').waitFor({ state: 'hidden' });
          await page.locator('#settings-refresh:not([disabled])').waitFor();
          await page.locator('#settings-nav-patron').click();
          await page.locator('#settings-patron').waitFor({ state: 'visible' });
          const brandingPlacement = await page.evaluate(() => {
            const panel = document.getElementById('settings-patron');
            const intro = panel.querySelector('.settings-panel-heading');
            const fieldsets = [...panel.children].filter(node => node.matches('fieldset.settings-fieldset'));
            const rect = node => {
              const bounds = node.getBoundingClientRect();
              return { top: bounds.top, bottom: bounds.bottom, left: bounds.left, right: bounds.right,
                width: bounds.width, height: bounds.height };
            };
            return {
              legends: fieldsets.map(fieldset => fieldset.querySelector('legend').textContent.trim()),
              intro: rect(intro), fieldsets: fieldsets.map(rect),
              brandingIds: [...fieldsets[0].querySelectorAll('[id]')].map(node => node.id),
              viewportWidth: innerWidth, scrollWidth: document.documentElement.scrollWidth
            };
          });
          assert.deepEqual(brandingPlacement.legends, [
            'Branding', 'Public patron portal', 'Patron text', 'Publication options and formats'
          ], `${width}px/${scope}: Branding is first and the other Patron sections retain their order`);
          assert.deepEqual(brandingPlacement.brandingIds, [
            'branding-preview-source', 'branding-preview', 'branding-alt', 'branding-logo', 'save-branding-logo',
            'discard-branding-draft', 'clear-branding-logo', 'branding-status'
          ], `${width}px/${scope}: preview, inputs, actions, and feedback remain together`);
          assert.ok(brandingPlacement.fieldsets[0].top >= brandingPlacement.intro.bottom,
            `${width}px/${scope}: Branding follows the Patron experience introduction`);
          for (let index = 1; index < brandingPlacement.fieldsets.length; index++) {
            assert.ok(brandingPlacement.fieldsets[index].top >= brandingPlacement.fieldsets[index - 1].bottom,
              `${width}px/${scope}: Patron fieldsets retain visual DOM order`);
          }
          assert.ok(brandingPlacement.scrollWidth <= brandingPlacement.viewportWidth,
            `${width}px/${scope}: Branding placement has no horizontal page overflow`);
          await page.keyboard.press('Tab');
          const brandingFocus = await page.evaluate(() => {
            const first = document.querySelector('#settings-patron > fieldset.settings-fieldset');
            return { insideBranding: first.contains(document.activeElement), visible: document.activeElement.matches(':focus-visible'),
              id: document.activeElement.id };
          });
          assert.equal(brandingFocus.insideBranding, true, `${width}px/${scope}: keyboard focus enters Branding first`);
          assert.equal(brandingFocus.visible, true, `${width}px/${scope}: first Branding control has visible keyboard focus`);
          report.brandingPlacements.push({ width, scope, legends: brandingPlacement.legends,
            fieldsetBounds: brandingPlacement.fieldsets, focusedControl: brandingFocus.id });
          await scan(`settings-branding-${scope}`);
          for (const buttonId of ['#add-publication-option', '#add-material-format', '#add-custom-field', '#add-format-rule', '#add-auto-claim-rule']) {
            if (await page.locator(buttonId).isVisible()) await measureHeading(buttonId);
          }
          await page.locator('#settings-nav-templates').click();
          await measureHeading('#add-email-template');
          await scan(`settings-headings-${scope}`);
          await page.locator('#settings-nav-workflow').click();
        }
        await ensureCommonCreatorsVisible();
        await measureHeading('#add-common-creator');
        if (await page.locator('#settings-save').isEnabled()) {
          await decideConfirmation(page, () => page.locator('#settings-discard').click(), dialog => dialog.accept());
          await page.locator('#settings-discard').waitFor({ state: 'hidden' });
          await page.locator('#settings-refresh:not([disabled])').waitFor();
        }
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
        // Polaris sync retires the operational catalog. Finish its reviewed route
        // activation and queue reload before checking the tab's keyboard focus.
        await page.locator('#queue-view:not([hidden])').waitFor();
        await page.locator('#refresh-queue:not([disabled])').waitFor();
        await quietFeedback();
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
    } finally {
      try { await restoreStaffUrl?.(); } finally { await browser.close(); }
    }
  }
  report.settingsAccessibility = await runSourceComponents(path.join(artifacts, 'settings-accessibility'));
  assert.deepEqual(report.externalRequests, []); assert.deepEqual(report.pageErrors, []);
  await fs.writeFile(path.join(artifacts, 'browser-results.json'), JSON.stringify(report, null, 2));
  console.log('Staff header, spacing, lookup sorting and horizontal-only tab geometry passed.');
})().catch(error => { console.error(error); process.exitCode = 1; });
