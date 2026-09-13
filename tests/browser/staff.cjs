'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { URL } = require('node:url');

function parseArguments(argv) {
  if (argv.length !== 12) {
    throw new Error('Usage: node tests/browser/staff.cjs <baseURL> <artifactDirectory> <superId> <tenantId> <superObjectId> <staffId> <staffObjectId> <legacyRequestId> <primaryRequestId> <blockedRequestId> <resolutionRequestId> <otherRequestId>');
  }
  const parsed = new URL(argv[0]);
  if (!['http:', 'https:'].includes(parsed.protocol) || parsed.pathname !== '/' ||
      parsed.username || parsed.password || parsed.search || parsed.hash) {
    throw new Error('baseURL must be an absolute origin without a path or credentials.');
  }
  return {
    baseOrigin: parsed.origin,
    artifactRoot: path.resolve(argv[1]),
    superIdentity: { staffId: argv[2], tenantId: argv[3], objectId: argv[4] },
    staffIdentity: { staffId: argv[5], tenantId: argv[3], objectId: argv[6] },
    legacyRequestId: argv[7],
    primaryRequestId: argv[8],
    blockedRequestId: argv[9],
    resolutionRequestId: argv[10],
    otherRequestId: argv[11]
  };
}

function sameOrigin(url, origin) {
  try { return new URL(url).origin === origin; } catch { return false; }
}

async function createContext(browser, viewport, baseOrigin, identity) {
  const context = await browser.newContext({
    viewport,
    extraHTTPHeaders: identity ? {
      'X-ASAP-Test-Staff-Id': identity.staffId,
      'X-ASAP-Test-Tenant-Id': identity.tenantId,
      'X-ASAP-Test-Object-Id': identity.objectId
    } : undefined
  });
  const traffic = { externalRequests: 0 };
  await context.route('**/*', route => {
    if (sameOrigin(route.request().url(), baseOrigin)) return route.continue();
    traffic.externalRequests += 1;
    return route.abort('blockedbyclient');
  });
  return { context, traffic };
}

async function scan(page, axeSource, artifactRoot, report, viewport, state) {
  await page.evaluate(axeSource);
  const accessibility = await page.evaluate(async () => {
    const result = await axe.run();
    return result.violations.filter(item => ['serious', 'critical'].includes(item.impact));
  });
  const layout = await page.evaluate(() => ({
    width: innerWidth,
    scrollWidth: document.documentElement.scrollWidth,
    visibleImagesLoaded: [...document.images]
      .filter(image => {
        const bounds = image.getBoundingClientRect();
        return bounds.width > 0 && bounds.height > 0;
      })
      .every(image => image.complete && image.naturalWidth > 0)
  }));
  await page.screenshot({ path: path.join(artifactRoot, `${viewport}-${state}.png`), fullPage: true });
  report.states.push({ viewport, state, accessibility, layout });
  assert.deepEqual(accessibility, [], `Serious or critical accessibility violation in ${viewport}/${state}`);
  assert.ok(layout.scrollWidth <= layout.width, `Horizontal document overflow in ${viewport}/${state}`);
  assert.equal(layout.visibleImagesLoaded, true, `Visible image failed in ${viewport}/${state}`);
}

async function assertReadableMobileQueue(page) {
  const geometry = await page.locator('#request-grid').evaluate(root => {
    const wrapper = root.querySelector('.gridjs-wrapper');
    const table = root.querySelector('table.gridjs-table');
    const row = root.querySelector('tbody tr');
    const cells = row ? [...row.querySelectorAll('td')] : [];
    const bounds = cells.map(cell => cell.getBoundingClientRect());
    return {
      wrapperClientWidth: wrapper ? wrapper.clientWidth : 0,
      wrapperScrollWidth: wrapper ? wrapper.scrollWidth : 0,
      wrapperRight: wrapper ? wrapper.getBoundingClientRect().right : 0,
      tableWidth: table ? table.getBoundingClientRect().width : 0,
      viewportWidth: innerWidth,
      cellCount: cells.length,
      overlaps: bounds.slice(1).some((current, index) => current.left < bounds[index].right - 1),
      nonPositiveCells: bounds.some(bounds => bounds.width <= 0)
    };
  });
  assert.equal(geometry.cellCount, 7, 'Mobile queue should render all request columns');
  assert.equal(geometry.overlaps, false, 'Mobile queue cells must not overlap neighboring columns');
  assert.equal(geometry.nonPositiveCells, false, 'Mobile queue cells need stable positive widths');
  assert.ok(geometry.wrapperScrollWidth > geometry.wrapperClientWidth, 'Mobile queue should scroll inside its wrapper');
  assert.ok(geometry.tableWidth > geometry.viewportWidth, 'Mobile queue should retain readable column widths');
  assert.ok(geometry.wrapperRight <= geometry.viewportWidth + 1, 'Queue wrapper must stay inside the viewport');
}

