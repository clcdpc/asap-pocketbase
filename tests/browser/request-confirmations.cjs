'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');
const { decideConfirmation, rejectNativeDialogs } = require('./confirmation.cjs');
const [baseURL, artifacts, staffId, tenantId, email, templateId, purchaseId, rejectionId, savedBibId, optoutId, untouchedId] = process.argv.slice(2);

(async () => {
  await fs.mkdir(artifacts, { recursive: true });
  const headers = { 'X-ASAP-Test-Staff-Id': staffId, 'X-ASAP-Test-Tenant-Id': tenantId, 'X-ASAP-Test-Staff-Email': email };
  const browser = await chromium.launch({ headless: true, ...(process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH
    ? { executablePath: process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH } : {}) });
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, extraHTTPHeaders: headers });
  const report = { states: [], errors: [], external: [], actions: [], heldEnter: [], cancellation: false, retries: false, otherPanels: false };
  const axe = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  const guard = async ctx => ctx.route('**/*', route => {
    if (new URL(route.request().url()).origin === new URL(baseURL).origin) return route.continue();
    report.external.push(route.request().url()); return route.abort();
  });
  await guard(context);
  const page = await context.newPage();
  const watch = target => {
    target.on('pageerror', error => report.errors.push(error.message)); rejectNativeDialogs(target, report.errors);
    target.on('request', request => {
      if (/\/title-requests\/\d+\/action$/.test(request.url())) report.actions.push({ url: request.url(), ...request.postDataJSON() });
    });
  };
  watch(page);
  const dialog = (target = page) => target.locator('#request-dialog');
  const bar = (target = page) => dialog(target).locator('.action-bar');
  const review = (target = page) => dialog(target).locator('.action-choice');
  const opener = (name, target = page) => bar(target).getByRole('button', { name, exact: true });
  const status = (target = page) => target.locator('#request-dialog-status');
  const getDetail = async id => {
    const response = await context.request.get(`${baseURL}/api/asap/staff/title-requests/${id}`);
    assert.equal(response.status(), 200); return response.json();
  };
  const open = async (id, target = page) => {
    await target.goto(`${baseURL}/staff/?scope=2&request=${id}`, { waitUntil: 'networkidle' });
    await dialog(target).waitFor({ state: 'visible' }); await bar(target).waitFor();
  };
  async function panelState(panel, target = page) {
    assert.equal(await bar(target).isVisible(), false, 'Toolbar is hidden during inline confirmation');
    assert.equal(await bar(target).getByRole('button').count(), 0, 'Toolbar commands leave the accessibility tree');
    assert.equal(await bar(target).locator('button').evaluateAll(nodes => nodes.every(n => !n.getClientRects().length)), true);
    assert.equal(await target.locator('#request-dialog-title').isVisible(), true);
    assert.equal(await status(target).isVisible(), true);
    assert.equal(await target.locator('#close-request').isVisible(), true);
    assert.equal(await panel.evaluate(node => node.contains(document.activeElement)), true, 'Focus enters the inline panel');
    assert.equal(await target.evaluate(() => {
      const style = getComputedStyle(document.activeElement);
      return style.outlineStyle !== 'none' && parseFloat(style.outlineWidth) > 0 || style.boxShadow !== 'none';
    }), true, 'Focused control has a visible indicator');
  }
  async function choose(name, target = page) {
    await opener(name, target).press('Enter');
    const panel = review(target); await panel.waitFor();
    await panel.locator('button[type=submit]:not([disabled])').waitFor();
    assert.equal(await panel.locator('button[type=submit]').textContent(), name === 'Purchase' ? 'Confirm purchase' : 'Reject and close');
    assert.equal(await panel.locator(name === 'Purchase' ? '.primary-button' : '.danger-button').count(), 1);
    await panelState(panel, target);
    return panel;
  }
  async function holdOpeningEnter(name, panel = review()) {
    const before = report.actions.length;
    await opener(name).focus(); await page.keyboard.down('Enter');
    await panel.waitFor();
    // Reject and async pickers must remain safe even when their choices become
    // ready before the staff member releases the opening key.
    await panel.locator('button[type=submit]:not([disabled])').waitFor();
    try {
      await page.keyboard.down('Enter'); await page.keyboard.down('Enter');
      assert.equal(await panel.isVisible(), true, 'Held opening Enter retains the inline panel');
      assert.equal(await panel.locator('button[type=submit]').isEnabled(), true, 'Opening repeats never start a mutation');
      assert.equal(report.actions.length, before, 'Held opening Enter posts no action');
      await panelState(panel);
      report.heldEnter.push({ name, postCount: report.actions.length - before });
    } finally { await page.keyboard.up('Enter'); }
    return panel;
  }
  async function cancel(panel, name, escape = false, target = page) {
    if (escape) {
      await target.keyboard.down('Escape'); await target.keyboard.down('Escape'); await target.keyboard.up('Escape');
    }
    else await panel.getByRole('button', { name: 'Cancel', exact: true }).press('Enter');
    await panel.waitFor({ state: 'hidden' });
    assert.equal(await bar(target).isVisible(), true);
    assert.equal(await opener(name, target).evaluate(node => node === document.activeElement), true, 'Cancellation restores opener focus');
  }
  async function scan(name, target = page, nativeZoom = false) {
    const panel = review(target); await panel.scrollIntoViewIfNeeded(); await target.evaluate(axe);
    const state = await target.evaluate(async () => {
      const modal = document.querySelector('#request-dialog'), panel = modal.querySelector('.action-choice');
      return { violations: (await axe.run()).violations.filter(v => ['serious', 'critical'].includes(v.impact)),
        width: innerWidth, documentWidth: document.documentElement.scrollWidth,
        overflow: panel.scrollWidth > panel.clientWidth + 1 || modal.querySelector('.request-middle').scrollWidth > modal.querySelector('.request-middle').clientWidth + 1,
        images: [...document.images].filter(n => n.getClientRects().length).every(n => n.complete && n.naturalWidth > 0),
        cssZoom: getComputedStyle(document.documentElement).zoom, toolbarHidden: modal.querySelector('.action-bar').hidden };
    });
    assert.deepEqual(state.violations, [], `${name}: serious/critical axe findings`);
    assert.ok(state.documentWidth <= state.width && !state.overflow, `${name}: horizontal overflow`);
    assert.equal(state.images, true, `${name}: missing image`); assert.equal(state.toolbarHidden, true);
    if (nativeZoom) {
      const cdp = await target.context().newCDPSession(target);
      try {
        const metrics = await cdp.send('Page.getLayoutMetrics');
        assert.equal(metrics.visualViewport.zoom, 2); assert.equal(state.cssZoom, '1'); state.browserZoom = metrics.visualViewport.zoom;
        const screenshot = await cdp.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
        await fs.writeFile(path.join(artifacts, `${name}.png`), Buffer.from(screenshot.data, 'base64'));
      } finally { await cdp.detach(); }
    } else await target.screenshot({ path: path.join(artifacts, `${name}.png`), fullPage: true });
    report.states.push({ name, ...state });
  }
  try {
    await open(untouchedId); const untouched = await getDetail(untouchedId);
    // The cancellation fixture never authorizes a write. An opening regression
    // fails here before it can reach SQL or enqueue a notification.
    await page.route(`**/staff/title-requests/${untouchedId}/action`, route => route.fulfill({ status: 409,
      json: { code: 'stale_version', message: 'Unexpected action during an opening-only scenario' } }));
    for (const name of ['Purchase', 'Reject']) {
      let panel = await choose(name);
      if (name === 'Purchase') assert.equal(await panel.getByRole('checkbox', { name: 'Send purchase reminder' }).isChecked(), true);
      else {
        assert.equal(await panel.getByLabel('Rejection template').inputValue(), '');
        assert.match(await panel.textContent(), /email is queued only when a template and delivery are available/);
      }
      await scan(`${name.toLowerCase()}-desktop`);
      await page.setViewportSize({ width: 390, height: 844 }); await scan(`${name.toLowerCase()}-mobile`);
      await page.setViewportSize({ width: 1280, height: 900 });
      for (let count = 0; count < 12; count++) {
        await page.keyboard.press('Tab');
        assert.equal(await page.evaluate(() => Boolean(document.activeElement.closest('.action-bar'))), false, 'Tab cannot reach hidden toolbar');
      }
      await cancel(panel, name);
      panel = await holdOpeningEnter(name);
      assert.equal(await review().count(), 1, 'Rapid repeated opening retains one panel');
      await cancel(panel, name, true);
      assert.deepEqual(await getDetail(untouchedId), untouched, 'Opening and cancellation preserve version, state and history');
    }
    assert.equal(report.actions.length, 0);

    // Template failure and an aborted late read both leave cancellation usable.
    const templatePath = `**/staff/title-requests/${untouchedId}/rejection-templates`;
    await page.route(templatePath, route => route.fulfill({ status: 503, json: { message: 'Deterministic template failure' } }));
    await opener('Reject').click();
    await page.locator('#app-status').filter({ hasText: 'Deterministic template failure' }).waitFor();
    assert.equal(await review().locator('button[type=submit]').isDisabled(), true);
    await cancel(review(), 'Reject'); await page.unroute(templatePath);
    let releaseTemplate, templateStarted, finishTemplate;
    const templateEntered = new Promise(resolve => { templateStarted = resolve; }), templateHeld = new Promise(resolve => { releaseTemplate = resolve; });
    const templateFinished = new Promise(resolve => { finishTemplate = resolve; });
    await page.route(templatePath, async route => {
      templateStarted(); await templateHeld;
      assert.match(route.request().failure()?.errorText || '', /ERR_ABORTED/, 'Cancel aborts the template read');
      finishTemplate();
    });
    await opener('Reject').click(); await templateEntered;
    const templateAborted = page.waitForEvent('requestfailed', request => request.url().endsWith('/rejection-templates'));
    await cancel(review(), 'Reject'); await templateAborted;
    const purchasePanel = await choose('Purchase'); releaseTemplate(); await templateFinished;
    await page.unroute(templatePath);
    assert.equal(await review().getByRole('heading').textContent(), 'Purchase suggestion');
    await cancel(purchasePanel, 'Purchase');

    // Closing the request keeps the established dirty-choice guard and never confirms a workflow action.
    let panel = await choose('Purchase');
    await panel.getByRole('checkbox', { name: 'Send purchase reminder' }).uncheck();
    await decideConfirmation(page, () => page.locator('#close-request').click(), decision => decision.dismiss());
    assert.equal(await dialog().isVisible(), true); assert.equal(await bar().isVisible(), false);
    await cancel(panel, 'Purchase', true);
    panel = await choose('Reject'); await page.locator('#close-request').click();
    await dialog().waitFor({ state: 'hidden' });
    assert.deepEqual(await getDetail(untouchedId), untouched); assert.equal(report.actions.length, 0);
    await open(untouchedId);

    // Async assignment options must not introduce a competing panel after Purchase has opened.
    let releaseAssignment, assignmentStarted, finishAssignment;
    const assignmentEntered = new Promise(resolve => { assignmentStarted = resolve; }), assignmentHeld = new Promise(resolve => { releaseAssignment = resolve; });
    const assignmentFinished = new Promise(resolve => { finishAssignment = resolve; });
    await page.route('**/staff/assignment-candidates?*', async route => {
      assignmentStarted(); await assignmentHeld;
      assert.match(route.request().failure()?.errorText || '', /ERR_ABORTED/, 'A new inline action aborts competing options');
      finishAssignment();
    });
    await opener('Assign').click(); await assignmentEntered;
    const assignmentAborted = page.waitForEvent('requestfailed', request => request.url().includes('/assignment-candidates?'));
    panel = await choose('Purchase'); await assignmentAborted; releaseAssignment(); await assignmentFinished;
    assert.equal(await dialog().locator('.inline-form').count(), 0); await cancel(panel, 'Purchase');
    await page.unroute('**/staff/assignment-candidates?*');
    const assignment = await holdOpeningEnter('Assign', dialog().locator('.inline-form'));
    await panelState(assignment); await cancel(assignment, 'Assign', true);
    await opener('Already own').focus(); await page.keyboard.down('Enter');
    const linking = dialog().locator('.bib-link-panel'); await linking.waitFor();
    await page.keyboard.down('Enter'); await page.keyboard.up('Enter');
    assert.equal(await page.locator('#polaris-dialog').isVisible(), false, 'Opening key repeats do not activate the focused catalog search');
    assert.equal(await bar().isVisible(), false);
    assert.equal(await dialog().getByRole('button', { name: 'Search Polaris catalog', exact: true }).evaluate(node => node === document.activeElement), true);
    await page.keyboard.press('Escape'); assert.equal(await bar().isVisible(), true);
    assert.equal(await opener('Already own').evaluate(node => node === document.activeElement), true);
    await opener('Already own').click();
    const notes = dialog().getByLabel('Notes', { exact: true });
    await notes.fill('Local linking draft to revert');
    await page.keyboard.down('Enter'); await page.keyboard.down('Enter'); await page.keyboard.up('Enter');
    assert.equal(await notes.inputValue(), 'Local linking draft to revert\n\n', 'Fresh textarea Enter and its repeats retain normal editing behavior');
    await decideConfirmation(page, () => dialog().getByRole('button', { name: 'Revert changes', exact: true }).click(), decision => decision.accept());
    await bar().waitFor(); assert.equal(await linking.isVisible(), false);
    assert.equal(await dialog().getByLabel('Notes', { exact: true }).inputValue(), untouched.notes);
    assert.deepEqual(await getDetail(untouchedId), untouched); report.cancellation = true;

    const extension = path.join(__dirname, 'native-zoom-extension');
    const zoomContext = await chromium.launchPersistentContext(path.join(artifacts, 'zoom-profile'), {
      channel: 'chromium', headless: true, viewport: null, extraHTTPHeaders: headers,
      args: [`--disable-extensions-except=${extension}`, `--load-extension=${extension}`, '--window-size=1280,900'] });
    try {
      await guard(zoomContext);
      const worker = zoomContext.serviceWorkers()[0] || await zoomContext.waitForEvent('serviceworker');
      const zoomPage = zoomContext.pages()[0] || await zoomContext.newPage(); watch(zoomPage); await open(untouchedId, zoomPage);
      assert.equal(await worker.evaluate(async origin => {
        const tab = (await chrome.tabs.query({})).find(tab => tab.url.startsWith(origin));
        await chrome.tabs.setZoomSettings(tab.id, { mode: 'automatic', scope: 'per-tab' });
        await chrome.tabs.setZoom(tab.id, 2); return chrome.tabs.getZoom(tab.id);
      }, new URL(baseURL).origin), 2);
      for (const name of ['Purchase', 'Reject']) {
        const zoomPanel = await choose(name, zoomPage); await scan(`${name.toLowerCase()}-native-200`, zoomPage, true);
        await cancel(zoomPanel, name, true, zoomPage);
      }
    } finally { await zoomContext.close(); }

    for (const [name, id, expected] of [['Purchase', purchaseId, 'Outstanding purchase'], ['Reject', rejectionId, 'Closed']]) {
      await open(id); const before = await getDetail(id); panel = await choose(name);
      if (name === 'Reject') await panel.getByLabel('Rejection template').selectOption(templateId);
      const actionPath = `**/staff/title-requests/${id}/action`;
      await page.route(actionPath, route => route.fulfill({ status: 422, json: { code: 'invalid_action', message: `Deterministic ${name} failure; retry` } }));
      await panel.locator('button[type=submit]').click();
      await page.locator('#app-status').filter({ hasText: `Deterministic ${name} failure; retry` }).waitFor();
      assert.equal(await panel.locator('button[type=submit]').isEnabled(), true); assert.equal(await bar().isVisible(), false);
      if (name === 'Reject') assert.equal(await panel.getByLabel('Rejection template').inputValue(), templateId);
      else assert.equal(await panel.getByRole('checkbox', { name: 'Send purchase reminder' }).isChecked(), true);
      assert.deepEqual(await getDetail(id), before); await scan(`${name.toLowerCase()}-recoverable-failure`); await page.unroute(actionPath);
      let release, entered;
      const started = new Promise(resolve => { entered = resolve; }), held = new Promise(resolve => { release = resolve; });
      await page.route(actionPath, async route => { entered(); await held; await route.continue(); });
      const response = page.waitForResponse(r => r.request().method() === 'POST' && new URL(r.url()).pathname.endsWith(`/${id}/action`) && r.status() === 200);
      const countBefore = report.actions.length;
      await panel.locator('button[type=submit]').focus();
      await page.keyboard.down('Enter'); await page.keyboard.down('Enter'); await page.keyboard.up('Enter'); await started;
      await page.keyboard.press('Escape'); await page.locator('#close-request').click();
      assert.equal(await dialog().isVisible(), true); assert.equal(await review().isVisible(), true);
      assert.equal(await panel.locator('button[type=submit]').isDisabled(), true); assert.equal(await bar().isVisible(), false);
      assert.equal(report.actions.length, countBefore + 1, 'Repeated confirmation dispatches one action');
      release(); const accepted = await (await response).json(); assert.equal(accepted.committed, true);
      assert.equal(accepted.notificationStatus, 'queued');
      await status().filter({ hasText: expected }).waitFor(); await bar().waitFor(); await page.unroute(actionPath);
      assert.equal(await page.locator('#close-request').evaluate(node => node === document.activeElement), true);
      const saved = await getDetail(id); assert.notEqual(saved.version, before.version);
      assert.equal(saved.activity.filter(item => item.eventType === 'status_changed').length, 1);
    }
    report.retries = true;

    for (const [id, expected] of [[savedBibId, 'Pending hold'], [optoutId, 'Closed']]) {
      await open(id); panel = await holdOpeningEnter('Purchase');
      assert.equal(await panel.getByRole('checkbox').count(), 0);
      assert.match(await panel.textContent(), /purchase reminder does not apply/);
      assert.match(await panel.textContent(), expected === 'Pending hold' ? /Pending hold for automatic hold placement/ : /closes this request without placing a hold/);
      await panel.locator('button[type=submit]').press('Enter'); await status().filter({ hasText: expected }).waitFor(); await bar().waitFor();
      if (id === savedBibId) {
        const before = await getDetail(id);
        const pickup = await holdOpeningEnter('Pickup', dialog().locator('.inline-form')); await panelState(pickup);
        await cancel(pickup, 'Pickup');
        await opener('Pickup').press('Enter'); await pickup.waitFor(); await cancel(pickup, 'Pickup', true);
        assert.deepEqual(await getDetail(id), before, 'Opening/cancelling pickup does not change the request');
      }
    }
    report.otherPanels = true; assert.deepEqual(report.errors, []); assert.deepEqual(report.external, []);
  } catch (error) {
    report.failure = error.stack; await page.screenshot({ path: path.join(artifacts, 'failure.png'), fullPage: true }).catch(() => {}); throw error;
  } finally {
    await fs.writeFile(path.join(artifacts, 'browser-results.json'), JSON.stringify(report, null, 2));
    await context.close(); await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
