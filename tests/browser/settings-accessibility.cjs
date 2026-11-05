'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');
const { decideConfirmation, rejectNativeDialogs } = require('./confirmation.cjs');

const sourceText = 'Prefix selected suffix';
const keys = ['suggestion_submitted', 'purchase_approved', 'already_owned', 'hold_placed', 'rejected'];
const frontend = path.resolve(__dirname, '../../src/Asap.Web/Frontend');

function settingsData(scope) {
  const empty = { exists: false, values: [] };
  const templates = keys.map((templateKey, index) => ({ id: String(index + 1), organizationId: 1,
    templateKey, subject: sourceText, body: sourceText, enabled: true, version: 'template-v1' }));
  const configured = { workflow: { suggestionLimitMessage: sourceText }, patron: { successMessage: sourceText },
    email: { fromAddress: 'fixture@example.org', fromName: 'Fixture' },
    publicationOptions: empty, commonCreators: empty, allowedPatronCodeIds: empty,
    providers: [], formats: [], templates: [], branding: { hasLogo: false, altText: 'Fixture' } };
  return { orgId: scope, version: 'settings-v1', organization: scope === 'system' ? null : { id: 2, name: 'Library Two' },
    stored: { systemSettings: { enabledLibraryOrgIds: [2], libraryOrgIds: [2] }, polaris: {},
      configuredSystem: configured, libraryOverride: scope === 'system' ? null : {},
      origins: [], providers: [], formats: [], customFields: [], formatRules: [], autoClaimRules: [], templates },
    effective: { formats: [], externalSearchProviders: [], customFields: [], logoAltText: 'Fixture' },
    emails: configured.email, templatePlaceholders: ['name', 'title', 'author', 'format', 'barcode', 'firstName', 'lastName'],
    patronCodeChoices: [], autoClaimStaff: [] };
}

async function focusProof(control) {
  return control.evaluate(node => {
    const bounds = node.getBoundingClientRect(), x = (bounds.left + bounds.right) / 2, y = (bounds.top + bounds.bottom) / 2;
    const hit = document.elementFromPoint(x, y);
    const style = getComputedStyle(node), extent = node.matches(':focus-visible')
      ? Math.max(0, parseFloat(style.outlineWidth) + parseFloat(style.outlineOffset)) : 0;
    const fullVerticalRequired = Boolean(node.closest('.settings-staff-roster')) && bounds.height + extent * 2 <= innerHeight;
    let caret = null;
    if (node.matches('input[type="text"], input[type="email"], textarea') && node.selectionEnd !== null) {
      const measure = document.createElement('canvas').getContext('2d');
      measure.font = `${style.fontStyle} ${style.fontWeight} ${style.fontSize} ${style.fontFamily}`;
      const lines = node.value.slice(0, node.selectionEnd).split('\n');
      const contentWidth = node.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight);
      const unwrapped = node.localName === 'input' || lines.every(line => measure.measureText(line).width <= contentWidth);
      if (unwrapped) {
        const left = bounds.left + parseFloat(style.borderLeftWidth) + parseFloat(style.paddingLeft) + measure.measureText(lines.at(-1)).width - node.scrollLeft;
        const lineHeight = parseFloat(style.lineHeight) || parseFloat(style.fontSize) * 1.2;
        const top = bounds.top + parseFloat(style.borderTopWidth) + parseFloat(style.paddingTop) + (lines.length - 1) * lineHeight - node.scrollTop;
        const hit = document.elementFromPoint(left + 1, top + lineHeight / 2);
        caret = { left, top, height: lineHeight, visible: left >= Math.max(0, bounds.left) && left < Math.min(innerWidth, bounds.right) &&
          top >= Math.max(0, bounds.top) && top + lineHeight <= Math.min(innerHeight, bounds.bottom) && (hit === node || node.contains(hit)) };
      }
    }
    return { id: node.id, name: node.getAttribute('aria-label') || node.textContent.trim(),
      active: document.activeElement === node, inViewport: x >= 0 && x < innerWidth && y >= 0 && y < innerHeight,
      unobscured: hit === node || node.contains(hit), x, y,
      focusVisible: node.matches(':focus-visible'), outlineWidth: parseFloat(getComputedStyle(node).outlineWidth),
      bounds: { left: bounds.left, right: bounds.right, top: bounds.top, bottom: bounds.bottom }, outlineExtent: extent,
      fullVerticalRequired, fullVertical: bounds.top - extent >= 0 && bounds.bottom + extent <= innerHeight,
      caret,
      horizontal: node.localName === 'input' ? { scrollLeft: node.scrollLeft, scrollWidth: node.scrollWidth,
        clientWidth: node.clientWidth, roundedMaximum: node.scrollWidth - node.clientWidth } : null,
      contained: bounds.left >= 0 && bounds.right <= innerWidth && document.documentElement.scrollWidth <= innerWidth,
      hit: hit?.id || hit?.className, barPosition: getComputedStyle(document.querySelector('.settings-save-bar')).position };
  });
}

async function clampedHorizontalMaximum(control) {
  // scrollWidth/clientWidth are integer CSS pixels while scrollLeft can be a
  // fractional native pixel at browser zoom. Measure its exact clamp only
  // after the natural caret proof/capture, then restore that observed position.
  return control.evaluate(node => {
    const observed = node.scrollLeft;
    node.scrollLeft = node.scrollWidth;
    const maximum = node.scrollLeft;
    node.scrollLeft = observed;
    return { maximum, roundedMaximum: node.scrollWidth - node.clientWidth, restored: node.scrollLeft };
  });
}