async function session(context, baseOrigin) {
  const response = await context.request.get(`${baseOrigin}/api/asap/staff/session`);
  assert.equal(response.status(), 200, 'Could not load the staff session contract');
  return response.json();
}

async function mutate(context, baseOrigin, route, data) {
  const current = await session(context, baseOrigin);
  assert.equal(current.authenticated, true, 'Mutation identity was not authenticated');
  return context.request.post(`${baseOrigin}${route}`, {
    headers: { 'X-ASAP-Antiforgery': current.antiforgeryToken },
    data
  });
}

async function runAnonymous(browser, args, axeSource, report) {
  for (const [name, viewport] of [['desktop', { width: 1280, height: 900 }], ['mobile', { width: 390, height: 844 }]]) {
    const { context, traffic } = await createContext(browser, viewport, args.baseOrigin, null);
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    try {
      await page.goto(`${args.baseOrigin}/staff/`, { waitUntil: 'networkidle' });
      await page.locator('#signed-out').waitFor({ state: 'visible' });
      assert.equal(await page.locator('#workspace').isVisible(), false);
      assert.equal(
        await page.locator('.sign-in').getAttribute('href'),
        '/api/asap/staff/sign-in?returnUrl=%2Fstaff%2F'
      );
      await scan(page, axeSource, args.artifactRoot, report, name, 'anonymous-sign-in');
      assert.deepEqual(errors, [], `${name} anonymous shell raised a browser error`);
      assert.equal(traffic.externalRequests, 0, `${name} anonymous shell requested an external asset`);
    } finally {
      await context.close();
    }
  }
}

