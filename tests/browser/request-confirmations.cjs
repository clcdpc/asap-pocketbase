'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');
const { decideConfirmation, rejectNativeDialogs } = require('./confirmation.cjs');
const [baseURL, artifacts, staffId, tenantId, email, templateId, purchaseId, rejectionId, savedBibId, optoutId, untouchedId, missingBibId] = process.argv.slice(2);

(async () => {
  await fs.mkdir(artifacts, { recursive: true });
  const headers = { 'X-ASAP-Test-Staff-Id': staffId, 'X-ASAP-Test-Tenant-Id': tenantId, 'X-ASAP-Test-Staff-Email': email };
  const browser = await chromium.launch({ headless: true, ...(process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH
    ? { executablePath: process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH } : {}) });
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, extraHTTPHeaders: headers });
  const report = { states: [], errors: [], external: [], actions: [], heldEnter: [], cancellation: false, retries: false, otherPanels: false,
    bibPrerequisite: false, bibPrerequisiteNative200: false, close: {}, pickup: {},
    initialFocus: { chromium: browser.version(), directNavigation: null, reload: null, skipLink: false, keyboardViewChange: false, pointerViewChange: null, modalReturn: null } };
  const axe = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  const guard = async ctx => ctx.route('**/*', route => {
    if (new URL(route.request().url()).origin === new URL(baseURL).origin) return route.continue();
    report.external.push(route.request().url()); return route.abort();
  });
  await guard(context);
  const page = await context.newPage();
  await page.addInitScript(() => {
    const describe = node => ({ tagName: node?.tagName || null, id: node?.id || null });
    window.__focusProbe = { initial: describe(document.activeElement), domContentLoaded: null, focusCalls: [] };
    const nativeFocus = HTMLElement.prototype.focus;
    HTMLElement.prototype.focus = function (...args) {
      window.__focusProbe.focusCalls.push({ target: this.id ? `#${this.id}` : this.tagName.toLowerCase(), stack: new Error().stack });
      return nativeFocus.apply(this, args);
    };
    document.addEventListener('DOMContentLoaded', () => {
      window.__focusProbe.domContentLoaded = describe(document.activeElement);
    }, { once: true });
  });
  const watch = target => {
    target.on('pageerror', error => report.errors.push(error.message)); rejectNativeDialogs(target, report.errors);
    target.on('request', request => {
      if (/\/title-requests\/\d+\/action$/.test(request.url())) report.actions.push({ url: request.url(), ...request.postDataJSON() });
    });
  };
  watch(page);
  const dialog = (target = page) => target.locator('#request-dialog');
  const footerClose = (target = page) => dialog(target).getByRole('button', { name: 'Close', exact: true });
  const bar = (target = page) => dialog(target).locator('.action-bar');
  const review = (target = page) => dialog(target).locator('.action-choice');
  const opener = (name, target = page) => bar(target).getByRole('button', { name, exact: true });
  const pickupCell = (target = page) => dialog(target).locator('.patron-context .detail-grid > div')
    .filter({ has: target.locator('.recorded-pickup-help') });
  const emailCell = (target = page) => dialog(target).locator('.patron-context .detail-grid > div')
    .filter({ has: target.locator('dt', { hasText: /^Email$/ }) });
  const pickupHelp = (target = page) => pickupCell(target).locator('.recorded-pickup-help');
  const pickupHelpToggle = (target = page) => pickupHelp(target).locator(':scope > summary');
  const pickupHelpText = (target = page) => pickupHelp(target).locator('.recorded-pickup-help-content');
  const status = (target = page) => target.locator('#request-dialog-status');
  async function focusEvidence() {
    return page.evaluate(() => {
      const heading = document.getElementById('queue-title'), style = getComputedStyle(heading), probe = window.__focusProbe;
      return { initialActiveElement: probe.initial, domContentLoadedActiveElement: probe.domContentLoaded,
        activeElement: { tagName: document.activeElement?.tagName || null, id: document.activeElement?.id || null },
        headingFocused: document.activeElement === heading, focusVisible: heading.matches(':focus-visible'),
        outline: { style: style.outlineStyle, width: style.outlineWidth, color: style.outlineColor },
        headingFocusCalls: probe.focusCalls.filter(call => call.target === '#queue-title') };
    });
  }
  async function waitForQueue(target = page) {
    await target.locator('#workspace:not([hidden])').waitFor();
    await target.locator('#queue-view:not([hidden])').waitFor();
    await target.locator('#refresh-queue:not([disabled])').waitFor();
    await target.waitForFunction(() => document.querySelector('#request-grid table') ||
      document.querySelector('#queue-empty')?.getClientRects().length > 0);
  }
  async function scanFocusPage(name) {
    await page.evaluate(axe);
    const state = await page.evaluate(async () => ({
      violations: (await axe.run()).violations.filter(item => ['serious', 'critical'].includes(item.impact)),
      width: innerWidth, documentWidth: document.documentElement.scrollWidth,
      images: [...document.images].filter(image => image.getClientRects().length)
        .every(image => image.complete && image.naturalWidth > 0)
    }));
    assert.deepEqual(state.violations, [], `${name}: serious/critical axe findings`);
    assert.ok(state.documentWidth <= state.width, `${name}: horizontal page overflow`);
    assert.equal(state.images, true, `${name}: missing visible image`);
    await page.screenshot({ path: path.join(artifacts, `${name}.png`), fullPage: true });
    report.states.push({ name, ...state });
  }
  async function verifyInitialHeadingFocus() {
    const queueUrl = `${baseURL}/staff/?stage=suggestion&scope=all`;
    await page.goto(queueUrl, { waitUntil: 'networkidle' });
    await waitForQueue();
    const direct = await focusEvidence();
    report.initialFocus.directNavigation = direct;
    assert.equal(direct.headingFocused, false, `Fresh queue navigation unexpectedly focuses Title requests: ${JSON.stringify(direct)}`);
    assert.equal(direct.headingFocusCalls.length, 0, `Fresh queue navigation called focus() on Title requests: ${JSON.stringify(direct)}`);
    assert.equal(direct.focusVisible && direct.outline.style !== 'none' && parseFloat(direct.outline.width) > 0, false,
      `Fresh queue navigation renders an unsolicited Title requests outline: ${JSON.stringify(direct)}`);
    await scanFocusPage('heading-focus-direct-navigation');

    await page.reload({ waitUntil: 'networkidle' });
    await waitForQueue();
    const reload = { beforeReloadActiveElement: direct.activeElement, ...await focusEvidence() };
    report.initialFocus.reload = reload;
    assert.equal(reload.headingFocused, false, `Reload unexpectedly focuses Title requests: ${JSON.stringify(reload)}`);
    assert.equal(reload.headingFocusCalls.length, 0, `Reload called focus() on Title requests: ${JSON.stringify(reload)}`);
    assert.equal(reload.focusVisible && reload.outline.style !== 'none' && parseFloat(reload.outline.width) > 0, false,
      `Reload renders an unsolicited Title requests outline: ${JSON.stringify(reload)}`);
    await scanFocusPage('heading-focus-reload');

    await page.keyboard.press('Tab');
    const skipLink = page.locator('.skip-link');
    assert.equal(await skipLink.evaluate(node => node === document.activeElement), true, 'Tab reaches the skip link first.');
    const skipStyle = await skipLink.evaluate(node => ({ focusVisible: node.matches(':focus-visible'), outlineStyle: getComputedStyle(node).outlineStyle,
      outlineWidth: getComputedStyle(node).outlineWidth, href: node.getAttribute('href') }));
    assert.equal(skipStyle.href, '#main-content');
    assert.equal(skipStyle.focusVisible, true, 'Keyboard focus remains visible on the skip link.');
    assert.ok(skipStyle.outlineStyle !== 'none' && parseFloat(skipStyle.outlineWidth) > 0, 'The skip link has a rendered focus indicator.');
    await page.keyboard.press('Enter');
    await page.waitForFunction(() => location.hash === '#main-content');
    report.initialFocus.skipLink = true;

    await page.goto(queueUrl, { waitUntil: 'networkidle' });
    await waitForQueue();
    for (let index = 0; index < 24 && await page.evaluate(() => document.activeElement?.id !== 'profile-button'); index++) {
      await page.keyboard.press('Tab');
    }
    assert.equal(await page.evaluate(() => document.activeElement?.id), 'profile-button', 'Keyboard navigation reaches Profile.');
    assert.equal(await page.locator('#profile-button').evaluate(node => node.matches(':focus-visible')), true);
    await page.keyboard.press('Enter');
    await page.locator('#profile-view:not([hidden])').waitFor();
    assert.equal(await page.evaluate(() => document.activeElement?.id), 'profile-title', 'Keyboard view changes focus the new heading.');
    const profileFocus = await page.locator('#profile-title').evaluate(node => ({ focusVisible: node.matches(':focus-visible'),
      outlineStyle: getComputedStyle(node).outlineStyle, outlineWidth: getComputedStyle(node).outlineWidth }));
    assert.equal(profileFocus.focusVisible, true, 'The new view heading retains keyboard focus visibility.');
    assert.ok(profileFocus.outlineStyle !== 'none' && parseFloat(profileFocus.outlineWidth) > 0);
    report.initialFocus.keyboardViewChange = true;
    await page.locator('[data-view="queue"]').click();
    await page.locator('#queue-view:not([hidden])').waitFor();
    const pointer = await focusEvidence();
    report.initialFocus.pointerViewChange = pointer;
    assert.equal(pointer.headingFocused, true, 'A deliberate pointer view change still moves focus to the new heading.');
    assert.equal(pointer.focusVisible && pointer.outline.style !== 'none' && parseFloat(pointer.outline.width) > 0, false,
      `Pointer navigation must not leave a misleading heading outline: ${JSON.stringify(pointer)}`);
    await scanFocusPage('heading-focus-pointer-view-change');

    const opener = page.locator(`#request-grid .grid-open[data-request-id="${untouchedId}"]`);
    await opener.waitFor();
    await opener.click();
    await dialog().waitFor({ state: 'visible' });
    assert.equal(await page.evaluate(() => document.activeElement?.id), 'close-request');
    await page.keyboard.press('Escape');
    await dialog().waitFor({ state: 'hidden' });
    await page.waitForFunction(id => document.activeElement?.dataset.requestId === id &&
      document.activeElement?.classList.contains('grid-open'), untouchedId);
    const modalReturn = await page.evaluate(() => ({ id: document.activeElement?.id || null,
      target: document.activeElement?.getAttribute('aria-label'), focusVisible: document.activeElement?.matches(':focus-visible'),
      outlineStyle: getComputedStyle(document.activeElement).outlineStyle, outlineWidth: getComputedStyle(document.activeElement).outlineWidth }));
    assert.equal(modalReturn.focusVisible, true, 'Keyboard dismissal restores visible focus to the request opener.');
    assert.ok(modalReturn.outlineStyle !== 'none' && parseFloat(modalReturn.outlineWidth) > 0);
    report.initialFocus.modalReturn = modalReturn;
    await scanFocusPage('heading-focus-modal-return');
  }
  const getDetail = async id => {
    const response = await context.request.get(`${baseURL}/api/asap/staff/title-requests/${id}`);
    assert.equal(response.status(), 200); return response.json();
  };
  const open = async (id, target = page) => {
    await target.goto(`${baseURL}/staff/?scope=2&request=${id}`, { waitUntil: 'networkidle' });
    await dialog(target).waitFor({ state: 'visible' }); await bar(target).waitFor();
  };
  async function verifyBibPrerequisite(target = page, nativeZoom = false, name = 'request-bib-prerequisite', alreadyOpen = false) {
    if (!alreadyOpen) await open(missingBibId, target);
    const before = await getDetail(missingBibId), actionsBefore = report.actions.length, requests = [];
    assert.equal(before.status, 'outstanding_purchase'); assert.equal(before.autohold, true); assert.equal(before.bibid, null);
    const onRequest = request => requests.push({ method: request.method(), url: request.url() });
    target.on('request', onRequest);
    try {
      const notice = dialog(target).locator('.request-bib-prerequisite');
      await notice.waitFor();
      assert.equal(await notice.getByRole('heading', { name: 'Waiting for a BIB ID', exact: true }).count(), 1);
      assert.equal(await notice.getByText('Add and save a Polaris BIB ID before moving this request to Ready for hold.', { exact: true }).count(), 1);
      assert.equal(await notice.getByRole('link', { name: 'Go to BIB ID', exact: true }).count(), 1);
      assert.deepEqual(await notice.evaluate(node => [node.previousElementSibling.classList.contains('detail-meta'),
        node.nextElementSibling.classList.contains('action-bar')]), [true, true]);
      assert.equal(await opener('Ready for hold', target).isDisabled(), true);
      assert.equal(await opener('Claim', target).count(), 1); assert.equal(await opener('Assign', target).count(), 1);
      const bib = dialog(target).locator(`input#request-${missingBibId}-bib-id`);
      assert.equal(await dialog(target).getByLabel('BIB ID', { exact: true }).count(), 1);
      const descriptions = (await bib.getAttribute('aria-describedby')).split(' ');
      assert.equal(descriptions.length, 1);
      assert.equal(await dialog(target).locator(`[id="${descriptions[0]}"]`).textContent(), 'Required for Ready for hold');
      await dialog(target).getByRole('tab', { name: 'Activity', exact: true }).press('Enter');
      assert.equal(await dialog(target).getByRole('tab', { name: 'Activity', exact: true }).getAttribute('aria-selected'), 'true');
      const link = notice.getByRole('link', { name: 'Go to BIB ID', exact: true });
      await link.press('Enter');
      assert.equal(await dialog(target).getByRole('tab', { name: 'Details', exact: true }).getAttribute('aria-selected'), 'true');
      await target.waitForFunction(id => document.activeElement?.id === id, `request-${missingBibId}-bib-id`);
      assert.equal(await bib.evaluate(node => node === document.activeElement), true, 'Keyboard activation focuses this request\'s BIB field.');
      assert.deepEqual(requests, [], 'The notice link navigates and focuses without an API or Polaris request.');
      await bib.fill('9001');
      assert.equal(await notice.isVisible(), true, 'Unsaved text cannot dismiss the notice.');
      assert.equal(await opener('Ready for hold', target).isDisabled(), true, 'Unsaved text cannot enable Ready for hold.');
      await scanBibPrerequisite(name, target, nativeZoom);
      assert.equal(report.actions.length, actionsBefore, 'The notice link and unsaved entry start no workflow action.');
      assert.deepEqual(await getDetail(missingBibId), before, 'The saved synthetic request remains unchanged.');
      return true;
    } finally { target.off('request', onRequest); }
  }
  async function panelState(panel, target = page) {
    assert.equal(await bar(target).isVisible(), false, 'Toolbar is hidden during inline confirmation');
    assert.equal(await bar(target).getByRole('button').count(), 0, 'Toolbar commands leave the accessibility tree');
    assert.equal(await bar(target).locator('button').evaluateAll(nodes => nodes.every(n => !n.getClientRects().length)), true);
    assert.equal(await target.locator('#request-dialog-title').isVisible(), true);
    assert.equal(await status(target).isVisible(), true);
    assert.equal(await target.locator('#close-request').isVisible(), true);
    assert.equal(await panel.evaluate(node => node.contains(document.activeElement)), true, 'Focus enters the inline panel');
    assert.deepEqual(await panel.evaluate(node => {
      const modal = node.closest('#request-dialog'), bar = modal.querySelector('.action-bar');
      const patron = modal.querySelector('.patron-context'), tabs = modal.querySelector('.request-detail-tabs');
      const follows = (left, right) => Boolean(left.compareDocumentPosition(right) & Node.DOCUMENT_POSITION_FOLLOWING);
      return { workflowSlot: node.closest('.request-workflow-panel') !== null,
        afterActionBar: follows(bar, node), beforePatronSummary: follows(node, patron), beforeTabs: follows(node, tabs) };
    }), { workflowSlot: true, afterActionBar: true, beforePatronSummary: true, beforeTabs: true },
    'The active review sits after the action bar and before the patron summary and tabs.');
    assert.equal(await target.evaluate(() => {
      const style = getComputedStyle(document.activeElement);
      return style.outlineStyle !== 'none' && parseFloat(style.outlineWidth) > 0 || style.boxShadow !== 'none';
    }), true, 'Focused control has a visible indicator');
  }
  async function choose(name, target = page) {
    if (name === 'Purchase') {
      assert.equal(await opener(name, target).locator('.fa-shopping-cart').count(), 1, 'The modal Purchase action keeps its cart icon.');
    }
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
  async function scanBibPrerequisite(name, target = page, nativeZoom = false) {
    await target.evaluate(axe);
    const state = await target.evaluate(async () => {
      const modal = document.querySelector('#request-dialog');
      const notice = modal.querySelector('.request-bib-prerequisite');
      const bib = modal.querySelector('.edit-form input[id$="-bib-id"]');
      const footer = modal.querySelector('.request-save-footer'), pane = modal.querySelector('.request-middle');
      const field = bib.getBoundingClientRect(), paneRect = pane.getBoundingClientRect(), footerRect = footer.getBoundingClientRect();
      const paneTop = paneRect.top + pane.clientTop, paneBottom = paneTop + pane.clientHeight;
      const heading = modal.querySelector('.request-heading-context');
      return { violations: (await axe.run()).violations.filter(v => ['serious', 'critical'].includes(v.impact)),
        width: innerWidth, documentWidth: document.documentElement.scrollWidth,
        modalOverflow: modal.scrollWidth > modal.clientWidth + 1 || pane.scrollWidth > pane.clientWidth + 1 ||
          heading.scrollWidth > heading.clientWidth + 1,
        noticeVisible: notice.getClientRects().length > 0,
        fieldFocused: document.activeElement === bib,
        fieldVisible: field.top >= paneTop - 1 && field.bottom <= paneBottom + 1,
        fieldBounds: { top: field.top, bottom: field.bottom }, paneBounds: { top: paneTop, bottom: paneBottom },
        paneScroll: { top: pane.scrollTop, height: pane.scrollHeight, clientHeight: pane.clientHeight },
        footerVisible: footerRect.top >= 0 && footerRect.bottom <= innerHeight + 1,
        focusVisible: bib.matches(':focus-visible'), cssZoom: getComputedStyle(document.documentElement).zoom };
    });
    if (!nativeZoom) await target.screenshot({ path: path.join(artifacts, `${name}.png`), fullPage: true });
    assert.deepEqual(state.violations, [], `${name}: serious/critical axe findings`);
    assert.ok(state.documentWidth <= state.width && !state.modalOverflow, `${name}: horizontal overflow`);
    assert.equal(state.noticeVisible, true, `${name}: notice remains visible`);
    assert.equal(state.fieldFocused, true, `${name}: the current BIB field retains keyboard focus`);
    assert.equal(state.fieldVisible, true, `${name}: the BIB field is visible within the scrolling pane: ${JSON.stringify(state)}`);
    assert.equal(state.footerVisible, true, `${name}: the save footer remains visible: ${JSON.stringify(state)}`);
    assert.equal(state.focusVisible, true, `${name}: the focused BIB field has keyboard focus styling`);
    if (nativeZoom) {
      const cdp = await target.context().newCDPSession(target);
      try {
        const metrics = await cdp.send('Page.getLayoutMetrics');
        assert.equal(metrics.visualViewport.zoom, 2); assert.equal(state.cssZoom, '1'); state.browserZoom = metrics.visualViewport.zoom;
        const screenshot = await cdp.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
        await fs.writeFile(path.join(artifacts, `${name}.png`), Buffer.from(screenshot.data, 'base64'));
      } finally { await cdp.detach(); }
    }
    report.states.push({ name, ...state });
  }
  async function scanCloseFooter(name, target = page, nativeZoom = false) {
    const close = footerClose(target);
    await close.waitFor(); assert.equal(await close.isVisible(), true); assert.equal(await close.isEnabled(), true);
    await target.evaluate(axe); await close.focus();
    const state = await target.evaluate(async () => {
      const modal = document.querySelector('#request-dialog'), footer = modal.querySelector('.request-save-footer');
      const actions = footer.querySelector('.form-actions'), buttons = [...actions.querySelectorAll('button')];
      return { violations: (await axe.run()).violations.filter(v => ['serious', 'critical'].includes(v.impact)),
        width: innerWidth, documentWidth: document.documentElement.scrollWidth,
        footerOverflow: footer.scrollWidth > footer.clientWidth + 1 || actions.scrollWidth > actions.clientWidth + 1,
        clippedButtons: buttons.some(button => {
          const rect = button.getBoundingClientRect(), bounds = actions.getBoundingClientRect();
          return !button.getClientRects().length || rect.left < bounds.left - 1 || rect.right > bounds.right + 1 ||
            button.scrollWidth > button.clientWidth + 1;
        }),
        focusVisible: document.activeElement.matches(':focus-visible') &&
          getComputedStyle(document.activeElement).outlineStyle !== 'none' && parseFloat(getComputedStyle(document.activeElement).outlineWidth) > 0 };
    });
    assert.deepEqual(state.violations, [], `${name}: serious/critical axe findings`);
    assert.ok(state.documentWidth <= state.width && !state.footerOverflow && !state.clippedButtons, `${name}: clipped footer or horizontal overflow`);
    assert.equal(state.focusVisible, true, `${name}: keyboard focus is visible`);
    if (nativeZoom) {
      const cdp = await target.context().newCDPSession(target);
      try {
        const metrics = await cdp.send('Page.getLayoutMetrics');
        assert.equal(metrics.visualViewport.zoom, 2); state.browserZoom = metrics.visualViewport.zoom;
        const screenshot = await cdp.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
        await fs.writeFile(path.join(artifacts, `${name}.png`), Buffer.from(screenshot.data, 'base64'));
      } finally { await cdp.detach(); }
    } else await target.screenshot({ path: path.join(artifacts, `${name}.png`), fullPage: true });
    report.states.push({ name, ...state });
  }
  async function scanPickupHelp(name, target = page, nativeZoom = false, focusRequired = false) {
    const toggle = pickupHelpToggle(target), content = pickupHelpText(target);
    assert.equal(await pickupHelp(target).evaluate(node => node.open), true, `${name}: the disclosure is open`);
    assert.equal(await content.isVisible(), true, `${name}: the explanation is visible`);
    await target.evaluate(axe);
    const state = await target.evaluate(async () => {
      const modal = document.querySelector('#request-dialog'), summary = modal.querySelector('.recorded-pickup-help > summary');
      const content = modal.querySelector('.recorded-pickup-help-content'), pickupCell = content.closest('dd');
      const pickup = pickupCell.getBoundingClientRect();
      const bounds = content.getBoundingClientRect();
      return { violations: (await axe.run()).violations.filter(v => ['serious', 'critical'].includes(v.impact)),
        width: innerWidth, documentWidth: document.documentElement.scrollWidth,
        overflow: modal.scrollWidth > modal.clientWidth + 1 || content.scrollWidth > content.clientWidth + 1,
        clipped: bounds.left < pickup.left - 1 || bounds.right > pickup.right + 1,
        focusVisible: document.activeElement === summary && summary.matches(':focus-visible') &&
          getComputedStyle(summary).outlineStyle !== 'none' && parseFloat(getComputedStyle(summary).outlineWidth) > 0,
        shortExplanation: pickupCell.querySelector('.recorded-pickup-summary')?.textContent.trim(),
        fullExplanation: content.textContent.trim() };
    });
    assert.deepEqual(state.violations, [], `${name}: serious/critical axe findings`);
    assert.ok(state.documentWidth <= state.width && !state.overflow && !state.clipped, `${name}: pickup help overflows or is clipped`);
    assert.equal(state.shortExplanation, 'Historical; holds use live Polaris pickup preference.');
    assert.match(state.fullExplanation, /historical snapshot.*unfilled request.*live Polaris pickup preference.*Updating pickup changes the patron's account default.*existing holds keep their recorded destination/i);
    if (focusRequired) assert.equal(state.focusVisible, true, `${name}: keyboard focus is visible`);
    if (nativeZoom) {
      const cdp = await target.context().newCDPSession(target);
      try {
        const metrics = await cdp.send('Page.getLayoutMetrics');
        assert.equal(metrics.visualViewport.zoom, 2); state.browserZoom = metrics.visualViewport.zoom;
        const screenshot = await cdp.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
        await fs.writeFile(path.join(artifacts, `${name}.png`), Buffer.from(screenshot.data, 'base64'));
      } finally { await cdp.detach(); }
    } else await target.screenshot({ path: path.join(artifacts, `${name}.png`), fullPage: true });
    report.states.push({ name, ...state });
  }
  async function verifyPickupHelp(name, target = page, { boundaries = false, nativeZoom = false } = {}) {
    const toggle = pickupHelpToggle(target), disclosure = pickupHelp(target), content = pickupHelpText(target);
    await toggle.waitFor({ state: 'visible' });
    assert.equal(await toggle.getAttribute('title'), null, `${name}: the help does not rely on a native title tooltip`);
    assert.equal(await toggle.getAttribute('aria-label'), 'Pickup details', `${name}: the help has an accessible name`);
    assert.equal(await pickupCell(target).locator('.recorded-pickup-summary').textContent(), 'Historical; holds use live Polaris pickup preference.');
    assert.equal(await disclosure.evaluate(node => node.open), false, `${name}: help starts closed`);
    if (boundaries) {
      assert.equal(await dialog(target).locator('.patron-context .detail-grid').getAttribute('title'), null);
      const patron = dialog(target).locator('.patron-context .detail-grid > div')
        .filter({ has: target.locator('dt', { hasText: /^Patron$/ }) });
      const barcode = dialog(target).locator('.patron-context .detail-grid > div')
        .filter({ has: target.locator('dt', { hasText: /^Barcode$/ }) });
      const email = dialog(target).locator('.patron-context .detail-grid > div')
        .filter({ has: target.locator('dt', { hasText: /^Email$/ }) });
      for (const [label, trigger] of [
        ['patron name', patron.locator('.patron-name-actions')], ['patron action', patron.locator('.grid-new-suggestion')],
        ['barcode', barcode.locator('dd')], ['email', email.locator('dd')],
        ['pickup value', pickupCell(target).locator('.recorded-pickup-summary')], ['help hover', toggle]
      ]) {
        await trigger.hover();
        assert.equal(await content.isVisible(), false, `${name}: hovering ${label} does not open pickup help`);
      }
      const row = await dialog(target).locator('.patron-context').boundingBox();
      await target.mouse.move(row.x + row.width - 3, row.y + row.height - 2);
      assert.equal(await content.isVisible(), false, `${name}: unused patron-row space does not open pickup help`);
    }
    for (let tab = 0; tab < 36 && !await toggle.evaluate(node => node === document.activeElement); tab++) {
      await target.keyboard.press('Tab');
    }
    assert.equal(await toggle.evaluate(node => node === document.activeElement), true, `${name}: Tab reaches pickup help`);
    await target.keyboard.press('Enter');
    await content.waitFor({ state: 'visible' });
    await scanPickupHelp(name, target, nativeZoom, true);
    await target.keyboard.press('Escape');
    assert.equal(await disclosure.evaluate(node => node.open), false, `${name}: Escape dismisses pickup help`);
    assert.equal(await dialog(target).isVisible(), true, `${name}: Escape leaves the request modal open`);
    assert.equal(await toggle.evaluate(node => node === document.activeElement), true, `${name}: Escape restores help focus`);
    await toggle.press('Space'); await content.waitFor({ state: 'visible' });
    await toggle.press('Space');
    assert.equal(await disclosure.evaluate(node => node.open), false, `${name}: Space opens and dismisses pickup help`);
    report.pickup[name] = true;
  }
  async function verifyEmailLink(name, target, recipient, expectedHref) {
    const cell = emailCell(target), link = cell.getByRole('link', { name: recipient, exact: true });
    assert.equal(await cell.locator('dd').textContent(), recipient, `${name}: the displayed recipient is unchanged`);
    assert.equal(await link.getAttribute('href'), expectedHref, `${name}: the recipient separator and encoded characters follow mailto URI syntax`);
    await target.setViewportSize({ width: 390, height: 844 });
    for (let tab = 0; tab < 36 && !await link.evaluate(node => node === document.activeElement); tab++) {
      await target.keyboard.press('Tab');
    }
    assert.equal(await link.evaluate(node => node === document.activeElement), true, `${name}: Tab reaches the email link`);
    const layout = await link.evaluate(node => {
      const bounds = node.closest('dd').getBoundingClientRect();
      const rects = [...node.getClientRects()].map(rect => ({ left: rect.left, right: rect.right }));
      const style = getComputedStyle(node);
      return { width: innerWidth, documentWidth: document.documentElement.scrollWidth,
        wraps: rects.length > 1, fitsCell: rects.every(rect => rect.left >= bounds.left - 1 && rect.right <= bounds.right + 1),
        focusVisible: node.matches(':focus-visible') && style.outlineStyle !== 'none' && parseFloat(style.outlineWidth) > 0 };
    });
    assert.ok(layout.documentWidth <= layout.width && layout.wraps && layout.fitsCell, `${name}: the long recipient wraps without horizontal overflow`);
    assert.equal(layout.focusVisible, true, `${name}: keyboard focus remains visible`);
    await target.evaluate(() => {
      window.__mailtoActivation = null;
      document.addEventListener('click', event => {
        const anchor = event.target instanceof Element ? event.target.closest('a[href^="mailto:"]') : null;
        if (!anchor) return;
        event.preventDefault(); window.__mailtoActivation = { href: anchor.getAttribute('href'), trusted: event.isTrusted };
      }, { capture: true });
    });
    const actionsBefore = report.actions.length, urlBefore = target.url();
    await target.keyboard.press('Enter');
    assert.deepEqual(await target.evaluate(() => window.__mailtoActivation), {
      href: expectedHref, trusted: true
    }, `${name}: keyboard activation reaches a normal mailto link without launching a handler`);
    assert.equal(report.actions.length, actionsBefore, `${name}: activating email posts no request action`);
    assert.equal(target.url(), urlBefore, `${name}: intercepting mailto leaves the request modal in place`);
    report.email = { [name]: true };
  }
  async function verifyTouchPickupHelp() {
    const touchContext = await browser.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 1,
      isMobile: true, hasTouch: true, extraHTTPHeaders: headers });
    try {
      await guard(touchContext);
      const touchPage = await touchContext.newPage(); watch(touchPage); await open(untouchedId, touchPage);
      const toggle = pickupHelpToggle(touchPage), disclosure = pickupHelp(touchPage), content = pickupHelpText(touchPage);
      await toggle.tap(); await content.waitFor({ state: 'visible' });
      await scanPickupHelp('request-pickup-touch', touchPage);
      assert.equal(await disclosure.evaluate(node => node.open), true);
      await toggle.tap();
      assert.equal(await disclosure.evaluate(node => node.open), false, 'A second touch dismisses pickup help.');
      assert.equal(await dialog(touchPage).isVisible(), true, 'Touch dismissal leaves the request modal open.');
      report.pickup.touch = true;
    } finally { await touchContext.close(); }
  }
  async function verifyPickupHelpDuringInlineAction() {
    const actionsBefore = report.actions.length;
    const panel = await choose('Purchase'), toggle = pickupHelpToggle(), content = pickupHelpText(), disclosure = pickupHelp();
    await toggle.press('Enter'); await content.waitFor({ state: 'visible' });
    assert.equal(await panel.isVisible(), true, 'Pickup help can open while a workflow panel is active.');
    await toggle.evaluate(node => {
      node.escapeEvents = [];
      for (const type of ['keydown', 'keyup']) node.addEventListener(type, event => {
        if (event.key === 'Escape') node.escapeEvents.push({ type: event.type, repeat: event.repeat, trusted: event.isTrusted });
      }, { capture: true });
    });
    await page.keyboard.down('Escape');
    assert.equal(await disclosure.evaluate(node => node.open), false, 'The initial Escape dismisses pickup help.');
    await page.keyboard.down('Escape'); await page.keyboard.up('Escape');
    assert.deepEqual(await toggle.evaluate(node => node.escapeEvents), [
      { type: 'keydown', repeat: false, trusted: true },
      { type: 'keydown', repeat: true, trusted: true },
      { type: 'keyup', repeat: false, trusted: true }
    ], 'The regression uses a trusted repeated Escape held through keyup.');
    assert.equal(await panel.isVisible(), true, 'A repeated held Escape does not cancel the workflow panel.');
    assert.equal(await dialog().isVisible(), true, 'A repeated held Escape does not close the request modal.');
    assert.equal(await toggle.evaluate(node => node === document.activeElement), true, 'Help dismissal retains focus.');
    await page.keyboard.press('Escape');
    await panel.waitFor({ state: 'hidden' });
    assert.equal(await dialog().isVisible(), true, 'A fresh Escape keeps the request modal open.');
    assert.equal(await bar().isVisible(), true, 'A fresh Escape still cancels the inline workflow panel.');
    assert.equal(await opener('Purchase').evaluate(node => node === document.activeElement), true, 'Workflow cancellation restores opener focus.');
    assert.equal(report.actions.length, actionsBefore, 'Opening and dismissing pickup help causes no action.');
    report.pickup.escapeWithInlineAction = true;
  }
  async function verifyUnavailableClose(name, response, expectedMessage) {
    await open(untouchedId);
    const before = await getDetail(untouchedId), actionCount = report.actions.length;
    const endpoint = `**/api/asap/staff/title-requests/${untouchedId}**`;
    let actionCalls = 0, failedRefreshReads = 0;
    await page.route(endpoint, async route => {
      const request = route.request(), url = new URL(request.url());
      if (request.method() === 'POST' && url.pathname.endsWith('/action')) {
        actionCalls++;
        await route.fulfill({ status: response.status, contentType: 'application/json', body: JSON.stringify(response.body) });
      } else if (request.method() === 'GET' && url.pathname.endsWith(`/${untouchedId}`) && actionCalls) {
        failedRefreshReads++;
        await route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ message: 'Deterministic detail refresh failure' }) });
      } else {
        await route.continue();
      }
    });
    try {
      const panel = await choose('Purchase');
      await panel.locator('button[type=submit]').click();
      await dialog().locator('.request-save-footer').waitFor();
      await dialog().getByText(expectedMessage).waitFor();
      assert.equal(actionCalls, 1, `${name}: one deliberately intercepted action`);
      assert.equal(failedRefreshReads, 1, `${name}: the authoritative refresh failed`);
      assert.deepEqual(await footerClose().isVisible(), true, `${name}: recovery retains footer Close`);
      for (let tab = 0; tab < 5 && !await footerClose().evaluate(node => node === document.activeElement); tab++) {
        await page.keyboard.press('Tab');
      }
      assert.equal(await footerClose().evaluate(node => node === document.activeElement), true,
        `${name}: Tab reaches recovery Close`);
      await scanCloseFooter(`${name}-close-fallback`);
      await footerClose().press('Enter');
      await dialog().waitFor({ state: 'hidden' });
      await page.waitForFunction(id => [...document.querySelectorAll('#request-grid .grid-open')]
        .find(node => node.dataset.requestId === id) === document.activeElement, untouchedId);
      assert.deepEqual(await getDetail(untouchedId), before, `${name}: Close preserves the request after the failed refresh`);
      assert.equal(report.actions.length, actionCount + 1, `${name}: Close starts no additional action`);
      report.close[name] = true;
    } finally {
      await page.unroute(endpoint);
    }
  }
  async function verifyUnsavedClose(name, trigger) {
    await open(untouchedId);
    const before = await getDetail(untouchedId), actionsBefore = report.actions.length;
    const notes = dialog().getByLabel('Notes', { exact: true }), draft = `${before.notes || ''}\nUnsaved ${name} close draft`;
    await notes.fill(draft);
    await decideConfirmation(page, trigger, async decision => {
      assert.match(decision.message(), /Discard unsaved request changes and navigate away/i);
      await decision.dismiss();
    });
    assert.equal(await dialog().isVisible(), true, `${name}: Keep editing cancels dismissal`);
    assert.equal(await notes.inputValue(), draft, `${name}: Keep editing retains the draft`);
    assert.deepEqual(await getDetail(untouchedId), before, `${name}: a local draft is not saved by Close`);
    await decideConfirmation(page, trigger, decision => decision.accept());
    await dialog().waitFor({ state: 'hidden' });
    assert.deepEqual(await getDetail(untouchedId), before, `${name}: explicit discard does not mutate the request`);
    assert.equal(report.actions.length, actionsBefore, `${name}: dismissal starts no request action`);
    report.close[name] = true;
  }
  try {
    await verifyInitialHeadingFocus();
    await verifyBibPrerequisite(page, false, 'request-bib-prerequisite-desktop');
    await page.setViewportSize({ width: 390, height: 844 });
    await verifyBibPrerequisite(page, false, 'request-bib-prerequisite-mobile');
    await page.setViewportSize({ width: 1280, height: 900 });
    report.bibPrerequisite = true;
    await open(untouchedId); const untouched = await getDetail(untouchedId);
    assert.equal(await emailCell().locator('dd').textContent(), 'Not recorded', 'Missing email keeps its current presentation.');
    assert.equal(await emailCell().locator('a[href^="mailto:"]').count(), 0, 'Missing email does not create an empty mailto link.');
    const displayedEmail = `${'a'.repeat(55)}+x&y@example.org`;
    const expectedMobileMailto = `mailto:${'a'.repeat(55)}%2Bx%26y@example.org`;
    await open(purchaseId); await verifyEmailLink('request-email-mobile', page, displayedEmail, expectedMobileMailto);
    await page.setViewportSize({ width: 1280, height: 900 }); await open(rejectionId);
    assert.equal(await emailCell().locator('dd').textContent(), 'synthetic@example.org');
    assert.equal(await emailCell().locator('a').getAttribute('href'), 'mailto:synthetic@example.org', 'A standard address retains its structural @ separator.');
    await page.setViewportSize({ width: 1280, height: 900 }); await open(untouchedId);
    // The cancellation fixture never authorizes a write. An opening regression
    // fails here before it can reach SQL or enqueue a notification.
    await page.route(`**/staff/title-requests/${untouchedId}/action`, route => route.fulfill({ status: 409,
      json: { code: 'stale_version', message: 'Unexpected action during an opening-only scenario' } }));
    const rowOpener = page.locator(`#request-grid .request-title-link[data-request-id="${untouchedId}"]`);
    await verifyPickupHelp('request-pickup-desktop', page, { boundaries: true });
    await page.setViewportSize({ width: 390, height: 844 });
    await verifyPickupHelp('request-pickup-mobile');
    await page.setViewportSize({ width: 1280, height: 900 });
    await verifyTouchPickupHelp();
    await verifyPickupHelpDuringInlineAction();
    await footerClose().waitFor();
    assert.equal(await footerClose().isEnabled(), true, 'Clean requests expose an enabled footer Close button.');
    await scanCloseFooter('request-close-desktop');
    await page.setViewportSize({ width: 390, height: 844 });
    await scanCloseFooter('request-close-mobile');
    await page.setViewportSize({ width: 1280, height: 900 });
    await footerClose().press('Enter');
    await dialog().waitFor({ state: 'hidden' });
    await page.waitForFunction(id => [...document.querySelectorAll('#request-grid .grid-open')]
      .find(node => node.dataset.requestId === id) === document.activeElement, untouchedId);
    assert.equal(await rowOpener.evaluate(node => node === document.activeElement), true);
    assert.deepEqual(await getDetail(untouchedId), untouched, 'Clean footer Close returns focus and preserves the request.');
    report.close.cleanKeyboard = true; report.close.focusReturn = true;
    await verifyUnavailableClose('failedRefresh', {
      status: 409, body: { code: 'stale_version', message: 'Deterministic stale request for recovery coverage' }
    }, 'Current details could not refresh');
    await verifyUnavailableClose('committedRefreshFailure', {
      status: 200, body: { committed: true, request: null, refreshUnavailable: true, finalStatus: 'suggestion' }
    }, 'The action committed. Reload this request to review current details.');
    await verifyUnsavedClose('footer', () => footerClose().click());
    await verifyUnsavedClose('x', () => page.locator('#close-request').click());
    await verifyUnsavedClose('escape', async () => {
      await dialog().getByLabel('Notes', { exact: true }).focus(); await page.keyboard.press('Escape');
    });
    await open(untouchedId);
    const openCancellationActionCount = report.actions.length;
    await opener('Close silently').press('Enter');
    const silentReview = review(); await silentReview.waitFor();
    await panelState(silentReview);
    assert.match(await silentReview.getByRole('heading').textContent(), /Close silently/);
    assert.match(await silentReview.textContent(), /without a rejection email/);
    assert.equal(report.actions.length, openCancellationActionCount, 'Opening Close silently remains pending review only.');
    await cancel(silentReview, 'Close silently');
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
    assert.equal(report.actions.length, openCancellationActionCount, 'Opening and cancelling choices posts no additional actions');

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
    assert.deepEqual(await getDetail(untouchedId), untouched);
    assert.equal(report.actions.length, openCancellationActionCount, 'Closing inline panels posts no additional actions');
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

    // An automatic-hold edit invalidates the pending branch review until the
    // existing Save action completes and authoritative request details return.
    await open(savedBibId);
    let holdReview = await choose('Purchase');
    let holdPreference = dialog().getByLabel('Automatically place hold', { exact: true });
    await holdPreference.uncheck();
    assert.equal(await holdReview.locator('button[type=submit]').isDisabled(), true);
    assert.equal(await holdReview.getByRole('alert').textContent(), 'Save the automatic-hold change before continuing.');
    const reviewActionCount = report.actions.length;
    await holdReview.locator('button[type=submit]').press('Enter');
    assert.equal(report.actions.length, reviewActionCount, 'An invalidated branch review posts no workflow action.');
    const middle = dialog().locator('.request-middle');
    const deepScroll = await middle.evaluate(node => {
      const maximum = node.scrollHeight - node.clientHeight;
      node.scrollTop = Math.round(maximum * 0.8);
      const pane = node.getBoundingClientRect();
      const paneTop = pane.top + node.clientTop;
      const paneBottom = paneTop + node.clientHeight;
      const focus = document.querySelector('.action-choice button[type="submit"]').getBoundingClientRect();
      return { top: node.scrollTop, maximum,
        focusIntersectsPane: focus.bottom > paneTop && focus.top < paneBottom };
    });
    assert.ok(deepScroll.maximum > 0 && deepScroll.top > deepScroll.maximum * 0.5 && deepScroll.top < deepScroll.maximum,
      `The refresh starts from a deep mid-scroll position: ${JSON.stringify(deepScroll)}`);
    assert.equal(deepScroll.focusIntersectsPane, false,
      'The restored review target starts outside the deep-scrolled pane.');
    const preservedScroll = deepScroll.top;
    const preferenceOff = page.waitForResponse(response => response.request().method() === 'POST' &&
      new URL(response.url()).pathname.endsWith(`/title-requests/${savedBibId}/action`) && response.status() === 200);
    await decideConfirmation(page, () => dialog().locator('.edit-form').evaluate(form => {
      form.requestSubmit(document.querySelector('.request-save-footer button[type="submit"]'));
    }), decision => {
      assert.match(decision.message(), /Save with automatic hold off/);
      return decision.accept();
    });
    await preferenceOff;
    await page.waitForFunction(() => document.querySelector('.action-choice')?.textContent.includes('A matching BIB exists and the patron does not want a hold'));
    holdReview = review(); await panelState(holdReview);
    assert.equal(await holdReview.getByRole('heading', { name: 'Purchase an additional copy', exact: true }).count(), 1);
    assert.equal(await holdReview.getByRole('checkbox', { name: 'Email me a purchase reminder', exact: true }).isChecked(), true);
    assert.equal(await holdReview.getByRole('button', { name: 'Create purchasing task', exact: true }).count(), 1);
    const restoredReviewViewport = await middle.evaluate(node => {
      const focused = document.activeElement;
      const focusBounds = focused.getBoundingClientRect();
      const pane = node.getBoundingClientRect();
      const paneTop = pane.top + node.clientTop;
      const paneBottom = paneTop + node.clientHeight;
      const modal = node.closest('#request-dialog');
      const panel = modal.querySelector('.action-choice');
      const style = getComputedStyle(focused);
      return { submitFocused: focused === panel.querySelector('button[type="submit"]'),
        focusIntersectsPane: focusBounds.bottom > paneTop && focusBounds.top < paneBottom,
        focusFullyVisible: focusBounds.top >= paneTop - 1 && focusBounds.bottom <= paneBottom + 1,
        focusRect: { top: focusBounds.top, bottom: focusBounds.bottom },
        paneViewport: { top: paneTop, bottom: paneBottom },
        focusClearance: (parseFloat(style.outlineWidth) || 0) + (parseFloat(style.outlineOffset) || 0),
        overflow: panel.scrollWidth > panel.clientWidth + 1 || node.scrollWidth > node.clientWidth + 1 ||
          modal.scrollWidth > modal.clientWidth + 1,
        documentOverflow: document.documentElement.scrollWidth > document.documentElement.clientWidth,
        top: node.scrollTop };
    });
    report.automaticHoldReviewRestore = { before: deepScroll, after: restoredReviewViewport };
    assert.equal(restoredReviewViewport.submitFocused, true, 'The review submit control regains focus after save and refresh.');
    assert.equal(restoredReviewViewport.focusIntersectsPane, true,
      'The restored review focus intersects the request middle scroll viewport.');
    assert.equal(restoredReviewViewport.focusFullyVisible, true,
      `The restored review control is fully visible in the request middle scroll viewport: ${JSON.stringify(restoredReviewViewport)}`);
    assert.equal(restoredReviewViewport.overflow, false, 'The restored review introduces no modal horizontal overflow.');
    assert.equal(restoredReviewViewport.documentOverflow, false, 'The restored review introduces no document horizontal overflow.');
    assert.ok(Math.abs(restoredReviewViewport.top - preservedScroll) < 400,
      `The refresh preserves as much of the deep scroll context as possible: ${JSON.stringify(restoredReviewViewport)}`);
    assert.match(await page.locator('#app-status').textContent(), /Automatic holds are now off/);
    holdPreference = dialog().getByLabel('Automatically place hold', { exact: true });
    await holdPreference.check();
    assert.equal(await holdReview.locator('button[type=submit]').isDisabled(), true);
    assert.equal(await holdReview.getByRole('alert').textContent(), 'Save the automatic-hold change before continuing.');
    const preferenceOn = page.waitForResponse(response => response.request().method() === 'POST' &&
      new URL(response.url()).pathname.endsWith(`/title-requests/${savedBibId}/action`) && response.status() === 200);
    await dialog().locator('.edit-form').evaluate(form => {
      form.requestSubmit(document.querySelector('.request-save-footer button[type="submit"]'));
    });
    await preferenceOn;
    await page.waitForFunction(() => document.querySelector('.action-choice')?.textContent.includes('Pending hold for automatic hold placement'));
    holdReview = review(); await panelState(holdReview);
    assert.match(await page.locator('#app-status').textContent(), /Automatic holds are now on/);
    const refreshedSavedBib = await getDetail(savedBibId);
    assert.equal(refreshedSavedBib.autohold, true, 'Both saved preference directions return to the original fixture state.');
    await cancel(holdReview, 'Purchase');

    const extension = path.join(__dirname, 'native-zoom-extension');
    const zoomContext = await chromium.launchPersistentContext(path.join(artifacts, 'zoom-profile'), {
      channel: 'chromium', headless: true, viewport: null, extraHTTPHeaders: headers,
      args: [`--disable-extensions-except=${extension}`, `--load-extension=${extension}`, '--window-size=1280,900'] });
    try {
      await guard(zoomContext);
      const worker = zoomContext.serviceWorkers()[0] || await zoomContext.waitForEvent('serviceworker');
      const zoomPage = zoomContext.pages()[0] || await zoomContext.newPage(); watch(zoomPage); await open(missingBibId, zoomPage);
      const bibZoomRatio = await zoomPage.evaluate(() => devicePixelRatio);
      assert.equal(await worker.evaluate(async origin => {
        const tab = (await chrome.tabs.query({})).find(tab => tab.url.startsWith(origin));
        await chrome.tabs.setZoomSettings(tab.id, { mode: 'automatic', scope: 'per-tab' });
        await chrome.tabs.setZoom(tab.id, 2); return chrome.tabs.getZoom(tab.id);
      }, new URL(baseURL).origin), 2);
      await zoomPage.waitForFunction(ratio => Math.abs(devicePixelRatio / ratio - 2) < 0.01, bibZoomRatio);
      await verifyBibPrerequisite(zoomPage, true, 'request-bib-prerequisite-native-200', true);
      report.bibPrerequisiteNative200 = true;
      await open(untouchedId, zoomPage);
      const zoomRatio = await zoomPage.evaluate(() => devicePixelRatio);
      assert.equal(await worker.evaluate(async origin => {
        const tab = (await chrome.tabs.query({})).find(tab => tab.url.startsWith(origin));
        await chrome.tabs.setZoomSettings(tab.id, { mode: 'automatic', scope: 'per-tab' });
        await chrome.tabs.setZoom(tab.id, 2); return chrome.tabs.getZoom(tab.id);
      }, new URL(baseURL).origin), 2);
      await zoomPage.waitForFunction(ratio => Math.abs(devicePixelRatio / ratio - 2) < 0.01, zoomRatio);
      await scanCloseFooter('request-close-native-200', zoomPage, true);
      await footerClose(zoomPage).press('Enter'); await dialog(zoomPage).waitFor({ state: 'hidden' });
      await zoomPage.waitForFunction(id => [...document.querySelectorAll('#request-grid .grid-open')]
        .find(node => node.dataset.requestId === id) === document.activeElement, untouchedId);
      report.close.native200 = true;
      await zoomPage.locator(`#request-grid .request-title-link[data-request-id="${untouchedId}"]`).click();
      await dialog(zoomPage).waitFor({ state: 'visible' }); await bar(zoomPage).waitFor();
      await verifyPickupHelp('request-pickup-native-200', zoomPage, { nativeZoom: true });
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
      assert.equal(await footerClose().isEnabled(), true, 'The footer Close remains enabled while the action guard is active.');
      await footerClose().click();
      assert.equal(await dialog().isVisible(), true); assert.equal(await review().isVisible(), true);
      assert.equal(await panel.locator('button[type=submit]').isDisabled(), true); assert.equal(await bar().isVisible(), false);
      assert.equal(report.actions.length, countBefore + 1, 'Repeated confirmation dispatches one action');
      report.close.pending = true;
      release(); const accepted = await (await response).json(); assert.equal(accepted.committed, true);
      assert.equal(accepted.notificationStatus, 'queued');
      await status().filter({ hasText: expected }).waitFor(); await bar().waitFor(); await page.unroute(actionPath);
      assert.equal(await page.locator('#close-request').evaluate(node => node === document.activeElement), true);
      const saved = await getDetail(id); assert.notEqual(saved.version, before.version);
      assert.equal(saved.activity.filter(item => item.eventType === 'status_changed').length, 1);
    }
    report.retries = true;

    for (const [id, expected] of [[savedBibId, 'Pending hold'], [optoutId, 'Closed']]) {
      await open(id);
      if (id === savedBibId) {
        panel = await holdOpeningEnter('Purchase');
        assert.equal(await panel.getByRole('checkbox').count(), 0);
        assert.match(await panel.textContent(), /purchase reminder does not apply/);
        assert.match(await panel.textContent(), /Pending hold for automatic hold placement/);
        await panel.locator('button[type=submit]').press('Enter');
      } else {
        const sourceBeforePurchaseTask = await getDetail(id);
        await opener('Purchase').press('Enter');
        panel = review(); await panel.waitFor(); await panelState(panel);
        assert.equal(await panel.getByRole('heading', { name: 'Purchase an additional copy', exact: true }).count(), 1);
        assert.match(await panel.textContent(), /A matching BIB exists and the patron does not want a hold\./);
        const reminder = panel.getByRole('checkbox', { name: 'Email me a purchase reminder', exact: true });
        assert.equal(await reminder.isChecked(), true, 'The saved staff reminder default populates the Purchase task choice.');
        await scan('purchase-task-confirmation');
        const actionsBefore = report.actions.length;
        const acceptedResponse = page.waitForResponse(response => response.request().method() === 'POST' &&
          new URL(response.url()).pathname.endsWith(`/title-requests/${optoutId}/action`) && response.status() === 200);
        await panel.getByRole('button', { name: 'Create purchasing task', exact: true }).press('Enter');
        const accepted = await (await acceptedResponse).json();
        assert.equal(accepted.committed, true);
        assert.equal(accepted.status, 'closed');
        assert.equal(accepted.finalStatus, 'closed');
        assert.equal(accepted.notificationStatus, 'queued');
        assert.match(accepted.additionalCopyRequestId, /^[1-9]\d{0,18}$/);
        assert.match(accepted.operationId, /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i);
        assert.equal(report.actions.length, actionsBefore + 1);
        assert.deepEqual(Object.keys(report.actions.at(-1)).filter(key => key !== 'url').sort(),
          ['action', 'emailPurchaseReminder', 'operationId', 'requestType', 'version'].sort());
        assert.equal(report.actions.at(-1).action, 'purchase');
        assert.equal(report.actions.at(-1).requestType, 'title_request');
        assert.equal(report.actions.at(-1).emailPurchaseReminder, true);
        await status().filter({ hasText: 'Closed' }).waitFor(); await bar().waitFor();
        await dialog().locator('.related-additional-copy a').waitFor();
        assert.equal(await dialog().locator('.related-additional-copy a').getAttribute('href'),
          `/staff/?request=${accepted.additionalCopyRequestId}&stage=additional_copies`);
        assert.equal(await dialog().locator('.related-additional-copy a').textContent(), `Task ${accepted.additionalCopyRequestId}`);
        const savedTask = await getDetail(optoutId);
        assert.equal(savedTask.additionalCopyRequestId, accepted.additionalCopyRequestId);
        assert.equal(savedTask.closeReason, 'purchased_no_hold');
        assert.equal(savedTask.title, sourceBeforePurchaseTask.title,
          'Purchase task creation preserves the source request title.');
        assert.equal(savedTask.identifier, sourceBeforePurchaseTask.identifier,
          'Purchase task creation preserves the source request identifier.');
        const patronActions = await context.request.get(`${baseURL}/api/asap/staff/title-requests/${optoutId}`);
        assert.equal((await patronActions.json()).activity.some(item => item.eventType === 'patron_action_email'), false);
        const recoveryLink = dialog().locator('.related-additional-copy a');
        await recoveryLink.press('Enter');
        await page.waitForURL(url => url.searchParams.get('request') === accepted.additionalCopyRequestId &&
          url.searchParams.get('stage') === 'additional_copies');
        await dialog().waitFor({ state: 'visible' });
        assert.equal(await page.locator('#request-dialog-title').textContent(), 'Catalog title 9001');
        report.purchaseTask = { operationId: accepted.operationId, taskId: accepted.additionalCopyRequestId, linkOpened: true };
        await open(id);
        assert.match(await status().textContent(), /Closed.*Purchased, no hold/);
      }
      await status().filter({ hasText: expected }).waitFor(); await bar().waitFor();
      if (expected === 'Closed') {
        const closed = await getDetail(id), actionCount = report.actions.length;
        assert.equal(await footerClose().isVisible(), true, 'Already closed requests retain the footer Close button.');
        assert.equal(await footerClose().isEnabled(), true);
        await footerClose().click(); await dialog().waitFor({ state: 'hidden' });
        assert.deepEqual(await getDetail(id), closed, 'Closing an already closed request does not mutate it.');
        assert.equal(report.actions.length, actionCount);
        report.close.alreadyClosed = true;
      }
      if (id === savedBibId) {
        const before = await getDetail(id);
        const pickup = await holdOpeningEnter('Pickup', dialog().locator('.inline-form')); await panelState(pickup);
        await cancel(pickup, 'Pickup');
        await opener('Pickup').press('Enter'); await pickup.waitFor(); await cancel(pickup, 'Pickup', true);
        assert.deepEqual(await getDetail(id), before, 'Opening/cancelling pickup does not change the request');
      }
    }
    report.otherPanels = true; assert.deepEqual(report.errors, []); assert.deepEqual(report.external, []);

    await page.goto(`${baseURL}/staff/?scope=2&stage=suggestion`, { waitUntil: 'networkidle' });
    const purchaseRow = page.locator(`#request-grid tr:has([data-request-id="${untouchedId}"])`);
    await purchaseRow.locator('.request-row-actions[data-ready]').waitFor();
    assert.equal(await page.locator('#library-scope').inputValue(), '2', 'The queue remains scoped to library 2.');
    const sourceBeforeMore = await getDetail(untouchedId);
    assert.equal(sourceBeforeMore.libraryOrgId, 2, 'The row request belongs to the selected library scope.');
    const rowPurchase = purchaseRow.locator('[data-request-action="purchase"]');
    assert.equal(await rowPurchase.count(), 1, 'The row exposes exactly one Purchase command.');
    assert.equal(await purchaseRow.locator('.request-row-actions > [data-request-action="purchase"]').count(), 0,
      'Purchase is menu-only and is never duplicated as a direct row action.');
    const more = page.getByRole('button', { name: `More actions for request ${untouchedId}`, exact: true });
    await more.waitFor(); await more.press('Enter');
    const menuPurchase = purchaseRow.locator('.request-table-menu:popover-open [data-request-action="purchase"]');
    await menuPurchase.waitFor();
    assert.equal((await menuPurchase.textContent()).trim(), 'Purchase');
    assert.equal(await rowPurchase.count(), 1, 'Opening More retains one Purchase command.');
    assert.equal(await menuPurchase.locator('.fa-shopping-cart').count(), 1, 'The table More menu Purchase action keeps its cart icon.');
    const actionCountBeforeMore = report.actions.length;
    await menuPurchase.press('Enter');
    await dialog().waitFor({ state: 'visible' }); await review().waitFor();
    const openedURL = new URL(page.url());
    assert.equal(openedURL.searchParams.get('scope'), '2', 'More-menu Purchase opens the request in the selected scope.');
    assert.equal(openedURL.searchParams.get('request'), untouchedId);
    assert.match(await page.locator('#request-dialog-kicker').textContent(), new RegExp(`Request ${untouchedId}`));
    await panelState(review());
    assert.equal((await getDetail(untouchedId)).libraryOrgId, 2, 'The reviewed request remains in library 2.');
    assert.equal(report.actions.length, actionCountBeforeMore, 'More-menu Purchase opens its pending review without dispatching.');
    await cancel(review(), 'Purchase');
    await page.keyboard.press('Escape'); await dialog().waitFor({ state: 'hidden' });
    assert.equal(report.actions.length, actionCountBeforeMore, 'Canceling the menu review dispatches no Purchase action.');
    assert.deepEqual(await getDetail(untouchedId), sourceBeforeMore, 'Canceling leaves the scoped source request unchanged.');
    await page.waitForFunction(id => {
      const opener = [...document.querySelectorAll('#request-grid .grid-open')].find(button => {
        const label = button.getAttribute('aria-label') || '';
        return label === `Open request ${id}` || label.startsWith(`Open request ${id}: `);
      });
      return opener && document.activeElement === opener && opener.matches(':focus-visible');
    }, untouchedId);
    report.moreMenuPurchase = true;
  } catch (error) {
    report.failure = error.stack; await page.screenshot({ path: path.join(artifacts, 'failure.png'), fullPage: true }).catch(() => {}); throw error;
  } finally {
    await fs.writeFile(path.join(artifacts, 'browser-results.json'), JSON.stringify(report, null, 2));
    await context.close(); await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