async function pickerScrollReopen(page, captureView) {
  const receipts = [];
  const body = page.locator('#email-purchase-approved-body'), subject = page.locator('#email-purchase-approved-subject');
  const trigger = control => control.locator('xpath=..').locator('.template-placeholder-trigger');
  const snapshot = control => control.evaluate(node => ({ value: node.value, selection: [node.selectionStart, node.selectionEnd],
    page: [scrollX, scrollY], field: [node.scrollTop, node.scrollLeft], active: document.activeElement === node }));
  const frames = () => page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  const open = async control => {
    await trigger(control).click();
    assert.equal(await trigger(control).getAttribute('aria-expanded'), 'true');
  };
  const dismissed = async control => {
    await page.waitForFunction(id => document.querySelector(`[aria-controls="${id}-placeholders"]`).getAttribute('aria-expanded') === 'false',
      await control.getAttribute('id'));
    assert.equal(await trigger(control).evaluate(node => node === document.activeElement), true);
  };
  const insert = async (control, name, expectedField = null) => {
    const id = await control.getAttribute('id');
    await page.locator(`#${id}-placeholders [data-placeholder-key="title"]`).focus();
    const before = await snapshot(control);
    const value = `${before.value.slice(0, before.selection[0])}{{title}}${before.value.slice(before.selection[1])}`;
    await page.keyboard.press('Enter');
    const after = await snapshot(control);
    assert.equal(after.value, value);
    assert.deepEqual(after.selection, [before.selection[0] + 9, before.selection[0] + 9]);
    assert.equal(after.active, true);
    assert.deepEqual(after.page, before.page, `${name}: retain the post-opening page view`);
    assert.deepEqual(after.field, expectedField || before.field, `${name}: retain the current owned field view`);
    // Observe the natural settled result before any other editing action.
    await frames();
    const settled = await snapshot(control);
    assert.deepEqual(settled, after, `${name}: the restored view remains settled without test repair`);
    const screenshot = await captureView(name);
    await page.keyboard.press('Control+Z'); assert.equal(await control.inputValue(), before.value);
    assert.deepEqual(await control.evaluate(node => [node.selectionStart, node.selectionEnd]), before.selection);
    await page.keyboard.press('Control+Shift+Z'); assert.equal(await control.inputValue(), value);
    receipts.push({ name, before, after, settled, screenshot, nativeHistory: true });
  };
  await page.locator('[data-template-key="purchase_approved"] [data-template-toggle]').click();
  await body.fill(Array.from({ length: 50 }, (_, index) => `Line ${index} & é`).join('\n'));
  await body.evaluate(node => node.setSelectionRange(0, 0));
  assert.ok((await snapshot(body)).field[0] > 80, 'The Body starts with a distinct pre-blur scroll position.');
  await open(body);
  const bodyBeforeScroll = await snapshot(body);
  await body.evaluate(node => { node.scrollTop = 80; });
  await dismissed(body);
  assert.equal(await body.inputValue(), bodyBeforeScroll.value, 'Field scrolling never changes the Body draft.');
  await open(body);
  assert.equal((await snapshot(body)).field[0], 80);
  await insert(body, 'body-scroll-reopen');

  const prepareSubject = async () => {
    await subject.fill('Long subject '.repeat(60)); await subject.press('Home');
    await subject.evaluate(node => { node.setSelectionRange(8, 16); node.scrollLeft = 47; });
    assert.equal((await snapshot(subject)).field[1], 47);
    await trigger(subject).focus();
    assert.equal((await snapshot(subject)).field[1], 0, 'The native single-line blur really resets its horizontal view.');
    await open(subject);
  };
  await prepareSubject();
  await subject.evaluate(node => node.dispatchEvent(new Event('scroll')));
  await frames();
  assert.equal(await trigger(subject).getAttribute('aria-expanded'), 'true', 'A queued blur reset still matches the picker anchor.');
  await insert(subject, 'subject-blur-reopen', [0, 47]);

  await prepareSubject();
  const subjectBeforeScroll = await snapshot(subject);
  await subject.evaluate(node => { node.scrollLeft = 100; });
  await dismissed(subject);
  assert.equal(await subject.inputValue(), subjectBeforeScroll.value, 'Field scrolling never changes the Subject draft.');
  await open(subject);
  assert.equal((await snapshot(subject)).field[1], 100);
  await insert(subject, 'subject-field-scroll-reopen');

  await prepareSubject();
  const pageBeforeScroll = await snapshot(subject);
  await page.evaluate(() => scrollBy(0, scrollY > 0 ? -Math.min(60, scrollY) : 60));
  await dismissed(subject);
  assert.notDeepEqual((await snapshot(subject)).page, pageBeforeScroll.page, 'The page really moves before reopening.');
  await open(subject);
  await insert(subject, 'subject-page-scroll-reopen', [0, 47]);
  return receipts;
}