async function runSuperAdmin(browser, args, axeSource, report) {
  const { context, traffic } = await createContext(
    browser,
    { width: 1280, height: 900 },
    args.baseOrigin,
    args.superIdentity
  );
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  try {
    await page.goto(`${args.baseOrigin}/staff/?request=${encodeURIComponent(args.legacyRequestId)}`, { waitUntil: 'networkidle' });
    await page.locator('#workspace').waitFor({ state: 'visible' });
    await page.locator('#request-dialog[open]').waitFor();
    assert.deepEqual(errors, [], `Request detail raised a browser error: ${errors.join('; ')}`);
    assert.equal(await page.evaluate(() => document.activeElement.id), 'close-request');
    assert.match(page.url(), new RegExp(`[?&]request=${args.primaryRequestId.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`));
    assert.equal(await page.locator('#library-scope').inputValue(), 'all');
    const format = page.getByLabel('Format', { exact: true });
    const publication = page.getByLabel('Publication timing', { exact: true });
    assert.equal(await format.evaluate(node => node.tagName), 'SELECT');
    assert.deepEqual(
      await format.locator('option').allTextContents(),
      ['Book', 'Audiobook (Physical CD)', 'DVD', 'Music CD', 'eBook', 'eAudiobook']
    );
    assert.equal(await format.inputValue(), 'book');
    assert.equal(await publication.evaluate(node => node.tagName), 'SELECT');
    assert.deepEqual(
      await publication.locator('option').evaluateAll(options => options.map(option => option.value)),
      ['Library early release', 'Library backlist', 'Historical browser publication']
    );
    assert.equal(await publication.inputValue(), 'Historical browser publication');
    assert.equal(await page.getByLabel('Audience note', { exact: true }).inputValue(), 'Historic audience');
    const binding = page.getByLabel('Binding *', { exact: true });
    assert.equal(await binding.evaluate(node => node.tagName), 'SELECT');
    assert.equal(await binding.inputValue(), 'historic');
    assert.deepEqual(
      await binding.locator('option').evaluateAll(options => options.map(option => option.value)),
      ['', 'hardback', 'paperback', 'historic']
    );
    await scan(page, axeSource, args.artifactRoot, report, 'desktop', 'deep-link-detail');

    await page.getByLabel('Title', { exact: true }).fill('Browser staff title edited');
    await publication.selectOption('Library backlist');
    await page.getByLabel('Audience note', { exact: true }).fill('Edited audience');
    await binding.selectOption('hardback');
    await page.getByRole('button', { name: 'Save changes' }).click();
    await page.getByText('Request changes saved.').waitFor();
    await page.getByRole('button', { name: 'Unclaim' }).waitFor();

    const currentRequest = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/title-requests/${args.primaryRequestId}`
    );
    assert.equal(currentRequest.status(), 200);
    const request = await currentRequest.json();
    const firstUnclaim = await mutate(
      context,
      args.baseOrigin,
      `/api/asap/staff/title-requests/${args.primaryRequestId}/unclaim`,
      { version: request.version }
    );
    assert.equal(firstUnclaim.status(), 200, await firstUnclaim.text());
    await page.getByRole('button', { name: 'Unclaim' }).click();
    await page.getByRole('button', { name: 'Claim', exact: true }).waitFor();
    assert.match(await page.locator('#app-status').textContent(), /changed|blocked|reload/i);

    await page.getByRole('button', { name: 'Claim', exact: true }).click();
    await page.getByRole('button', { name: 'Unclaim' }).waitFor();
    await page.getByRole('button', { name: 'Purchase', exact: true }).click();
    await page.getByText('Outstanding purchase', { exact: true }).waitFor();
    await scan(page, axeSource, args.artifactRoot, report, 'desktop', 'edited-claimed-actioned');

    await page.getByRole('button', { name: 'Close request details' }).click();
    await page.getByRole('button', { name: 'Profile' }).click();
    assert.equal(await page.evaluate(() => document.activeElement.id), 'profile-title');
    await page.locator('#weekly-email').fill('browser-weekly@example.org');
    await page.locator('#weekly-enabled').check();
    await page.locator('#mine-default').check();
    await page.getByRole('button', { name: 'Save profile' }).click();
    await page.getByText('Profile saved.').waitFor();
    await scan(page, axeSource, args.artifactRoot, report, 'desktop', 'profile-saved');

    await page.getByRole('button', { name: 'Requests' }).click();
    assert.equal(await page.evaluate(() => document.activeElement.id), 'queue-title');
    const suggestionTab = page.locator('[data-status="suggestion"]');
    await suggestionTab.focus();
    await page.keyboard.press('ArrowRight');
    assert.equal(await page.evaluate(() => document.activeElement.dataset.status), 'outstanding_purchase');
    await page.keyboard.press('Enter');
    await page.locator('#library-scope').selectOption('2');
    await page.getByRole('button', { name: `Open request ${args.primaryRequestId}` }).waitFor();
    assert.equal(await page.getByText('Other browser library title', { exact: true }).count(), 0);
    const open = page.getByRole('button', { name: `Open request ${args.primaryRequestId}` });
    await open.click();
    await page.locator('#request-dialog[open]').waitFor();
    await page.keyboard.press('Escape');
    await page.locator('#request-dialog').waitFor({ state: 'hidden' });
    assert.equal(await page.evaluate(() => document.activeElement.classList.contains('grid-open')), true);
    assert.deepEqual(errors, [], `Super-admin browser flow raised an error: ${errors.join('; ')}`);
    assert.equal(traffic.externalRequests, 0, 'Super-admin browser flow requested an external asset');
  } finally {
    await context.close();
  }
}

async function runScopedBlocked(browser, args, axeSource, report) {
  const { context, traffic } = await createContext(
    browser,
    { width: 390, height: 844 },
    args.baseOrigin,
    args.staffIdentity
  );
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  try {
    await page.goto(`${args.baseOrigin}/staff/?request=${args.blockedRequestId}`, { waitUntil: 'networkidle' });
    await page.locator('#request-dialog[open]').waitFor();
    await page.getByText('Hold placement recovery', { exact: true }).waitFor();
    assert.equal(await page.getByLabel('Identifier', { exact: true }).isDisabled(), true);
    assert.equal(await page.getByLabel('BIB ID', { exact: true }).isDisabled(), true);
    assert.equal(await page.getByLabel('Automatically place hold', { exact: true }).isDisabled(), true);
    assert.equal(await page.getByLabel('Title', { exact: true }).isEnabled(), true);
    assert.equal(await page.getByRole('button', { name: 'Save changes' }).isEnabled(), true);
    assert.equal(await page.getByRole('button', { name: 'Claim', exact: true }).isEnabled(), true);
    for (const name of ['Purchase', 'Already own', 'Reject', 'Close silently']) {
      assert.equal(await page.getByRole('button', { name, exact: true }).isDisabled(), true, `${name} should honor the operation barrier`);
    }
    assert.equal(await page.getByRole('button', { name: 'Reconcile provider state' }).count(), 0);
    assert.equal(await page.getByRole('button', { name: 'Resolve operation' }).count(), 0);
    assert.equal(await page.locator('#scope-field').isVisible(), false);
    const forbidden = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/title-requests/${args.otherRequestId}`
    );
    assert.equal(forbidden.status(), 404, 'Library staff could read another library request');
    await scan(page, axeSource, args.artifactRoot, report, 'mobile', 'scoped-blocked-recovery');
    await page.getByRole('button', { name: 'Close request details' }).click();
    await page.locator('#request-dialog').waitFor({ state: 'hidden' });
    await assertReadableMobileQueue(page);
    await scan(page, axeSource, args.artifactRoot, report, 'mobile', 'scoped-queue');
    await page.getByRole('button', { name: 'Sign out' }).click();
    await page.locator('#signed-out').waitFor({ state: 'visible' });
    assert.deepEqual(errors, [], `Scoped browser flow raised an error: ${errors.join('; ')}`);
    assert.equal(traffic.externalRequests, 0, 'Scoped browser flow requested an external asset');
  } finally {
    await context.close();
  }
}

