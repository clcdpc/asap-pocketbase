'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((done, fail) => { resolve = done; reject = fail; });
  return { promise, resolve, reject };
}

async function atBarrier(promise, name) {
  let timeout;
  try {
    return await Promise.race([promise, new Promise((_, reject) => {
      timeout = setTimeout(() => reject(new Error(`Timed out at ${name}`)), 15000);
    })]);
  } finally {
    clearTimeout(timeout);
  }
}

async function main() {
  const [baseURL, artifactDirectory, library, staffId, tenantId, email, claimantId] = process.argv.slice(2);
  assert.ok(claimantId, 'Usage: functional-acceptance.cjs baseURL artifacts library staff tenant email claimant');
  const origin = new URL(baseURL).origin;
  assert.equal(origin, baseURL);
  const artifacts = path.resolve(artifactDirectory);
  await fs.mkdir(artifacts, { recursive: true });
  const browser = await chromium.launch({ headless: true,
    ...(process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH
      ? { executablePath: process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH } : {}) });
  const axeSource = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  const report = { externalRequests: 0, pageErrors: [], states: [], posts: [] };
  const releases = [];
  const contexts = [];
  const suggestionPath = '/api/asap/patron/suggestions';
  const settingsPath = '/api/asap/staff/settings';
  const isPost = (response, pathname) => response.request().method() === 'POST' &&
    new URL(response.url()).pathname === pathname;

  async function newContext(headers = {}) {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, extraHTTPHeaders: headers });
    contexts.push(context);
    await context.route('**/*', route => {
      if (new URL(route.request().url()).origin === origin) return route.continue();
      report.externalRequests += 1;
      return route.abort('blockedbyclient');
    });
    context.on('page', page => page.on('pageerror', error => report.pageErrors.push(error.message)));
    return context;
  }

  async function scan(page, name) {
    await page.evaluate(axeSource);
    const violations = await page.evaluate(async () => (await axe.run()).violations
      .filter(item => ['serious', 'critical'].includes(item.impact)));
    const layout = await page.evaluate(() => ({ width: innerWidth, scrollWidth: document.documentElement.scrollWidth,
      imagesLoaded: [...document.images].filter(image => {
        const bounds = image.getBoundingClientRect();
        return bounds.width > 0 && bounds.height > 0;
      }).every(image => image.complete && image.naturalWidth > 0) }));
    await page.screenshot({ path: path.join(artifacts, `${name}.png`), fullPage: true });
    report.states.push({ name, violations, layout });
    assert.deepEqual(violations, [], name);
    assert.ok(layout.scrollWidth <= layout.width, `Horizontal overflow: ${name}`);
    assert.equal(layout.imagesLoaded, true, `Missing image: ${name}`);
  }

  async function openSettings(page) {
    await page.goto(`${origin}/staff/?stage=settings&settingsScope=${library}`, { waitUntil: 'networkidle' });
    await page.locator('#settings-form').waitFor({ state: 'visible' });
    assert.equal(await page.locator('#settings-scope').inputValue(), library);
  }

  async function saveSettings(page, status = 200) {
    const responsePromise = page.waitForResponse(response => isPost(response, settingsPath));
    await page.locator('#settings-save').click();
    const response = await responsePromise;
    const body = await response.json();
    assert.equal(response.status(), status, JSON.stringify(body));
    if (status === 200) {
      await page.locator('#settings-message').filter({ hasText: /^Settings saved\./ }).waitFor();
    } else {
      await page.locator('#settings-message.error').waitFor();
      if (status === 400) await page.locator('#settings-save:not([disabled])').waitFor();
    }
    return body;
  }

  async function settingsSnapshot(page) {
    return page.evaluate(async library => {
      const response = await fetch(`/api/asap/staff/settings?orgId=${library}`);
      if (!response.ok) throw new Error(`Settings GET ${response.status}`);
      return response.json();
    }, library);
  }

  async function rejectInvalidSettings(page) {
    let request = null;
    const observe = candidate => {
      if (candidate.method() === 'POST' && new URL(candidate.url()).pathname === settingsPath) request = candidate;
    };
    page.on('request', observe);
    try {
      await page.locator('#settings-save').click();
      await page.locator('#settings-message.error').waitFor();
      assert.match(await page.locator('#settings-message').textContent(), /required|select|option/i);
      if (request) {
        const response = await request.response();
        assert.equal(response.status(), 400, await response.text());
        report.invalidSettingsBoundary = 'HTTP';
      } else {
        report.invalidSettingsBoundary = 'editor validation before HTTP';
      }
    } finally {
      page.off('request', observe);
    }
  }

  async function overrideScalar(page, selector, value) {
    const input = page.locator(selector);
    const field = input.locator('xpath=..');
    const override = field.locator('.settings-override-toggle');
    if (await override.count()) await override.check();
    await input.fill(value);
  }

  async function login(page, number) {
    await page.goto(`${origin}/patron/?libraryOrgId=${library}`, { waitUntil: 'networkidle' });
    if (await page.locator('#step-form:not(.hidden)').count()) {
      await page.locator('#step-form .btn-logout').click();
    }
    await page.locator('#barcode').fill(`2000000038077${number}`);
    await page.locator('#pin').fill('1234');
    await page.locator('#pin').press('Enter');
    await page.locator('#step-form:not(.hidden)').waitFor();
    assert.equal((await page.locator('#display-barcode').textContent()).trim(), `2000000038077${number}`);
  }

  async function fill(page, title, choice = 'alpha') {
    await page.locator('#format').selectOption('book');
    await page.locator('#title').fill(title);
    await page.locator('#author').fill('Acceptance author');
    await page.locator('#isbn').fill('');
    await page.locator('#custom-field-recommendation_note').fill('Acceptance detail');
    await page.locator('#custom-field-required_choice').selectOption(choice);
    if (await page.locator('#autohold').isChecked()) await page.locator('label[for="autohold"]').click();
    assert.equal(await page.locator('#autohold').isChecked(), false);
  }

  async function submit(page, status) {
    const responsePromise = page.waitForResponse(response => isPost(response, suggestionPath));
    await page.locator('#submit-btn').click();
    const response = await responsePromise;
    const body = await response.json();
    report.posts.push({ status: response.status(), title: response.request().postDataJSON().title,
      id: body.id || null, code: body.code || null });
    assert.equal(response.status(), status, JSON.stringify(body));
    if (status === 201) {
      await page.locator('#step-success:not(.hidden)').waitFor();
      assert.equal(await page.evaluate(() => document.activeElement.id), 'success-title');
    } else if (status !== 401) {
      await page.locator('#submit-error:not(.hidden)').waitFor();
      assert.equal(await page.locator('#submit-btn').isEnabled(), true);
      assert.equal(await page.evaluate(() => document.activeElement.id), 'submit-error');
      assert.equal(await page.locator('#step-conflict:not(.hidden)').count(), 0);
    }
    return body;
  }

  try {
    const staffContext = await newContext({ 'X-ASAP-Test-Staff-Id': staffId,
      'X-ASAP-Test-Tenant-Id': tenantId, 'X-ASAP-Test-Staff-Email': email });
    const admin = await staffContext.newPage();
    await openSettings(admin);
    await admin.locator('#settings-nav-workflow').click();
    await admin.locator('#allow-patron-autohold-opt-out').check();
    await admin.locator('#settings-nav-patron').click();
    await admin.locator('#format-rules-editor [data-rule-code="book"] [data-format-field="publication"]').selectOption('optional');
    await admin.locator('#publication-options-use-system').uncheck();
    const publications = admin.locator('#publication-options-editor [data-domain-row]');
    // Keep inherited choices and add one library choice through the real editor.
    await admin.locator('#add-publication-option').click();
    await publications.last().locator('input[type="text"]').nth(0).fill('acceptance_publication');
    await publications.last().locator('input[type="text"]').nth(1).fill('Acceptance publication');
    for (const [key, label, type] of [['recommendation_note', 'Recommendation detail', 'text'],
      ['required_choice', 'Required choice', 'select'], ['optional_choice', 'Optional choice', 'select']]) {
      await admin.locator('#add-custom-field').click();
      const row = admin.locator('#additional-fields-editor [data-domain-row]').last();
      await row.locator('input[type="text"]').nth(0).fill(key);
      await row.locator('input[type="text"]').nth(1).fill(label);
      await row.locator('select').selectOption(type);
      if (type === 'select') {
        for (const [optionKey, optionLabel, enabled] of [['alpha', 'Alpha display', true],
          ['beta', 'Beta display', true], ['retired', 'Retired display', false]]) {
          const editor = row.locator('[data-options-editor]');
          await editor.getByRole('button', { name: /Add option/i }).click();
          const option = editor.locator('[data-option-row]').last();
          await option.locator('input[type="text"]').nth(0).fill(optionKey);
          await option.locator('input[type="text"]').nth(1).fill(optionLabel);
          await option.locator('input[type="checkbox"]').setChecked(enabled);
        }
      }
      const rule = admin.locator(`#format-rules-editor [data-rule-code="book"] [data-custom-rule-key="${key}"]`);
      await rule.locator('[data-custom-rule-property="mode"]').selectOption(key === 'optional_choice' ? 'optional' : 'required');
      if (key === 'recommendation_note') {
        await rule.locator('[data-custom-rule-property="labelOverride"]').fill('Book recommendation detail');
      }
    }
    await admin.locator('#add-auto-claim-rule').click();
    const claim = admin.locator('#format-claim-rules-editor [data-domain-row]').last();
    const snapshot = await settingsSnapshot(admin);
    await claim.locator('select').nth(0).selectOption(snapshot.effective.formats.find(item => item.code === 'book').id);
    await claim.locator('select').nth(1).selectOption(claimantId);
    await saveSettings(admin);
    await openSettings(admin);
    const configured = await settingsSnapshot(admin);

    // Concurrent real editor snapshots: the stale loser may not erase the winner.
    const concurrent = await staffContext.newPage();
    await openSettings(concurrent);
    await concurrent.locator('#settings-nav-patron').click();
    await overrideScalar(concurrent, '#patron-page-title', 'Acceptance concurrent winner');
    await saveSettings(concurrent);
    await admin.locator('#settings-nav-patron').click();
    await overrideScalar(admin, '#patron-page-title', 'Acceptance stale loser');
    await saveSettings(admin, 409);
    assert.equal((await settingsSnapshot(admin)).effective.pageTitle, 'Acceptance concurrent winner');
    report.settingsStaleWinnerPreserved = true;
    await openSettings(admin);
    await admin.locator('#settings-nav-patron').click();
    const beforeInvalid = await settingsSnapshot(admin);
    const fieldRows = admin.locator('#additional-fields-editor [data-domain-row]');
    let requiredRow;
    for (const row of await fieldRows.all()) {
      if (await row.locator('input[type="text"]').first().inputValue() === 'required_choice') requiredRow = row;
    }
    assert.ok(requiredRow, 'The real Settings editor must contain the configured required select.');
    const requiredOptions = await requiredRow.locator('[data-option-row] input[type="checkbox"]').all();
    assert.equal(requiredOptions.length, 3);
    for (const checkbox of requiredOptions) {
      await checkbox.uncheck();
    }
    await rejectInvalidSettings(admin);
    assert.deepEqual(await settingsSnapshot(admin), beforeInvalid);
    report.settingsRejectionUnchanged = true;
    await scan(admin, 'settings-invalid-save');
    await openSettings(admin);

    const patronContext = await newContext();
    const page = await patronContext.newPage();
    await login(page, 1);
    await page.locator('#format').selectOption('dvd');
    assert.equal(await page.locator('.custom-field-input').count(), 0, 'Hidden rules must remove fields from submission.');
    await fill(page, 'Acceptance committed');
    assert.equal(await page.locator('#custom-field-optional_choice').inputValue(), '', 'Optional select must initially be blank.');
    await page.locator('#custom-field-optional_choice').selectOption('beta');
    await page.locator('#custom-field-optional_choice').selectOption('');
    await page.locator('#publication').selectOption({ label: 'Acceptance publication' });
    await page.locator('#publication').selectOption('');
    assert.equal(await page.locator('#custom-field-required_choice option[value="retired"]').count(), 0);
    report.optionalChoicesCleared = true;
    // Native validation blocks missing values; whitespace then reaches real server validation.
    await page.locator('#title').fill('');
    await page.locator('#submit-btn').click();
    assert.equal(await page.locator('#title').evaluate(element => element.validity.valueMissing), true);
    await page.locator('#title').fill('Acceptance committed');
    await page.locator('#custom-field-recommendation_note').fill('   ');
    await submit(page, 400);
    assert.match(await page.locator('#submit-error').textContent(), /Book recommendation detail is required/);
    await fill(page, 'Acceptance committed');
    await page.locator('#preferred-pickup-branch').selectOption('38079');
    const received = deferred();
    const release = deferred();
    releases.push(release);
    let pendingPosts = 0;
    await page.route(`**${suggestionPath}`, async route => {
      pendingPosts += 1;
      const response = await route.fetch({ maxRetries: 0 });
      assert.equal(response.status(), 201, await response.text());
      received.resolve();
      await release.promise;
      await route.fulfill({ response });
    });
    await page.locator('#submit-btn').click();
    await atBarrier(received.promise, 'committed submission response');
    await page.locator('#preferred-pickup-branch').selectOption('38078');
    assert.equal(await page.locator('#submit-btn').isDisabled(), true, 'Pickup changes must preserve the pending submission lock.');
    // Exercise another keyboard interaction while the accepted response is pending.
    // The Node regression separately invokes the handler twice and checks its busy guard.
    await page.locator('#author').press('Enter');
    release.resolve();
    await page.locator('#step-success:not(.hidden)').waitFor();
    assert.equal(pendingPosts, 1);
    report.pendingSubmitSinglePost = true;
    await page.unroute(`**${suggestionPath}`);
    await scan(page, 'configured-success');

    // The backend commits, then only the browser response is lost.
    await login(page, 2);
    await fill(page, 'Acceptance unknown');
    await page.route(`**${suggestionPath}`, async route => {
      const response = await route.fetch({ maxRetries: 0 });
      assert.equal(response.status(), 201, await response.text());
      report.lostCommitId = (await response.json()).id;
      await route.abort('failed');
    }, { times: 1 });
    await page.locator('#submit-btn').click();
    await page.locator('#submit-error:not(.hidden)').waitFor();
    assert.match(await page.locator('#submit-error').textContent(), /could not confirm whether.*saved/);
    await page.locator('#preferred-pickup-branch').selectOption('38079');
    assert.equal(await page.locator('#submit-btn').isDisabled(), true, 'Unknown outcomes must remain locked after pickup edits.');
    report.lostCommitLocked = true;
    await scan(page, 'lost-committed-response');
    await page.reload({ waitUntil: 'networkidle' });
    await page.locator('#step-form:not(.hidden)').waitFor();
    await fill(page, 'Acceptance unknown');
    const duplicateResponse = page.waitForResponse(response => isPost(response, suggestionPath));
    await page.locator('#submit-btn').click();
    const duplicate = await duplicateResponse;
    assert.equal(duplicate.status(), 409);
    assert.equal((await duplicate.json()).duplicate.id, report.lostCommitId);
    await page.locator('#step-conflict:not(.hidden)').waitFor();

    // Hold a real committed A response, log out, then display B's independent draft.
    await login(page, 1);
    await fill(page, 'Acceptance old identity');
    const oldCommitted = deferred();
    const oldRelease = deferred();
    const oldDelivered = deferred();
    releases.push(oldRelease);
    await page.route(`**${suggestionPath}`, async route => {
      const response = await route.fetch({ maxRetries: 0 });
      assert.equal(response.status(), 201, await response.text());
      oldCommitted.resolve();
      await oldRelease.promise;
      await route.fulfill({ response });
      oldDelivered.resolve();
    }, { times: 1 });
    await page.locator('#submit-btn').click();
    await atBarrier(oldCommitted.promise, 'old identity commit');
    await page.locator('#step-form .btn-logout').click();
    await page.locator('#barcode').fill('20000000380772');
    await page.locator('#pin').fill('1234');
    await page.locator('#pin').press('Enter');
    await page.locator('#step-form:not(.hidden)').waitFor();
    await page.locator('#title').fill('B independent draft');
    oldRelease.resolve();
    await atBarrier(oldDelivered.promise, 'old identity response delivery');
    await page.waitForLoadState('networkidle');
    assert.equal(await page.locator('#title').inputValue(), 'B independent draft');
    assert.equal(await page.locator('#step-success:not(.hidden)').count(), 0);
    report.committedOldIdentityIgnored = true;

    // A real Settings commit invalidates a stale patron choice before SQL acceptance.
    await login(page, 3);
    await fill(page, 'Acceptance current config');
    // The owning .NET fixture changes Settings at the second real pickup-intent barrier,
    // after this request passed initial validation and before any second provider write.
    await page.locator('#preferred-pickup-branch').selectOption('38079');
    const configurationChanged = await submit(page, 409);
    assert.equal(configurationChanged.code, 'submission_configuration_changed');
    assert.match(await page.locator('#submit-error').textContent(), /configuration|changed|reload/i);
    report.currentConfigurationRejected = true;
    await scan(page, 'current-config-rejection');
    await page.reload({ waitUntil: 'networkidle' });
    await page.locator('#step-form:not(.hidden)').waitFor();
    await page.locator('#preferred-pickup-branch').selectOption('38078');
    assert.match(await page.locator('label[for="custom-field-recommendation_note"]').textContent(), /^Book recommendation detail/);
    await fill(page, 'Acceptance current config', 'beta');
    await submit(page, 201);

    // A pre-dispatch network loss proves the same safe UI lock; no endpoint is replaced with JSON.
    await login(page, 3);
    await fill(page, 'Acceptance never dispatched', 'beta');
    await page.route(`**${suggestionPath}`, route => route.abort('failed'), { times: 1 });
    await page.locator('#submit-btn').click();
    await page.locator('#submit-error:not(.hidden)').waitFor();
    assert.equal(await page.locator('#submit-btn').isDisabled(), true);
    report.preDispatchFailureNoReplay = true;
    await login(page, 3);
    await fill(page, 'Acceptance recovered', 'beta');
    await submit(page, 201);

    await openSettings(admin);
    await admin.locator('#settings-nav-workflow').click();
    await admin.locator('#suggestion-limit').fill('1');
    await saveSettings(admin);
    await login(page, 1);
    await fill(page, 'Acceptance weekly denied', 'beta');
    await submit(page, 406);
    report.weeklyLimitRejected = true;
    await scan(page, 'weekly-limit');
    await login(page, 3);
    await fill(page, 'Acceptance expired denied', 'beta');
    const revoked = await page.evaluate(async () => (await fetch('/api/asap/patron/logout', {
      method: 'POST', headers: { Authorization: `Bearer ${sessionStorage.getItem('asap_patron_token')}` }
    })).status);
    assert.equal(revoked, 204);
    await submit(page, 401);
    await page.locator('#step-login:not(.hidden)').waitFor();
    assert.match(await page.locator('#login-error').textContent(), /session has expired/);
    report.expiredSessionRejected = true;
    await page.setViewportSize({ width: 390, height: 844 });
    await scan(page, 'mobile-session-expired');
    await login(page, 2);
    await scan(page, 'mobile-configured-form');
    assert.equal(await page.locator('#custom-field-optional_choice').inputValue(), '');
    assert.equal(configured.effective.formats.find(item => item.code === 'book').customFields.required_choice.mode, 'required');
    assert.deepEqual(report.pageErrors, []);
    assert.equal(report.externalRequests, 0);
  } finally {
    releases.forEach(release => release.resolve());
    await fs.writeFile(path.join(artifacts, 'browser-results.json'), JSON.stringify(report, null, 2));
    for (const context of contexts) await context.close();
    await browser.close();
  }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