async function nativeHistory(page, { panel, id, trigger, option }, selection) {
  await page.locator(`#settings-nav-${panel}`).click();
  if (panel === 'templates') await page.locator('[data-template-key="suggestion_submitted"] [data-template-toggle]').click();
  const control = page.locator(`#${id}`), launch = page.getByRole('button', { name: trigger, exact: true });
  assert.equal(await control.inputValue(), sourceText, id);
  await control.focus(); await control.press('Home'); await page.keyboard.type('Before ');
  const before = `Before ${sourceText}`, start = 14, end = selection ? 22 : start;
  const token = option.includes('barcode_label') ? '{{barcode_label}}' : option.includes('next_available_date') ? '{{next_available_date}}' : '{{title}}';
  const inserted = `${before.slice(0, start)}${token}${before.slice(end)}`;
  await control.evaluate((node, positions) => node.setSelectionRange(...positions), [start, end]);
  await launch.focus(); await launch.press('Enter');
  const scroll = await control.evaluate(node => [scrollX, scrollY, node.scrollTop, node.scrollLeft]);
  await page.locator(option).press('Enter');
  assert.equal(await control.inputValue(), inserted);
  assert.equal(await control.evaluate(node => node === document.activeElement), true);
  assert.deepEqual(await control.evaluate(node => [scrollX, scrollY, node.scrollTop, node.scrollLeft]), scroll,
    `${id}: insertion preserves exact page and field scroll`);
  const adjacent = ' extra', prefix = inserted.slice(0, start + token.length), suffix = inserted.slice(start + token.length);
  const read = async () => {
    const value = await control.inputValue();
    assert.ok(value.startsWith(prefix) && value.endsWith(suffix), `${id}: native history retains neighboring source: ${value}`);
    const text = value.slice(prefix.length, value.length - suffix.length);
    assert.ok(adjacent.startsWith(text)); return text;
  };
  await page.keyboard.type(adjacent);
  let text = await read();
  for (let step = 0; text; step++) {
    assert.ok(step < adjacent.length); await page.keyboard.press('Control+Z');
    const next = await read(); assert.ok(next.length < text.length); text = next;
  }
  await page.keyboard.press('Control+Z');
  assert.equal(await control.inputValue(), before);
  assert.deepEqual(await control.evaluate(node => [node.selectionStart, node.selectionEnd]), [start, end]);
  // The ordinary typing before the token is a native edit too, and remains
  // independently undoable. No local JavaScript history can substitute for it.
  for (let step = 0; await control.inputValue() !== sourceText; step++) {
    assert.ok(step < 7); await page.keyboard.press('Control+Z');
    const value = await control.inputValue();
    assert.ok(value.endsWith(sourceText) && 'Before '.startsWith(value.slice(0, value.length - sourceText.length)), value);
  }
  assert.equal(await page.locator('#settings-save-title').textContent(), 'No changes');
  assert.equal(await page.locator('#settings-save').isDisabled(), true);
  for (let step = 0; await control.inputValue() !== before; step++) {
    assert.ok(step < 7); await page.keyboard.press('Control+Shift+Z');
  }
  await page.keyboard.press('Control+Shift+Z'); assert.equal(await control.inputValue(), inserted);
  assert.deepEqual(await control.evaluate(node => [node.selectionStart, node.selectionEnd]), [start + token.length, start + token.length]);
  text = await read();
  for (let step = 0; text.length < adjacent.length; step++) {
    assert.ok(step < adjacent.length); await page.keyboard.press('Control+Shift+Z');
    const next = await read(); assert.ok(next.length > text.length); text = next;
  }
  assert.equal(await page.locator('#settings-save-title').textContent(), 'Unsaved changes');
  assert.equal(await page.locator('#settings-save').isEnabled(), true);
  await launch.focus(); await launch.press('Enter'); await page.keyboard.press('Escape');
  assert.equal(await launch.evaluate(node => node === document.activeElement), true);
  await page.evaluate(() => window.accessibilitySettings.discardDraft());
}

// Composite every ancestor's background and group opacity. Reading only the
// text's computed color misses a disabled fieldset dimming its informational text.
async function contrast(page, selector, boundary = false) {
  return page.locator(selector).evaluate((node, boundary) => {
    const rgba = value => {
      const numbers = value.match(/[\d.]+/g).map(Number);
      return [...numbers.slice(0, 3), numbers[3] ?? 1];
    };
    const over = (top, bottom) => {
      const alpha = top[3] + bottom[3] * (1 - top[3]);
      return [...top.slice(0, 3).map((value, index) => alpha ?
        (value * top[3] + bottom[index] * bottom[3] * (1 - top[3])) / alpha : 0), alpha];
    };
    const rendered = (element, color) => {
      for (let current = element; current; current = current.parentElement) {
        const style = getComputedStyle(current);
        color = over(color, rgba(style.backgroundColor));
        color[3] *= Number(style.opacity);
      }
      return over(color, [255, 255, 255, 1]);
    };
    const luminance = color => color.slice(0, 3).map(value => {
      const channel = value / 255; return channel <= .04045 ? channel / 12.92 : ((channel + .055) / 1.055) ** 2.4;
    }).reduce((sum, value, index) => sum + value * [.2126, .7152, .0722][index], 0);
    const ratio = (a, b) => (Math.max(luminance(a), luminance(b)) + .05) / (Math.min(luminance(a), luminance(b)) + .05);
    const style = getComputedStyle(node), foreground = rendered(node, rgba(boundary ? style.borderTopColor : style.color));
    const inside = rendered(node, [0, 0, 0, 0]), outside = rendered(node.parentElement, [0, 0, 0, 0]);
    return { foreground, inside, outside, ratios: [ratio(foreground, inside), ...(boundary ? [ratio(foreground, outside)] : [])],
      ancestorOpacities: [...function* () { for (let current = node; current; current = current.parentElement) yield Number(getComputedStyle(current).opacity); }()] };
  }, boundary);
}

