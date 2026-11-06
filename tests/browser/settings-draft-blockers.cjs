'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');
const { decideConfirmation, rejectNativeDialogs } = require('./confirmation.cjs');

// Runs against the production host and the owning native test's isolated SQL
// library. Only fixture accounts/settings are written; providers stay local.
exports.verifySettingsDraftBlockers = async (page, args, report, ready, scan) => {
  const context = page.context();
  const session = await (await context.request.get(`${args.baseOrigin}/api/asap/staff/session`)).json();
  const headers = { 'X-ASAP-Antiforgery': session.antiforgeryToken };
  const api = async (method, url, data, status = 200) => {
    const response = await context.request[method](`${args.baseOrigin}${url}`, { headers, data });
    assert.equal(response.status(), status, await response.text()); return response.json();
  };
  const read = () => api('get', `/api/asap/staff/settings?orgId=${args.scopeId}`);
  const post = async values => {
    const saved = await read();
    return api('post', '/api/asap/staff/settings', { orgId: args.scopeId, version: saved.version, ...values });
  };
  const original = await read(), writes = [], witness = report.draftBlockers = { focus: [], native200: [] };
  const record = request => {
    if (request.method() !== 'GET' && /^\/api\/asap\/staff\/(?:settings|users)(?:\/|$)/.test(new URL(request.url()).pathname)) {
      writes.push({ method: request.method(), path: new URL(request.url()).pathname, body: request.postDataJSON() });
    }
  };
  const first = (await api('post', '/api/asap/staff/users', {
    email: `draft-a-${args.formatCode}@example.org`, role: 'staff', organizationId: Number(args.scopeId)
  }, 201)).user;
  const second = (await api('post', '/api/asap/staff/users', {
    email: `draft-b-${args.formatCode}@example.org`, role: 'staff', organizationId: Number(args.scopeId)
  }, 201)).user;
  witness.staffIds = [first.id, second.id];
  const row = id => page.locator(`.settings-staff-row[data-staff-id="${id}"]`);
  const link = async id => {
    const position = await row(id).evaluate(node => [...node.parentElement.children].indexOf(node) + 1);
    return page.getByRole('button', { name: `Go to Staff access — Staff-account edits (roster row ${position})`, exact: true });
  };
  const open = async id => {
    const disclosure = row(id).locator('.settings-staff-summary');
    if (await disclosure.getAttribute('aria-expanded') === 'false') await disclosure.click();
  };
  const settled = () => page.waitForFunction(() => !document.getElementById('settings-form').inert &&
    !document.getElementById('settings-form').hasAttribute('aria-busy') && !document.getElementById('staff-refresh').disabled);
  const staff = async () => { await page.locator('#settings-nav-staff').click(); await row(first.id).waitFor(); await settled(); };
  const reload = async () => { await page.reload({ waitUntil: 'networkidle' }); await ready(args.scopeId); };
  const cancel = id => row(id).getByRole('button', { name: 'Cancel', exact: true }).click();
  const blocked = async count => {
    await page.waitForFunction(count => document.getElementById('settings-message').textContent.startsWith(`${count} unsaved draft`), count);
    assert.equal(await page.locator('#settings-draft-blockers button').count(), count);
    const message = await page.locator('#settings-draft-blockers').textContent();
    assert.doesNotMatch(message, /draft-[ab]-|@example|private|Symbol|userPrincipalName|staff editor/);
    assert.equal(await page.locator('#settings-draft-blockers button').first().evaluate(node => node === document.activeElement), true);
  };
  const focus = async (name, target) => {
    assert.equal(await target.evaluate(node => node === document.activeElement), true, name);
    const bounds = await target.boundingBox(), viewport = await page.evaluate(() => ({ width: innerWidth, height: innerHeight }));
    assert.ok(bounds && bounds.x >= 0 && bounds.y >= 6 && bounds.x + bounds.width <= viewport.width && bounds.y + bounds.height <= viewport.height - 6,
      `${name}: full focused control must be visible: ${JSON.stringify({ bounds, viewport })}`);
    assert.match(await page.locator('#app-status').textContent(), /Showing .*Unsaved changes are preserved/);
    witness.focus.push({ name, bounds, viewport });
  };
  const saveSettings = async () => {
    const response = page.waitForResponse(response => response.request().method() === 'POST' && new URL(response.url()).pathname === '/api/asap/staff/settings');
    await page.locator('#settings-save').click(); assert.equal((await response).status(), 200);
    await page.waitForFunction(() => document.getElementById('settings-save-title').textContent === 'No changes' && !document.getElementById('settings-form').inert);
  };
  const discard = async () => {
    await decideConfirmation(page, () => page.locator('#settings-discard').click(), dialog => dialog.accept());
    await page.waitForFunction(() => document.getElementById('settings-save-title').textContent === 'No changes' && !document.getElementById('settings-form').inert);
  };
  page.on('request', record);
  try {
    await reload(); await staff(); await open(first.id);
    await row(first.id).getByLabel(/^Display name/).fill('private first draft');
    await row(first.id).getByLabel(/^Display name/).press('Escape');
    await page.locator('#settings-nav-workflow').click();
    const message = page.locator('#suggestion-limit-message');
    await message.fill('private Workflow draft');
    await page.locator('#settings-save').click(); await blocked(1);
    await scan('draft-blocker-desktop-list');
    await (await link(first.id)).press('Enter');
    assert.equal(await row(first.id).locator('.settings-staff-summary').getAttribute('aria-expanded'), 'true');
    await focus('Workflow to exact Staff editor', row(first.id).getByLabel(/^Email address/));
    assert.equal(await message.inputValue(), 'private Workflow draft'); assert.equal(writes.length, 0);
    await scan('draft-blocker-desktop-target');

    await open(second.id); await row(second.id).getByLabel(/^Display name/).fill('private second draft');
    await row(second.id).getByLabel(/^Display name/).press('Escape');
    await page.locator('#staff-add-toggle').click();
    await page.locator('#staff-add-email').fill('private.new@example.org');
    await page.keyboard.press('Escape');
    await page.locator('#settings-nav-workflow').click(); await page.locator('#settings-save').click(); await blocked(3);
    await page.setViewportSize({ width: 390, height: 844 });
    await scan('draft-blockers-mobile-list');
    await (await link(second.id)).press('Enter');
    await focus('Mobile second Staff editor', row(second.id).getByLabel(/^Email address/));
    assert.equal(await row(first.id).getByLabel(/^Display name/).inputValue(), 'private first draft');
    assert.equal(await row(second.id).getByLabel(/^Display name/).inputValue(), 'private second draft');
    await scan('draft-blockers-mobile-target');
    await page.getByRole('button', { name: 'Go to Staff access — New staff-account draft', exact: true }).press('Enter');
    await focus('New-account draft', page.locator('#staff-add-email'));
    assert.equal(await page.locator('#staff-add-email').inputValue(), 'private.new@example.org');
    assert.equal(await message.inputValue(), 'private Workflow draft'); assert.equal(writes.length, 0);
    await page.locator('#staff-add-cancel').click(); await cancel(second.id); await cancel(first.id);
    assert.equal(await page.locator('#settings-draft-blockers').isHidden(), true);
    await page.locator('#settings-nav-workflow').click(); await saveSettings();
    assert.equal((await read()).workflow.suggestionLimitMessage, 'private Workflow draft');
    assert.equal(writes.length, 1, 'The original action dispatches only after all blockers are explicitly resolved.');
    witness.multipleOwnersAndRetry = true;

    await page.setViewportSize({ width: 1280, height: 900 }); await staff(); await open(first.id); await open(second.id);
    await row(first.id).getByLabel(/^Display name/).fill('private saved blocker');
    await decideConfirmation(page, () => row(second.id).getByRole('button', { name: 'Deactivate', exact: true }).click(), dialog => dialog.accept());
    await blocked(1); await (await link(first.id)).press('Enter');
    await focus('Same-section Staff blocker', row(first.id).getByLabel(/^Email address/));
    assert.equal(await page.locator('#settings-save').isDisabled(), true, 'Navigation alone never creates a Settings draft.');
    await row(first.id).getByLabel(/^Email address/).fill(second.userPrincipalName);
    const rejected = page.waitForResponse(response => response.request().method() === 'PUT' && response.url().endsWith(`/users/${first.id}`));
    await row(first.id).getByRole('button', { name: 'Save changes', exact: true }).click();
    assert.equal((await rejected).status(), 409); await settled();
    assert.equal(await row(first.id).getByLabel(/^Display name/).inputValue(), 'private saved blocker');
    await decideConfirmation(page, () => row(second.id).getByRole('button', { name: 'Deactivate', exact: true }).click(), dialog => dialog.accept());
    await blocked(1); const staleUncertain = await (await link(first.id)).elementHandle();
    await (await link(first.id)).press('Enter');
    await row(first.id).getByLabel(/^Email address/).fill(first.userPrincipalName);
    let committed;
    const loseReceipt = async route => {
      if (route.request().method() !== 'PUT') return route.fallback();
      const response = await route.fetch(); assert.equal(response.status(), 200); committed = await response.json();
      await route.fulfill({ status: 200, json: { ...committed, user: { ...committed.user, notificationEmail: 'unconfirmed@example.org' } } });
    };
    await page.route(`**/api/asap/staff/users/${first.id}`, loseReceipt);
    await row(first.id).getByRole('button', { name: 'Save changes', exact: true }).click();
    await page.waitForFunction(() => document.getElementById('settings-save-title').textContent === 'Outcome uncertain; reload needed');
    await page.unroute(`**/api/asap/staff/users/${first.id}`, loseReceipt);
    const beforeUncertainNavigation = writes.length;
    await staleUncertain.evaluate(button => { const list = document.getElementById('settings-draft-blockers'); list.hidden = false; list.append(button); });
    await staleUncertain.press('Enter');
    await page.locator('#settings-message').filter({ hasText: 'Refresh settings to reconcile' }).waitFor();
    assert.equal(writes.length, beforeUncertainNavigation); assert.equal(await row(first.id).getByLabel(/^Display name/).inputValue(), 'private saved blocker');
    await decideConfirmation(page, () => page.locator('#settings-refresh').click(), dialog => dialog.accept()); await settled();
    assert.equal(await row(first.id).getByLabel(/^Display name/).inputValue(), committed.user.displayName);
    assert.equal(await page.locator('#settings-save-title').textContent(), 'No changes');
    const originalRetry = page.waitForResponse(response => response.request().method() === 'DELETE' && response.url().endsWith(`/users/${second.id}`));
    await decideConfirmation(page, () => row(second.id).getByRole('button', { name: 'Deactivate', exact: true }).click(), dialog => dialog.accept());
    assert.equal((await originalRetry).status(), 200); await settled();
    witness.failedAndUncertainBlockerRecovery = true;

    const system = await api('get', '/api/asap/staff/settings?orgId=system');
    await post({ workflow: { suggestionLimit: system.workflow.suggestionLimit } }); await reload();
    await page.locator('#settings-nav-workflow').click();
    const limit = page.locator('#suggestion-limit'), acceptedLimit = await limit.inputValue();
    await page.locator('[data-setting-key="suggestionLimit"] .settings-use-system').press('Enter');
    assert.equal(await limit.inputValue(), acceptedLimit); assert.equal(await page.locator('#settings-save').isEnabled(), true);
    await staff(); await open(first.id); await row(first.id).getByLabel(/^Display name/).fill('private reset blocker');
    const beforeResetNavigation = writes.length;
    await row(first.id).getByRole('button', { name: 'Save changes', exact: true }).click(); await blocked(1);
    await page.getByRole('button', { name: 'Go to Workflow — Weekly suggestion limit', exact: true }).press('Enter');
    await focus('Equal-display pending reset', limit);
    assert.match(await page.locator('[data-setting-key="suggestionLimit"] .settings-inheritance-status').textContent(), /Using system value.*Unsaved/);
    assert.equal(writes.length, beforeResetNavigation);
    await staff(); await cancel(first.id); await page.locator('#settings-nav-workflow').click(); await saveSettings();
    assert.equal(writes.at(-1).body.workflow.suggestionLimit, null);
    assert.equal((await read()).stored.libraryOverride.workflow.suggestionLimit, null);
    witness.equalDisplayResetPreserved = true;

    await page.locator('#settings-nav-patron').click();
    const book = page.locator('#format-rules-editor [data-rule-code="book"]'), disclosure = book.locator('[data-rule-toggle]');
    await disclosure.click(); await book.locator('[data-format-label="author"]').fill('private rule draft'); await disclosure.click();
    await staff(); await open(first.id); await row(first.id).getByLabel(/^Display name/).fill('private Staff with rule draft');
    const beforeRuleNavigation = writes.length;
    await row(first.id).getByRole('button', { name: 'Save changes', exact: true }).click();
    await page.getByRole('button', { name: 'Go to Patron experience — Format rules', exact: true }).press('Enter');
    assert.equal(await disclosure.getAttribute('aria-expanded'), 'true'); await focus('Exact dirty rule disclosure', disclosure);
    assert.equal(await book.locator('[data-format-label="author"]').inputValue(), 'private rule draft');
    assert.equal(writes.length, beforeRuleNavigation); await discard();
    await page.locator('#settings-nav-templates').click();
    const template = page.locator('fieldset.settings-template[data-template-key="suggestion_submitted"]');
    await template.locator('[data-template-toggle]').click(); await template.locator('textarea').fill('private template draft');
    await template.locator('[data-template-toggle]').click(); await staff(); await open(first.id);
    await row(first.id).getByLabel(/^Display name/).fill('private Staff with template draft');
    await row(first.id).getByRole('button', { name: 'Save changes', exact: true }).click();
    await page.getByRole('button', { name: 'Go to Email templates — Submission confirmation — Body', exact: true }).press('Enter');
    assert.equal(await template.locator('[data-template-toggle]').getAttribute('aria-expanded'), 'true');
    await focus('Exact dirty template Body', template.locator('textarea'));
    assert.equal(await template.locator('textarea').inputValue(), 'private template draft'); await discard();
    witness.settingsFieldsAndDisclosures = true;

    await staff(); await open(first.id); await row(first.id).getByLabel(/^Display name/).fill('private old context');
    await page.locator('#settings-nav-workflow').click(); await message.fill('private old Workflow');
    await page.locator('#settings-save').click(); await blocked(1);
    const oldContext = await (await link(first.id)).elementHandle(), beforeContextNavigation = writes.length;
    await decideConfirmation(page, () => page.locator('#settings-scope').selectOption('system'), dialog => dialog.accept()); await ready('system');
    await oldContext.evaluate(button => { const list = document.getElementById('settings-draft-blockers'); list.hidden = false; list.append(button); });
    await oldContext.press('Enter');
    await page.locator('#settings-message').filter({ hasText: 'earlier Settings context' }).waitFor();
    assert.equal(await page.locator('#settings-scope').inputValue(), 'system'); assert.equal(writes.length, beforeContextNavigation);
    assert.equal(await page.locator('#settings-save-title').textContent(), 'No changes');
    await page.locator('#settings-scope').selectOption(args.scopeId); await ready(args.scopeId);
    witness.staleContextActionRetired = true;

    await staff(); await open(first.id); await row(first.id).getByLabel(/^Display name/).fill('private retired owner');
    await row(first.id).getByLabel(/^Display name/).press('Escape');
    await page.locator('#settings-nav-workflow').click(); await message.fill('private retired Workflow');
    await page.locator('#settings-save').click(); await blocked(1);
    const retiredAction = await (await link(first.id)).elementHandle(), retiredRow = await row(first.id).elementHandle();
    await retiredRow.evaluate(node => node.remove());
    // Cross a real disconnected turn before reinsertion; a mounted reorder
    // within one render remains an existing owner in the retained contracts.
    await page.evaluate(() => Promise.resolve());
    await retiredRow.evaluate(node => document.getElementById('settings-staff-users-list').append(node));
    const beforeRetiredNavigation = writes.length;
    await retiredAction.press('Enter');
    await page.locator('#settings-message').filter({ hasText: 'no longer current' }).waitFor();
    assert.equal(await row(first.id).locator('.settings-staff-summary').getAttribute('aria-expanded'), 'false');
    assert.equal(await message.inputValue(), 'private retired Workflow'); assert.equal(writes.length, beforeRetiredNavigation);
    await decideConfirmation(page, () => page.locator('#settings-refresh').click(), dialog => dialog.accept());
    await page.waitForFunction(node => !node.isConnected, retiredRow); await ready(args.scopeId);
    assert.equal(await retiredRow.evaluate(node => node.isConnected), false, 'Explicit recovery creates a new accepted row owner.');
    await staff(); await open(first.id); await row(first.id).getByLabel(/^Display name/).fill('Fresh owner accepted');
    const freshSave = page.waitForResponse(response => response.request().method() === 'PUT' && response.url().endsWith(`/users/${first.id}`));
    await row(first.id).getByRole('button', { name: 'Save changes', exact: true }).click();
    assert.equal((await freshSave).status(), 200); await settled();
    assert.equal(await row(first.id).getByLabel(/^Display name/).inputValue(), 'Fresh owner accepted');
    const summary = row(first.id).locator('.settings-staff-summary');
    if (await summary.getAttribute('aria-expanded') === 'true') await summary.click();
    const beforeRetiredRetry = writes.length;
    await retiredAction.evaluate(button => { const list = document.getElementById('settings-draft-blockers'); list.hidden = false; list.append(button); });
    await retiredAction.press('Enter');
    assert.equal(await summary.getAttribute('aria-expanded'), 'false', 'The former handle cannot route to the replacement row.');
    assert.equal(writes.length, beforeRetiredRetry);
    witness.retiredOwnerRecovered = true;

    await verifyNative200(args, first.id, witness);
    assert.equal(witness.native200.length, 2);
    witness.noNavigationWrites = true;
  } finally {
    page.off('request', record);
    witness.writes = writes;
    await post({ workflow: { suggestionLimitMessage: original.stored.libraryOverride.workflow?.suggestionLimitMessage ?? null,
      suggestionLimit: original.stored.libraryOverride.workflow?.suggestionLimit ?? null } });
    await reload(); await page.setViewportSize({ width: 1280, height: 900 });
  }
};

