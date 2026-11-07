'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');
const { decideConfirmation, rejectNativeDialogs } = require('./confirmation.cjs');
const verifySortKeyboard = require('./request-sort-keyboard.cjs');
const [baseURL, artifacts, staffId, tenantId, email, library] = process.argv.slice(2);

(async () => {
  const report = { sorts: [], labels: [], states: [], mutations: [], settingsWrites: [], external: [], errors: [] };
  const browser = await chromium.launch({ headless: true });
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, extraHTTPHeaders: {
    'X-ASAP-Test-Staff-Id': staffId, 'X-ASAP-Test-Tenant-Id': tenantId, 'X-ASAP-Test-Staff-Email': email
  } });
  const page = await context.newPage();
  const axe = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  page.on('pageerror', error => report.errors.push(error.message)); rejectNativeDialogs(page, report.errors);
  await context.route('**/*', route => {
    if (new URL(route.request().url()).origin === new URL(baseURL).origin) return route.continue();
    report.external.push(route.request().url()); return route.abort();
  });
  page.on('request', request => {
    if (new URL(request.url()).pathname === '/api/asap/staff/settings' && request.method() !== 'GET') {
      report.settingsWrites.push(request.method());
    }
    if (/\/api\/asap\/staff\/users(?:\/|$)/.test(new URL(request.url()).pathname) && request.method() !== 'GET') {
      report.mutations.push({ method: request.method(), path: new URL(request.url()).pathname, body: request.postDataJSON() });
    }
  });
  const scan = async name => {
    await page.evaluate(axe);
    const state = await page.evaluate(async () => ({
      violations: (await axe.run()).violations.filter(item => ['serious', 'critical'].includes(item.impact)),
      overflow: document.documentElement.scrollWidth > innerWidth,
      missingImages: [...document.images].filter(node => node.getClientRects().length && (!node.complete || node.naturalWidth === 0)).map(node => node.id)
    }));
    report.states.push({ name, ...state });
    assert.deepEqual(state.violations, [], `${name}: axe`); assert.equal(state.overflow, false, `${name}: overflow`);
    assert.deepEqual(state.missingImages, [], `${name}: missing images`);
    await page.screenshot({ path: path.join(artifacts, `${name}.png`), fullPage: true });
  };
  const settled = () => page.waitForFunction(() => !document.getElementById('settings-form').inert && !document.getElementById('staff-refresh').disabled);
  const users = async () => {
    const response = await context.request.get(`${baseURL}/api/asap/staff/users?orgId=${library}`);
    assert.equal(response.status(), 200, await response.text()); return (await response.json()).users;
  };
  const rowFor = id => page.locator(`.settings-staff-row[data-staff-id="${id}"]`);
  const save = async (row, id, status) => {
    const response = page.waitForResponse(response => response.request().method() === 'PUT' && new URL(response.url()).pathname === `/api/asap/staff/users/${id}`);
    await row.getByRole('button', { name: 'Save changes', exact: true }).click();
    const result = await response; assert.equal(result.status(), status, await result.text());
    return result;
  };
  try {
    await page.goto(`${baseURL}/staff/?stage=suggestion&scope=${library}`, { waitUntil: 'networkidle' });
    const grid = page.locator('#request-grid');
    await page.waitForFunction(() => document.querySelectorAll('#request-grid .request-title-link').length === 3);
    report.sorts = await verifySortKeyboard(page, grid);
    await scan('requests-keyboard');
    await page.goto(`${baseURL}/staff/?stage=settings&settingsScope=${library}#settings-patron`, { waitUntil: 'networkidle' });
    await page.locator('#settings-nav-patron[aria-selected="true"]').waitFor();
    const fields = [
      ['branding-alt', 'Logo alternate text'], ['patron-barcode-label', 'Barcode label'], ['patron-pin-label', 'PIN label'],
      ['patron-suggestion-status-label', 'Suggestion status'], ['patron-closed-status-label', 'Closed status']
    ];
    for (const [id, name] of fields) {
      const input = page.getByRole('textbox', { name, exact: true });
      assert.equal(await input.getAttribute('id'), id);
      const field = page.locator('.settings-field').filter({ has: page.locator(`#${id}`) });
      const status = field.locator('.settings-inheritance-status');
      const original = await input.inputValue();
      assert.equal(await page.getByRole('checkbox', { name: `Override ${name}`, exact: true }).count(), 0);
      assert.equal(await input.isEditable(), true);
      assert.equal(await status.innerText(), 'Using system value');
      await input.focus(); await page.keyboard.press('Tab');
      assert.equal(await input.evaluate(node => node === document.activeElement), false);
      assert.equal(await page.locator('#settings-save').isDisabled(), true, 'Focus alone leaves inherited values clean.');
      await input.fill(original + ' draft');
      assert.match(await status.innerText(), /Library setting.*Unsaved/);
      const reset = field.getByRole('button', { name: `Use system value for ${name}`, exact: true });
      await page.keyboard.press('Tab');
      assert.equal(await reset.evaluate(node => node === document.activeElement && node.matches(':focus-visible')), true);
      await page.keyboard.press('Enter');
      assert.match(await status.innerText(), /Using system value.*Unsaved/);
      assert.equal(await input.evaluate(node => node === document.activeElement), true);
      await decideConfirmation(page, () => page.locator('#settings-discard').click(), dialog => dialog.accept());
      await page.waitForFunction(() => document.querySelector('#settings-save-title').textContent === 'No changes' &&
        !document.querySelector('#settings-form').inert);
      await settled();
      assert.equal(await input.inputValue(), original);
      assert.equal(await status.innerText(), 'Using system value');
      report.labels.push({ id, name, directEdit: true, keyboardReset: true });
    }
    assert.equal(await page.locator('#settings-save').isDisabled(), true, 'Discard restores the accepted inherited values.');
    assert.deepEqual(report.settingsWrites, [], 'Direct edits and pending resets never save implicitly.');
    await scan('settings-library-labels');
    await page.locator('#settings-nav-staff').click(); await settled();
    const create = async address => {
      await page.locator('#staff-add-toggle').click();
      await page.locator('#staff-add-email').fill(address);
      const response = page.waitForResponse(response => response.request().method() === 'POST' && new URL(response.url()).pathname === '/api/asap/staff/users');
      await page.locator('#staff-add-submit').click();
      const result = await response; assert.equal(result.status(), 201, await result.text()); await settled();
      const user = (await result.json()).user; assert.equal(user.displayName, null); return user;
    };
    const first = await create('remediation-save@example.org');
    const second = await create('remediation-deactivate@example.org');
    const row = rowFor(first.id); await row.locator('.settings-staff-summary').click();
    for (const scheme of ['light', 'dark']) {
      await page.emulateMedia({ colorScheme: scheme });
      for (const width of [1280, 390]) {
        await page.setViewportSize({ width, height: 900 });
        if (width === 1280) {
          await page.locator('#operations-view-tab').click(); await page.locator('#operations-view').waitFor();
          await page.locator('#settings-view-tab').click(); await page.locator('#settings-staff').waitFor(); await settled();
        }
        const colors = await page.evaluate(() => {
          const style = selector => getComputedStyle(document.querySelector(selector));
          return { shell: style('#settings-staff').getPropertyValue('--surface'), roster: style('.settings-staff-roster').getPropertyValue('--surface'),
            shellInk: style('#settings-staff').color, rosterInk: style('.settings-staff-roster').color,
            inputScheme: style('.settings-staff-editor input').colorScheme, shellScheme: style('#settings-staff').colorScheme };
        });
        assert.equal(colors.roster.trim(), colors.shell.trim(), `${scheme}/${width}: roster inherits the shell surface`);
        assert.equal(colors.rosterInk, colors.shellInk); assert.equal(colors.inputScheme, colors.shellScheme);
        const display = row.getByRole('textbox', { name: /^Display name/ });
        await display.focus(); await page.keyboard.press('Tab');
        assert.equal(await page.locator(':focus-visible').count(), 1);
        assert.ok(await page.locator(':focus-visible').evaluate(node => parseFloat(getComputedStyle(node).outlineWidth) >= 2));
        await scan(`roster-${scheme}-${width}`);
        report.states.at(-1).palette = colors;
      }
    }
    await page.setViewportSize({ width: 1280, height: 900 });
    await row.getByRole('textbox', { name: /^Display name/ }).fill('Saved fixture');
    await row.getByRole('combobox', { name: /^Role/ }).selectOption('admin');
    const routePath = `**/api/asap/staff/users/${first.id}`;
    const reject = route => route.fulfill({ status: 405, contentType: 'text/html', body: '<h1>Host rejected method</h1>', headers: { 'X-Correlation-ID': 'fixture-host-405' } });
    await page.route(routePath, reject);
    await save(row, first.id, 405); await settled();
    assert.match(await page.locator('#staff-access-status').textContent(), /HTTP 405/);
    assert.match(await page.locator('#staff-access-status').textContent(), /fixture-host-405/);
    assert.equal((await users()).find(user => user.id === first.id).displayName, null);
    assert.equal(await row.getByRole('textbox', { name: /^Display name/ }).inputValue(), 'Saved fixture');
    assert.equal(await row.locator('.settings-staff-unsaved').isVisible(), true);
    await scan('staff-save-rejected-draft');
    await page.unroute(routePath, reject);
    await save(row, first.id, 200); await settled();
    const saved = (await users()).find(user => user.id === first.id);
    assert.equal(saved.displayName, 'Saved fixture'); assert.equal(saved.role, 'admin');
    assert.equal(await row.locator('.settings-staff-unsaved').isVisible(), false);
    await row.getByRole('textbox', { name: /^Email address/ }).fill(second.userPrincipalName);
    await save(row, first.id, 409); await settled();
    assert.match(await page.locator('#staff-access-status').textContent(), /identity already exists/);
    assert.equal((await users()).find(user => user.id === first.id).userPrincipalName, first.userPrincipalName);
    await row.getByRole('button', { name: 'Cancel', exact: true }).click();
    const loseResponse = reference => async route => {
      const response = await route.fetch(); assert.equal(response.status(), 200, await response.text());
      await route.fulfill({ status: 502, contentType: 'text/html', body: '<h1>Response lost after commit</h1>', headers: { 'X-Correlation-ID': reference } });
    };
    await row.getByRole('textbox', { name: /^Display name/ }).fill('Reconciled fixture');
    const lostSave = loseResponse('fixture-save-502');
    await page.route(routePath, lostSave);
    await save(row, first.id, 502);
    await page.locator('#staff-access-status').filter({ hasText: 'fixture-save-502' }).waitFor();
    assert.equal((await users()).find(user => user.id === first.id).displayName, 'Reconciled fixture');
    assert.equal(await row.locator('.settings-staff-unsaved').isVisible(), true);
    assert.equal(await page.locator('#settings-form').evaluate(node => node.inert), true);
    await page.unroute(routePath, lostSave);
    await decideConfirmation(page, () => page.locator('#settings-refresh').click(), dialog => dialog.accept()); await settled();
    if (await row.locator('.settings-staff-summary').getAttribute('aria-expanded') !== 'true') await row.locator('.settings-staff-summary').click();
    assert.equal(await row.getByRole('textbox', { name: /^Display name/ }).inputValue(), 'Reconciled fixture');
    assert.equal(await row.locator('.settings-staff-unsaved').isVisible(), false);
    await scan('staff-save-reconciled');
    // A no-first-sign-in account can deactivate without a preceding profile Save.
    const target = rowFor(second.id); await target.locator('.settings-staff-summary').click();
    const targetPath = `**/api/asap/staff/users/${second.id}`;
    await page.route(targetPath, reject);
    await decideConfirmation(page, () => target.getByRole('button', { name: 'Deactivate', exact: true }).click(), dialog => dialog.accept());
    await page.locator('#staff-access-status').filter({ hasText: 'HTTP 405' }).waitFor(); await settled();
    assert.equal((await users()).find(user => user.id === second.id).active, true);
    await page.unroute(targetPath, reject);
    const uncertain = loseResponse('fixture-committed-502');
    await page.route(targetPath, uncertain);
    await decideConfirmation(page, () => target.getByRole('button', { name: 'Deactivate', exact: true }).click(), dialog => dialog.accept());
    await page.locator('#staff-access-status').filter({ hasText: 'HTTP 502' }).waitFor();
    assert.match(await page.locator('#staff-access-status').textContent(), /Refresh settings/);
    assert.match(await page.locator('#staff-access-status').textContent(), /fixture-committed-502/);
    assert.equal((await users()).find(user => user.id === second.id).active, false);
    assert.equal(await page.locator('#settings-form').evaluate(node => node.inert), true, 'Uncertain results prevent blind retries.');
    await page.unroute(targetPath, uncertain);
    await page.locator('#settings-refresh').click(); await settled();
    assert.equal(await target.isVisible(), false);
    await page.getByRole('checkbox', { name: 'Show inactive accounts', exact: true }).check();
    assert.match(await target.locator('.status-badge').textContent(), /Inactive/);
    assert.match(await page.locator('#staff-roster-counts').textContent(), /1 active account.*1 inactive account shown/);
    await scan('staff-deactivate-reconciled');
    await decideConfirmation(page, () => row.getByRole('button', { name: 'Deactivate', exact: true }).click(), dialog => dialog.accept());
    await page.waitForFunction(id => document.querySelector(`[data-staff-id="${id}"] .status-badge`)?.textContent === 'Inactive', first.id);
    await settled(); assert.match(await page.locator('#staff-access-status').textContent(), /Staff user deactivated/);
    assert.equal((await users()).filter(user => user.active).length, 0);
    await scan('staff-deactivate-confirmed');
    assert.deepEqual(report.errors, []); assert.deepEqual(report.external, []);
  } catch (error) {
    report.failure = error.message;
    await page.screenshot({ path: path.join(artifacts, 'failure.png'), fullPage: true });
    throw error;
  } finally {
    await fs.writeFile(path.join(artifacts, 'staff-remediation.json'), JSON.stringify(report, null, 2));
    await browser.close();
  }
  console.log(`Staff remediation passed: ${report.sorts.length} keyboard headings, ${report.states.length} rendered states.`);
})().catch(error => { console.error(error); process.exitCode = 1; });