async function runSourceComponents(artifacts) {
  await fs.mkdir(artifacts, { recursive: true });
  const axe = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  const html = (await fs.readFile(path.join(frontend, 'staff/index.html'), 'utf8')).replace(/<script\b[^>]*>[\s\S]*?<\/script>/g, '');
  const states = [];
  for (const configuration of [
    { name: 'desktop', width: 1280, height: 900, zoom: 1 }, { name: 'mobile', width: 390, height: 844, zoom: 1 },
    { name: 'native-200', width: 1280, height: 900, zoom: 2 }, { name: 'native-400', width: 1440, height: 900, zoom: 4 }
  ]) {
    const extension = path.join(__dirname, 'native-zoom-extension');
    const context = await chromium.launchPersistentContext(path.join(artifacts, `${configuration.name}-profile`), {
      channel: 'chromium', headless: true, viewport: configuration.zoom === 1 ? { width: configuration.width, height: configuration.height } : null,
      args: [`--disable-extensions-except=${extension}`, `--load-extension=${extension}`, `--window-size=${configuration.width},${configuration.height}`]
    });
    const errors = [], external = [], writes = [];
    try {
      await context.route('**/*', async route => {
        const url = new URL(route.request().url());
        if (url.origin !== 'http://asap.test') { external.push(url.href); return route.abort(); }
        if (url.pathname === '/api/asap/staff/session') return route.fulfill({ json: { authenticated: true, antiforgeryToken: 'fixture-token' } });
        if (url.pathname === '/api/asap/staff/settings') {
          if (route.request().method() !== 'GET') { writes.push(route.request().postDataJSON()); return route.fulfill({ json: { code: 'saved', data: { orgId: 'system', version: 'settings-v2' } } }); }
          return route.fulfill({ json: settingsData(url.searchParams.get('orgId')) });
        }
        if (url.pathname === '/api/asap/staff/organizations') return route.fulfill({ json: { data: [
          { id: 1, name: 'System', organizationCodeId: 1, parentOrganizationId: null, isActive: true, version: 'org1' },
          { id: 2, name: 'Library Two', organizationCodeId: 2, parentOrganizationId: 1, isActive: true, version: 'org2' }
        ] } });
        if (url.pathname === '/api/asap/staff/polaris/patron-codes') return route.fulfill({ json: { data: [] } });
        if (url.pathname === '/api/asap/staff/users') {
          if (route.request().method() !== 'GET') { writes.push({ staff: route.request().postDataJSON() }); return route.fulfill({ status: 400, json: { message: 'Unexpected staff mutation in the presentation fixture.' } }); }
          return route.fulfill({ json: { canAssignSuperAdmin: true, users: [
          { id: '20', displayName: 'Fixture Staff', userPrincipalName: 'fixture@example.org', notificationEmail: 'fixture@example.org',
            role: 'staff', organizationId: 2, active: true, version: 'user-v1' }
        ] } });
        }
        if (url.pathname === '/staff/') return route.fulfill({ contentType: 'text/html', body: html });
        const file = path.resolve(frontend, `.${decodeURIComponent(url.pathname)}`);
        assert.ok(file.startsWith(frontend + path.sep));
        return route.fulfill({ path: file });
      });
      const worker = context.serviceWorkers()[0] || await context.waitForEvent('serviceworker');
      const page = context.pages()[0] || await context.newPage();
      page.on('pageerror', error => errors.push(error.message)); rejectNativeDialogs(page, errors);
      await page.goto('http://asap.test/staff/', { waitUntil: 'networkidle' });
      const initialRatio = await page.evaluate(() => devicePixelRatio);
      const zoom = await worker.evaluate(async value => {
        const tab = (await chrome.tabs.query({})).find(item => item.url.startsWith('http://asap.test/'));
        await chrome.tabs.setZoomSettings(tab.id, { mode: 'automatic', scope: 'per-tab' });
        await chrome.tabs.setZoom(tab.id, value); return chrome.tabs.getZoom(tab.id);
      }, configuration.zoom);
      assert.ok(Math.abs(zoom - configuration.zoom) < .000001, `Actual browser zoom: ${zoom}`);
      await page.waitForFunction(({ ratio, zoom }) => Math.abs(devicePixelRatio / ratio - zoom) < .01, { ratio: initialRatio, zoom });
      await page.evaluate(async () => {
        document.getElementById('workspace').hidden = false; document.getElementById('queue-view').hidden = true;
        document.getElementById('settings-view').hidden = false;
        const { createSettingsController } = await import('/staff/js/settings.js');
        window.accessibilitySettings = createSettingsController({ root: document.getElementById('settings-view'),
          tab: document.getElementById('settings-view-tab'), announce() {},
          prepareDeparture: async () => {
            if (window.accessibilitySettings.isDirty()) throw new Error('The component scope transition requires a clean editor.');
            return { revalidate: async () => true, commit: () => true };
          } });
        window.accessibilitySettings.bind(); window.accessibilitySettings.setStaff({ id: '1', role: 'super_admin', organizationId: 1 });
        await window.accessibilitySettings.activate();
      });
      assert.equal(await page.evaluate(() => window.accessibilitySettings.isReady()), true,
        await page.locator('#settings-message').textContent());
      const receipt = { ...configuration, physicalWidth: configuration.width, physicalHeight: configuration.height,
        zoom, focus: [], history: [], scrollCases: [], contrasts: [], accessibility: [] };
      const evidence = await context.newCDPSession(page);
      const captureView = async name => {
        const capture = await evidence.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
        const file = `${configuration.name}-${name}.png`;
        await fs.writeFile(path.join(artifacts, file), Buffer.from(capture.data, 'base64'));
        return file;
      };
      const scan = async name => {
        await page.evaluate(axe);
        const result = await page.evaluate(async () => ({ violations: (await axe.run()).violations.filter(item => ['serious', 'critical'].includes(item.impact)),
          overflow: document.documentElement.scrollWidth > innerWidth,
          clippedControls: [...document.querySelectorAll('#settings-form input, #settings-form select, #settings-form textarea, #settings-form button')]
            .filter(node => node.getClientRects().length && !node.closest('[popover]')).filter(node => {
              const bounds = node.getBoundingClientRect(), owner = (node.closest('fieldset, .settings-field, .settings-template-field, .settings-save-bar') || node.parentElement).getBoundingClientRect();
              return bounds.left < -1 || bounds.right > innerWidth + 1 || bounds.left < owner.left - 1 || bounds.right > owner.right + 1;
            }).map(node => node.id || node.getAttribute('aria-label') || node.textContent.trim()) }));
        receipt.accessibility.push({ name, ...result });
        assert.deepEqual(result.violations, [], `${configuration.name}/${name}: axe`);
        assert.equal(result.overflow, false, `${configuration.name}/${name}: page overflow`);
        assert.deepEqual(result.clippedControls, [], `${configuration.name}/${name}: child containment`);
      };
      const prove = async control => {
        // No scroll/focus repair, and this evidence precedes axe.
        await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
        const proof = await focusProof(control); receipt.focus.push(proof);
        const capture = await evidence.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
        proof.screenshot = `${configuration.name}-focus-${receipt.focus.length}.png`;
        await fs.writeFile(path.join(artifacts, proof.screenshot), Buffer.from(capture.data, 'base64'));
        assert.equal(proof.active && proof.inViewport && proof.unobscured && proof.contained && proof.focusVisible && proof.outlineWidth >= 2 &&
          (!proof.fullVerticalRequired || proof.fullVertical),
          true, JSON.stringify(proof));
      };
      const orientation = configuration.width / zoom <= 760 ? 'horizontal' : 'vertical';
      assert.equal(await page.locator('#settings-nav').getAttribute('aria-orientation'), orientation);
      const firstTab = page.locator('#settings-nav-start'); await firstTab.focus(); await page.keyboard.press(orientation === 'vertical' ? 'ArrowDown' : 'ArrowRight');
      assert.equal(await page.locator('#settings-nav-polaris').evaluate(node => node === document.activeElement), true);
      assert.equal(await firstTab.getAttribute('aria-selected'), 'true');
      await page.keyboard.press('Enter'); assert.equal(await page.locator('#settings-nav-polaris').getAttribute('aria-selected'), 'true');
      await firstTab.click();
      if (configuration.name === 'desktop') {
        for (const [width, orientation, display] of [[760, 'horizontal', 'flex'], [1280, 'vertical', 'grid']]) {
          await page.setViewportSize({ width, height: 900 });
          await page.waitForFunction(value => document.getElementById('settings-nav').getAttribute('aria-orientation') === value, orientation);
          assert.equal(await page.locator('#settings-nav').evaluate(node => getComputedStyle(node).display), display);
        }
      }
      for (const name of ['LEAP BIB record URL pattern', 'LEAP patron record URL pattern', 'Nonparticipating-library message', 'Misconfigured system message']) {
        assert.equal(await page.getByRole('textbox', { name, exact: true }).count(), 1, 'Visible field names omit their associated help.');
      }
      for (const selector of ['#system-staff-url', '#leap-bib-url-pattern', '#format-icon-url-pattern']) {
        const result = await contrast(page, selector, true); receipt.contrasts.push({ selector, ...result });
        assert.ok(result.ratios.every(value => value >= 3), `${selector}: ${JSON.stringify(result)}`);
      }
      await page.evaluate(() => { document.getElementById('settings-view').hidden = true; document.getElementById('queue-view').hidden = false; });
      for (const selector of ['#request-search', '#claim-filter', '#tag-filter']) {
        const result = await contrast(page, selector, true); receipt.contrasts.push({ selector, ...result });
        assert.ok(result.ratios.every(value => value >= 3), `${selector}: ${JSON.stringify(result)}`);
      }
      await page.evaluate(() => { document.getElementById('settings-view').hidden = false; document.getElementById('queue-view').hidden = true; });
      for (const field of [
        { panel: 'workflow', id: 'suggestion-limit-message', trigger: 'Insert placeholder into Suggestion limit message', option: '#suggestion-limit-placeholder-picker [data-suggestion-placeholder="next_available_date"]' },
        { panel: 'templates', id: 'email-submit-subject', trigger: 'Insert placeholder into Submission confirmation Subject', option: '#email-submit-subject-placeholders [data-placeholder-key="title"]' },
        { panel: 'templates', id: 'email-submit-body', trigger: 'Insert placeholder into Submission confirmation Body', option: '#email-submit-body-placeholders [data-placeholder-key="title"]' },
        { panel: 'patron', id: 'patron-success-message', trigger: 'Insert placeholder into Success message', option: '#patron-success-message-placeholders [data-patron-placeholder="barcode_label"]' }
      ]) {
        for (const selection of [false, true]) {
          await nativeHistory(page, field, selection); receipt.history.push({ id: field.id, selection });
        }
      }
      await page.locator('#settings-nav-templates').click();
      await page.locator('[data-template-key="suggestion_submitted"] [data-template-toggle]').press('Enter');
      const body = page.locator('#email-submit-body');
      for (const [id, value, offset, scrollTop] of [
        ['email-submit-subject', 'Long subject '.repeat(60), 740, 0],
        ['email-submit-body', Array.from({ length: 60 }, (_, index) => `Line ${index}: selected content`).join('\n'), 900, 180]
      ]) {
        const control = page.locator(`#${id}`);
        await control.fill(value);
        if (id.endsWith('subject')) await control.press('End');
        await control.evaluate((node, saved) => { node.setSelectionRange(saved.offset, saved.offset + 8); node.scrollTop = saved.scrollTop; }, { offset, scrollTop });
        let before, after, horizontal = null;
        if (id.endsWith('body')) {
          const launch = page.getByRole('button', { name: 'Insert placeholder into Submission confirmation Body', exact: true });
          await launch.focus(); await launch.press('Enter');
          before = await control.evaluate(node => [scrollX, scrollY, node.scrollTop, node.scrollLeft]);
          await page.locator(`#${id}-placeholders [data-placeholder-key="title"]`).press('Enter');
          after = await control.evaluate(node => [scrollX, scrollY, node.scrollTop, node.scrollLeft]);
        } else {
          before = await control.evaluate(node => [scrollX, scrollY, node.scrollTop, node.scrollLeft]);
          const beforeGeometry = await focusProof(control);
          assert.equal(beforeGeometry.caret?.visible, true, JSON.stringify(beforeGeometry));
          await page.keyboard.press('Shift+Tab');
          assert.equal(await page.getByRole('button', { name: 'Insert placeholder into Submission confirmation Subject', exact: true })
            .evaluate(node => node === document.activeElement), true);
          await page.keyboard.press('Enter');
          await control.evaluate(node => node.dispatchEvent(new Event('scroll')));
          await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
          assert.equal(await page.locator(`[aria-controls="${id}-placeholders"]`).getAttribute('aria-expanded'), 'true',
            'A queued field scroll at its opening position cannot dismiss the picker.');
          const pageBefore = await page.evaluate(() => [scrollX, scrollY]);
          await page.locator(`#${id}-placeholders [data-placeholder-key="title"]`).press('Enter');
          after = await control.evaluate(node => [scrollX, scrollY, node.scrollTop, node.scrollLeft]);
          const afterGeometry = await focusProof(control);
          assert.equal(afterGeometry.caret?.visible, true, JSON.stringify(afterGeometry));
          assert.equal(afterGeometry.active, true);
          assert.deepEqual(after.slice(0, 2), pageBefore, 'A long Subject picker preserves the post-opening page scroll.');
          const replacementScreenshot = await captureView('subject-replacement');
          const clampAfter = await clampedHorizontalMaximum(control);
          assert.equal(clampAfter.restored, after[3]);
          assert.equal(after[3], Math.min(before[3], clampAfter.maximum), 'Replacement preserves pre-blur scroll with only normal browser clamping.');
          assert.equal(await control.inputValue(), `${value.slice(0, offset)}{{title}}${value.slice(offset + 8)}`);
          await page.keyboard.press('Control+Z'); assert.equal(await control.inputValue(), value);
          assert.deepEqual(await control.evaluate(node => [node.selectionStart, node.selectionEnd]), [offset, offset + 8]);
          await page.keyboard.press('Control+Shift+Z');
          assert.equal(await control.inputValue(), `${value.slice(0, offset)}{{title}}${value.slice(offset + 8)}`);
          assert.equal((await focusProof(control)).caret?.visible, true);
          horizontal = { before: beforeGeometry, after: afterGeometry, pageBefore, clampAfter, replacementScreenshot };
          await control.fill(value); await control.press('End');
          const endBefore = await control.evaluate(node => ({ scrollLeft: node.scrollLeft, max: node.scrollWidth - node.clientWidth }));
          assert.equal((await focusProof(control)).caret?.visible, true);
          await page.keyboard.press('Shift+Tab'); await page.keyboard.press('Enter');
          await control.evaluate(node => node.dispatchEvent(new Event('scroll')));
          await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
          assert.equal(await page.locator(`[aria-controls="${id}-placeholders"]`).getAttribute('aria-expanded'), 'true');
          const endPage = await page.evaluate(() => [scrollX, scrollY]);
          await page.locator(`#${id}-placeholders [data-placeholder-key="title"]`).press('Enter');
          await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
          const endAfter = await control.evaluate(node => ({ scrollLeft: node.scrollLeft, max: node.scrollWidth - node.clientWidth,
            page: [scrollX, scrollY], selection: [node.selectionStart, node.selectionEnd] }));
          assert.equal(await control.inputValue(), `${value}{{title}}`);
          assert.deepEqual(endAfter.page, endPage);
          assert.deepEqual(endAfter.selection, [value.length + 9, value.length + 9]);
          assert.ok(endAfter.max > endBefore.max);
          assert.equal((await focusProof(control)).caret?.visible, true);
          const endScreenshot = await captureView('subject-end-caret');
          const endClamp = await clampedHorizontalMaximum(control);
          assert.equal(endAfter.scrollLeft, endClamp.maximum, 'End insertion makes precisely the additional horizontal room required for its new caret.');
          assert.equal(endClamp.restored, endAfter.scrollLeft);
          await page.keyboard.type('X'); await page.keyboard.press('Control+Z');
          assert.equal(await control.inputValue(), `${value}{{title}}`, 'Undo retains the adjacent token when undoing later typing.');
          await page.keyboard.press('Control+Z'); assert.equal(await control.inputValue(), value);
          await page.keyboard.press('Control+Shift+Z'); assert.equal(await control.inputValue(), `${value}{{title}}`);
          horizontal.endCaret = { before: endBefore, after: endAfter, clamp: endClamp, screenshot: endScreenshot };
          await page.keyboard.press('Shift+Tab'); await page.keyboard.press('Enter');
          await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
          assert.equal(await page.locator(`[aria-controls="${id}-placeholders"]`).getAttribute('aria-expanded'), 'true');
          await control.evaluate(node => { node.scrollLeft = 1; });
          await page.waitForFunction(id => document.querySelector(`[aria-controls="${id}-placeholders"]`).getAttribute('aria-expanded') === 'false', id);
          assert.equal(await page.locator(`[aria-controls="${id}-placeholders"]`).evaluate(node => document.activeElement === node), true,
            'Genuine later editor scrolling dismisses the picker and returns focus to its trigger.');
          horizontal.queuedFieldScrollRetained = true; horizontal.laterFieldScrollDismissed = true;
        }
        assert.ok(id.endsWith('body') ? before[2] > 0 : before[3] > 0, `${id}: the field really is scrolled`);
        if (id.endsWith('body')) assert.deepEqual(after, before, `${id}: preserve nonzero field and page scroll`);
        receipt.scrollCases.push({ id, before, after, horizontal });
      }
      receipt.pickerScrollReopen = await pickerScrollReopen(page, captureView);
      await page.evaluate(() => window.accessibilitySettings.discardDraft());
      await page.locator('[data-template-key="suggestion_submitted"] [data-template-toggle]').press('Enter');
      await page.locator('#email-submit-subject').focus(); await page.keyboard.press('Tab'); await page.keyboard.press('Tab');
      await page.keyboard.type('First dirty Body'); await prove(body); await scan('first-dirty-body');
      await page.evaluate(() => window.accessibilitySettings.discardDraft());
      await page.locator('[data-template-key="suggestion_submitted"] [data-template-toggle]').press('Enter');
      const bodyTrigger = page.getByRole('button', { name: 'Insert placeholder into Submission confirmation Body', exact: true });
      await bodyTrigger.focus(); await bodyTrigger.press('Enter');
      const beforeInsertion = await body.evaluate(node => [scrollX, scrollY, node.scrollTop, node.scrollLeft]);
      await page.locator('#email-submit-body-placeholders [data-placeholder-key="title"]').press('Enter');
      assert.equal(await body.inputValue(), `${sourceText}{{title}}`);
      assert.deepEqual(await body.evaluate(node => [scrollX, scrollY, node.scrollTop, node.scrollLeft]), beforeInsertion);
      await prove(body);
      assert.equal(receipt.focus.at(-1).caret?.visible, true, 'Direct Body insertion retains a visible caret while preserving exact insertion scroll.');
      await scan('direct-body-picker-insertion');
      await page.evaluate(() => window.accessibilitySettings.discardDraft());
      await page.locator('#settings-nav-staff').click();
      const row = page.locator('.settings-staff-row'); await row.waitFor();
      await row.locator('.settings-staff-summary').press('Enter'); await page.keyboard.press('Tab');
      const email = row.locator('input[type="email"]'); await page.keyboard.type('x'); await prove(email);
      await scan('first-dirty-staff-email');
      for (let index = 0; index < 6; index++) { await page.keyboard.press('Tab'); await prove(page.locator(':focus')); }
      await page.keyboard.press('Shift+Tab');
      assert.equal(await row.getByRole('button', { name: 'Cancel', exact: true }).evaluate(node => node === document.activeElement), true);
      await page.keyboard.press('Enter'); await prove(email);
      assert.equal(await email.inputValue(), 'fixture@example.org');
      await page.keyboard.press('Tab');
      const displayName = row.locator('input[type="text"]');
      await page.keyboard.type(' x'); await prove(displayName);
      await row.getByRole('button', { name: 'Cancel', exact: true }).click();
      await page.keyboard.press('Tab'); await page.keyboard.press('Tab');
      const role = row.locator('select').first();
      await page.keyboard.press('ArrowUp');
      assert.equal(await role.inputValue(), 'admin'); await prove(role);
      await row.getByRole('button', { name: 'Cancel', exact: true }).click();
      const rowCancel = row.getByRole('button', { name: 'Cancel', exact: true });
      await email.fill(''); await row.getByRole('button', { name: 'Save changes', exact: true }).click();
      assert.equal(await page.locator('#staff-access-status').textContent(), 'Email address is required.');
      await rowCancel.click(); await prove(email);
      assert.equal(await email.inputValue(), 'fixture@example.org');
      assert.equal(await page.evaluate(() => window.accessibilitySettings.isDirty()), false);
      assert.equal(await page.locator('#staff-access-status').textContent(), '', 'Row Cancel retires its owned required notice.');
      await email.fill(''); await row.getByRole('button', { name: 'Save changes', exact: true }).click();
      await page.keyboard.press('Escape'); await prove(row.locator('.settings-staff-summary'));
      assert.equal(await email.inputValue(), '', 'Row Escape retains its draft.');
      assert.equal(await page.evaluate(() => window.accessibilitySettings.isDirty()), true);
      assert.equal(await page.locator('#staff-access-status').textContent(), '', 'Row Escape retires its owned required notice.');
      await row.locator('.settings-staff-summary').press('Enter'); await rowCancel.click();
      await page.locator('#staff-add-toggle').click(); // Give the edge-click setup page scroll room at desktop height too.
      await email.fill('pointer-draft@example.org');
      await page.evaluate(() => document.getElementById('staff-show-inactive').focus({ preventScroll: true }));
      // Settle the setup focus reveal before arranging the pointer target.
      await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      // Arrange a visible lower edge before pointerdown. Nothing scrolls or
      // refocuses the target between the real pointerdown and pointerup.
      await rowCancel.evaluate(node => scrollBy(0, node.getBoundingClientRect().bottom - innerHeight));
      await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      const pointerBefore = await rowCancel.boundingBox(), pointerHeight = await page.evaluate(() => innerHeight);
      assert.ok(Math.abs(pointerBefore.y + pointerBefore.height - pointerHeight) <= 1, JSON.stringify(pointerBefore));
      const point = { x: pointerBefore.x + pointerBefore.width / 2, y: Math.min(pointerHeight - 1, pointerBefore.y + pointerBefore.height - 1) };
      await page.mouse.move(point.x, point.y); await page.mouse.down();
      const pointerDown = await rowCancel.boundingBox();
      assert.deepEqual(pointerDown, pointerBefore, 'Pointer focus cannot move a roster button before release.');
      await page.mouse.up();
      assert.equal(await email.inputValue(), 'fixture@example.org', 'One real lower-edge pointer click activates Cancel.');
      assert.equal(await page.evaluate(() => window.accessibilitySettings.isDirty()), false);
      receipt.rosterPointer = { before: pointerBefore, down: pointerDown, point, clicked: true };
      await page.locator('#staff-add-toggle').click();
      await row.locator('.settings-staff-summary').press('Enter'); await prove(row.locator('.settings-staff-summary'));
      if (configuration.name === 'native-200') {
        const summary = row.locator('.settings-staff-summary');
        receipt.rosterKeyboardReveal = [];
        for (const clipped of [24, 64]) {
          await page.locator('#staff-roster-counts').click();
          // Set up a genuinely clipped row, then use only native pointer and
          // keyboard input. Do not repair its focus or scroll after activation.
          await summary.evaluate((node, clipped) => scrollBy(0, node.getBoundingClientRect().bottom - innerHeight - clipped), clipped);
          const before = await summary.boundingBox(), height = await page.evaluate(() => innerHeight);
          assert.ok(before.y > 0 && before.y + before.height >= height + clipped - 1, JSON.stringify({ before, height, clipped }));
          const expanded = await summary.getAttribute('aria-expanded');
          await page.mouse.move(before.x + 20, before.y + 12); await page.mouse.down();
          await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
          const down = await summary.boundingBox();
          assert.deepEqual(down, before, 'Pointer focus leaves a clipped summary stationary until release.');
          await page.mouse.up();
          assert.notEqual(await summary.getAttribute('aria-expanded'), expanded, 'One stationary pointer click toggles the summary.');
          const pointer = await focusProof(summary);
          assert.equal(pointer.active, true);
          assert.equal(pointer.focusVisible, false);
          assert.equal(pointer.fullVertical, false, 'The pointer-focused row remains clipped before switching to keyboard.');
          await page.keyboard.press('Enter'); await prove(summary);
          assert.equal(await summary.getAttribute('aria-expanded'), expanded, 'Enter activates the same focused summary.');
          receipt.rosterKeyboardReveal.push({ clipped, before, down, pointer, keyboard: receipt.focus.at(-1) });
        }
      }
      await page.locator('#staff-add-toggle').press('Enter');
      await page.locator('#staff-add-submit').click();
      assert.equal(await page.locator('#staff-access-status').textContent(), 'Email address is required.');
      await page.keyboard.press('Escape'); await prove(page.locator('#staff-add-toggle'));
      assert.equal(await page.locator('#staff-access-status').textContent(), '');
      await page.locator('#staff-add-toggle').press('Enter');
      await page.locator('#staff-add-submit').click();
      await page.locator('#staff-add-email').fill('sam'); await prove(page.locator('#staff-add-email'));
      assert.equal(await page.locator('#staff-access-status').textContent(), '', 'Nonempty partial input retires only its obsolete required notice.');
      const staffWritesBefore = writes.length;
      await page.locator('#staff-add-submit').click();
      assert.equal(await page.locator('#staff-add-email').evaluate(node => node.validity.valid), false);
      assert.equal(writes.length, staffWritesBefore, 'A partial email does not dispatch a mutation.');
      await page.locator('#staff-add-email').fill('corrected@example.org');
      assert.equal(await page.locator('#staff-access-status').textContent(), '');
      await page.keyboard.press('Escape'); await prove(page.locator('#staff-add-toggle'));
      await page.locator('#staff-add-toggle').press('Enter');
      assert.equal(await page.locator('#staff-add-email').inputValue(), 'corrected@example.org', 'Escape preserves the Add draft.');
      await page.locator('#staff-add-email').fill(''); await page.locator('#staff-add-submit').click();
      assert.equal(await page.locator('#staff-access-status').textContent(), 'Email address is required.');
      await page.locator('#staff-add-cancel').click();
      assert.equal(await page.locator('#staff-access-status').textContent(), '');
      await page.locator('#staff-add-toggle').press('Enter');
      assert.equal(await page.locator('#staff-add-email').inputValue(), '', 'Cancel resets the Add draft.');
      await page.keyboard.press('Escape');
      await scan('staff-create-escape');
      await page.evaluate(() => window.accessibilitySettings.discardDraft());
      await page.locator('#settings-nav-workflow').click();
      await page.locator('#suggestion-limit-message').fill('Pointer save draft');
      const pointerClick = async button => {
        await button.scrollIntoViewIfNeeded();
        const before = await button.boundingBox();
        await page.mouse.move(before.x + before.width / 2, before.y + before.height / 2); await page.mouse.down();
        const after = await button.boundingBox(); assert.deepEqual(after, before, 'Focusing a global action cannot move its click target');
        await page.mouse.up();
      };
      await pointerClick(page.locator('#settings-save'));
      await page.waitForFunction(() => document.getElementById('settings-save-title').textContent === 'No changes')
        .catch(async error => { throw new Error(`${error.message}: ${JSON.stringify({ writes, message: await page.locator('#settings-message').textContent(),
          invalid: await page.locator('#settings-form :invalid').evaluateAll(nodes => nodes.map(node => ({ id: node.id, validation: node.validationMessage }))) })}`); });
      assert.equal(writes.length, 1, 'One pointer click dispatches exactly one settings save');
      await page.locator('#suggestion-limit-message').fill('Pointer discard draft');
      await pointerClick(page.locator('#settings-discard'));
      await page.locator('#confirmation-dialog[open]').waitFor(); await page.keyboard.press('Escape');
      await page.locator('#confirmation-dialog').waitFor({ state: 'hidden' });
      assert.equal(await page.locator('#settings-discard').evaluate(node => node === document.activeElement), true);
      assert.equal(await page.locator('#suggestion-limit-message').inputValue(), 'Pointer discard draft');
      await decideConfirmation(page, () => pointerClick(page.locator('#settings-discard')), dialog => dialog.accept());
      await page.waitForFunction(() => document.getElementById('settings-save-title').textContent === 'No changes');
      await page.locator('#settings-scope').selectOption('2');
      await page.waitForFunction(() => window.accessibilitySettings.currentScope() === '2' && window.accessibilitySettings.isReady());
      await firstTab.click();
      assert.equal(await page.locator('#manage-library-participation').isDisabled(), true);
      const summary = await contrast(page, '#organizations-status-message'); receipt.contrasts.push({ selector: '#organizations-status-message', ...summary });
      assert.ok(summary.ratios[0] >= 4.5, JSON.stringify(summary));
      await page.locator('#settings-nav-polaris').click();
      assert.equal(await page.locator('#polaris-host').isDisabled(), true);
      for (const selector of ['#polaris-api-key-status', '#polaris-admin-pass-status']) {
        const result = await contrast(page, selector); receipt.contrasts.push({ selector, ...result });
        assert.ok(result.ratios[0] >= 4.5, `${selector}: ${JSON.stringify(result)}`);
      }
      const session = await context.newCDPSession(page), metrics = await session.send('Page.getLayoutMetrics');
      const capture = await session.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
      await fs.writeFile(path.join(artifacts, `${configuration.name}.png`), Buffer.from(capture.data, 'base64')); await session.detach();
      receipt.cdpZoom = metrics.visualViewport.zoom;
      assert.ok(Math.abs(receipt.cdpZoom - zoom) < .000001, `CDP zoom: ${receipt.cdpZoom}; tab zoom: ${zoom}`);
      await page.evaluate(axe);
      const layout = await page.evaluate(async () => ({ width: innerWidth, height: innerHeight, devicePixelRatio,
        scale: visualViewport.scale, htmlZoom: getComputedStyle(document.documentElement).zoom, bodyZoom: getComputedStyle(document.body).zoom,
        overflow: document.documentElement.scrollWidth > innerWidth,
        violations: (await axe.run()).violations.filter(item => ['serious', 'critical'].includes(item.impact)),
        images: [...document.images].filter(node => node.getClientRects().length).every(node => node.complete && node.naturalWidth > 0),
        browser: navigator.userAgent }));
      Object.assign(receipt, layout); assert.equal(layout.overflow, false); assert.equal(layout.images, true);
      assert.equal(layout.scale, 1); assert.equal(layout.htmlZoom, '1'); assert.equal(layout.bodyZoom, '1'); assert.deepEqual(layout.violations, []);
      assert.deepEqual(errors, []); assert.deepEqual(external, []); states.push(receipt);
      await fs.writeFile(path.join(artifacts, `${configuration.name}.json`), JSON.stringify(receipt, null, 2));
    } catch (error) {
      const page = context.pages()[0];
      const diagnostic = { ...configuration, errors, external, error: error.stack };
      if (page) {
        const session = await context.newCDPSession(page);
        const capture = await session.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
        diagnostic.metrics = await session.send('Page.getLayoutMetrics');
        diagnostic.focus = await page.locator(':focus').count() ? await focusProof(page.locator(':focus')) : null;
        await fs.writeFile(path.join(artifacts, `${configuration.name}-failure.png`), Buffer.from(capture.data, 'base64'));
        await session.detach();
      }
      await fs.writeFile(path.join(artifacts, `${configuration.name}-failure.json`), JSON.stringify(diagnostic, null, 2));
      throw error;
    } finally { await context.close(); }
  }
  return states;
}

module.exports = { runSourceComponents, contrast };
if (require.main === module) runSourceComponents(path.resolve(process.argv[2])).then(states => {
  console.log(JSON.stringify(states.map(state => ({ name: state.name, zoom: state.zoom, cdpZoom: state.cdpZoom,
    width: state.width, height: state.height, focus: state.focus.length, history: state.history.length, violations: state.violations.length }))));
}).catch(error => { console.error(error); process.exitCode = 1; });