async function verifyNative200(args, staffId, witness) {
  const extension = path.join(__dirname, 'native-zoom-extension');
  const axe = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  for (const width of [1280, 1000]) {
    const errors = [], external = [], mutations = [];
    const context = await chromium.launchPersistentContext(path.join(args.artifactRoot, `draft-zoom-${width}`), {
      channel: 'chromium', headless: true, viewport: null,
      extraHTTPHeaders: { 'X-ASAP-Test-Staff-Id': args.identity.staffId, 'X-ASAP-Test-Tenant-Id': args.identity.tenantId, 'X-ASAP-Test-Staff-Email': args.identity.email },
      args: [`--disable-extensions-except=${extension}`, `--load-extension=${extension}`, `--window-size=${width},900`]
    });
    try {
      await context.route('**/*', route => {
        if (new URL(route.request().url()).origin === args.baseOrigin) return route.continue();
        external.push(route.request().url()); return route.abort();
      });
      const page = context.pages()[0] || await context.newPage();
      page.on('pageerror', error => errors.push(error.message)); rejectNativeDialogs(page, errors);
      page.on('request', request => { if (request.method() !== 'GET') mutations.push(request.url()); });
      await page.goto(`${args.baseOrigin}/staff/?stage=settings&settingsScope=${args.scopeId}#settings-workflow`, { waitUntil: 'networkidle' });
      await page.waitForFunction(() => !document.getElementById('settings-form').hidden && !document.getElementById('settings-form').inert);
      const ratio = await page.evaluate(() => devicePixelRatio);
      const worker = context.serviceWorkers()[0] || await context.waitForEvent('serviceworker');
      const zoom = await worker.evaluate(async origin => {
        const tab = (await chrome.tabs.query({})).find(tab => tab.url.startsWith(origin));
        await chrome.tabs.setZoomSettings(tab.id, { mode: 'automatic', scope: 'per-tab' }); await chrome.tabs.setZoom(tab.id, 2);
        return chrome.tabs.getZoom(tab.id);
      }, args.baseOrigin);
      assert.equal(zoom, 2); await page.waitForFunction(prior => Math.abs(devicePixelRatio / prior - 2) < 0.01, ratio);
      await page.locator('#settings-nav-staff').click();
      const row = page.locator(`.settings-staff-row[data-staff-id="${staffId}"]`);
      await row.locator('.settings-staff-summary').click(); await row.getByLabel(/^Display name/).fill('private native draft');
      await row.getByLabel(/^Display name/).press('Escape');
      await page.locator('#settings-nav-workflow').click(); await page.locator('#suggestion-limit-message').fill('private native Workflow');
      await page.locator('#settings-save').click();
      const action = page.locator('#settings-draft-blockers button'); await action.waitFor();
      assert.equal(await action.evaluate(node => node === document.activeElement), true); await action.press('Enter');
      const email = row.getByLabel(/^Email address/);
      assert.equal(await email.evaluate(node => node === document.activeElement), true);
      assert.equal(await row.locator('.settings-staff-summary').getAttribute('aria-expanded'), 'true');
      await page.evaluate(axe);
      const state = await page.evaluate(async ratio => {
        const control = document.activeElement, bounds = control.getBoundingClientRect(), style = getComputedStyle(control);
        const clearance = Math.max(6, (parseFloat(style.outlineWidth) || 0) + (parseFloat(style.outlineOffset) || 0));
        return { width: innerWidth, height: innerHeight, ratio: devicePixelRatio / ratio, htmlZoom: getComputedStyle(document.documentElement).zoom,
          viewportScale: visualViewport.scale, bounds: { left: bounds.left, right: bounds.right, top: bounds.top, bottom: bounds.bottom }, clearance,
          focusedEmail: control.type === 'email', announcement: document.getElementById('app-status').textContent,
          overflow: document.documentElement.scrollWidth > innerWidth,
          imagesLoaded: [...document.images].filter(node => node.getClientRects().length).every(node => node.complete && node.naturalWidth > 0),
          violations: (await axe.run()).violations.filter(item => ['serious', 'critical'].includes(item.impact)) };
      }, ratio);
      const session = await context.newCDPSession(page), metrics = await session.send('Page.getLayoutMetrics');
      assert.equal(metrics.visualViewport.zoom, 2); assert.equal(state.htmlZoom, '1'); assert.equal(state.viewportScale, 1);
      assert.ok(Math.abs(state.ratio - 2) < 0.01); assert.equal(state.focusedEmail, true);
      assert.ok(state.bounds.top >= state.clearance && state.bounds.bottom <= state.height - state.clearance && state.bounds.left >= 0 && state.bounds.right <= state.width,
        `Native 200% full focus containment: ${JSON.stringify(state)}`);
      assert.equal(state.overflow, false); assert.equal(state.imagesLoaded, true); assert.deepEqual(state.violations, []);
      assert.deepEqual(mutations, []); assert.deepEqual(errors, []); assert.deepEqual(external, []);
      const screenshot = await session.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
      await fs.writeFile(path.join(args.artifactRoot, `draft-blockers-native200-${width}.png`), Buffer.from(screenshot.data, 'base64'));
      witness.native200.push({ physicalWidth: width, zoom, ...state });
      await session.detach();
    } finally { await context.close(); }
  }
}