async function runOperatorResolution(browser, args, axeSource, report) {
  for (const [name, viewport] of [['desktop', { width: 1280, height: 900 }], ['mobile', { width: 390, height: 844 }]]) {
    const { context, traffic } = await createContext(browser, viewport, args.baseOrigin, args.superIdentity);
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    try {
      await page.goto(`${args.baseOrigin}/staff/?request=${args.resolutionRequestId}`, { waitUntil: 'networkidle' });
      await page.locator('#request-dialog[open]').waitFor();
      await page.getByText('Hold placement recovery', { exact: true }).waitFor();
      await page.getByText(/Operation \d+; attempt 2; epoch 3; frozen patron \*+2904; frozen BIB 92904\./).waitFor();
      const resolution = page.getByLabel('Resolution', { exact: true });
      const evidence = page.getByLabel('Evidence type', { exact: true });
      assert.equal(await resolution.inputValue(), 'succeeded');
      assert.equal(await evidence.inputValue(), 'authoritative_correlated_hold');
      assert.equal(await page.getByLabel('Proven final hold ID', { exact: true }).isVisible(), true);
      assert.equal(await page.getByLabel('Evidence provenance', { exact: true }).isVisible(), true);
      assert.equal(await page.getByLabel(/I attest that this evidence proves/).isVisible(), true);
      assert.equal(await page.getByLabel(/every responsible or superseded worker/).isVisible(), true);
      assert.equal(await page.getByLabel('Executor exclusion reference', { exact: true }).isVisible(), true);
      assert.equal(await page.getByLabel('Executor exclusion and in-flight work account', { exact: true }).isVisible(), true);
      assert.equal(await evidence.locator('option[value="fenced_never_dispatched"]').count(), 0);
      await resolution.focus();
      await page.keyboard.press('Tab');
      assert.equal(await evidence.evaluate(node => document.activeElement === node), true);
      await scan(page, axeSource, args.artifactRoot, report, name, 'operator-resolution');

      await fillOperatorResolution(page, false);
      const resolveButton = page.getByRole('button', { name: 'Resolve operation' });
      if (name === 'desktop') {
        let resolutionRequests = 0;
        page.on('request', request => {
          if (request.method() === 'POST' && new URL(request.url()).pathname.endsWith('/resolve')) {
            resolutionRequests += 1;
          }
        });
        await resolveButton.click();
        await page.waitForTimeout(150);
        assert.equal(resolutionRequests, 0, 'Missing proof attestation must not issue a resolution request');
        assert.equal(
          await page.getByLabel(/I attest that this evidence proves/).evaluate(node => node.matches(':invalid')),
          true,
          'Missing operation-specific proof attestation should remain visibly invalid'
        );
      }
      await page.getByLabel(/I attest that this evidence proves/).check();
      const reason = page.getByLabel('Reason', { exact: true });
      await reason.focus();
      await page.keyboard.press('Tab');
      assert.equal(await resolveButton.evaluate(node => document.activeElement === node), true);
      await resolveButton.scrollIntoViewIfNeeded();
      const buttonBounds = await resolveButton.boundingBox();
      assert.ok(buttonBounds && buttonBounds.y >= 0 && buttonBounds.y + buttonBounds.height <= viewport.height,
        `${name} lower resolution command should be visible inside the scrolled dialog`);
      await scan(page, axeSource, args.artifactRoot, report, name, 'operator-resolution-lower');
      assert.deepEqual(errors, [], `${name} operator resolution raised a browser error`);
      assert.equal(traffic.externalRequests, 0, `${name} operator resolution requested an external asset`);
    } finally {
      await context.close();
    }
  }

  const { context, traffic } = await createContext(
    browser,
    { width: 1280, height: 900 },
    args.baseOrigin,
    args.superIdentity
  );
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  try {
    await page.goto(`${args.baseOrigin}/staff/?request=${args.resolutionRequestId}`, { waitUntil: 'networkidle' });
    await page.getByText(/Operation \d+; attempt 2; epoch 3; frozen patron \*+2904; frozen BIB 92904\./).waitFor();
    await fillOperatorResolution(page, true);
    page.once('dialog', dialog => dialog.accept());
    const [response] = await Promise.all([
      page.waitForResponse(candidate =>
        candidate.request().method() === 'POST' &&
        /\/api\/asap\/staff\/hold-operations\/\d+\/resolve$/.test(new URL(candidate.url()).pathname)),
      page.getByRole('button', { name: 'Resolve operation' }).click()
    ]);
    assert.equal(response.status(), 200, await response.text());
    await page.getByText('Hold recovery state updated.', { exact: true }).waitFor();
    const statusValue = page.locator('#request-dialog .status-badge');
    await statusValue.waitFor();
    assert.equal((await statusValue.textContent()).trim(), 'Hold placed');
    assert.equal(await page.getByRole('button', { name: 'Resolve operation' }).count(), 0);
    await scan(page, axeSource, args.artifactRoot, report, 'desktop', 'operator-resolution-accepted');
    assert.deepEqual(errors, [], `Accepted operator resolution raised a browser error: ${errors.join('; ')}`);
    assert.equal(traffic.externalRequests, 0, 'Accepted operator resolution requested an external asset');
  } finally {
    await context.close();
  }
}

async function fillOperatorResolution(page, attestProof) {
  await page.getByLabel('Evidence reference', { exact: true }).fill('Support report SR-BROWSER-2904');
  await page.getByLabel('Evidence provenance', { exact: true }).fill('Polaris support final transaction report');
  await page.getByLabel('Connection to this exact attempt', { exact: true })
    .fill('Report identifies operation attempt 2, frozen patron ending 2904, and BIB 92904 as the completed transaction.');
  await page.getByLabel('Proven final hold ID', { exact: true }).fill('8456');
  await page.getByLabel(/every responsible or superseded worker/).check();
  await page.getByLabel('Executor exclusion reference', { exact: true }).fill('Operations record OPS-BROWSER-2904');
  await page.getByLabel('Executor exclusion and in-flight work account', { exact: true })
    .fill('All responsible and superseded workers and interactive hosts ended at 2026-09-13T13:00:00Z; the cited final report accounts for already-sent provider work.');
  await page.getByLabel(/I attest that the exclusion record identifies/).check();
  await page.getByLabel('Reason', { exact: true }).fill('Resolve from the documented final provider result after verified executor exclusion.');
  if (attestProof) await page.getByLabel(/I attest that this evidence proves/).check();
}

async function loadDependencies() {
  let chromium;
  try { ({ chromium } = require('playwright')); } catch {
    throw new Error('Missing local Playwright. Run npm install with playwright@1.62.1.');
  }
  let axePath;
  try { axePath = require.resolve('axe-core/axe.min.js'); } catch {
    throw new Error('Missing local axe-core. Run npm install with axe-core@4.10.3.');
  }
  return { chromium, axeSource: await fs.readFile(axePath, 'utf8') };
}

async function launch(chromium) {
  const executablePath = process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH;
  if (executablePath) await fs.access(executablePath);
  return chromium.launch({ headless: true, ...(executablePath ? { executablePath } : {}) });
}

async function main() {
  const args = parseArguments(process.argv.slice(2));
  await fs.mkdir(args.artifactRoot, { recursive: true });
  const { chromium, axeSource } = await loadDependencies();
  const browser = await launch(chromium);
  const report = { states: [] };
  try {
    await runAnonymous(browser, args, axeSource, report);
    await runSuperAdmin(browser, args, axeSource, report);
    await runOperatorResolution(browser, args, axeSource, report);
    await runScopedBlocked(browser, args, axeSource, report);
    assert.equal(report.states.length, 12, 'Expected twelve major staff browser states');
    await fs.writeFile(
      path.join(args.artifactRoot, 'staff-browser-results.json'),
      JSON.stringify(report, null, 2),
      'utf8'
    );
  } finally {
    await browser.close();
  }
}

main().catch(error => {
  console.error(`Staff browser acceptance failed: ${error.stack || error.message || 'unknown failure'}`);
  process.exitCode = 1;
});
