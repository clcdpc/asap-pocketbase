'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { URL } = require('node:url');

function parseArguments(argv) {
  if (argv.length !== 25) {
    throw new Error('Usage: node tests/browser/staff-legacy.cjs <baseURL> <artifactDirectory> <superId> <tenantId> <superEmail> <staffId> <staffEmail> <legacyRequestId> <primaryRequestId> <blockedRequestId> <resolutionRequestId> <otherRequestId> <copySourceRequestId> <invalidClosedCopyId> <invalidClaimantId> <legacyRuleId> <mobileCopyId> <foreignStaffId> <invalidTenantStaffId> <unboundStaffId> <staleTitleAId> <staleTitleBId> <staleCopyAId> <staleCopyBId> <staleCreateSourceId>');
  }
  const parsed = new URL(argv[0]);
  if (!['http:', 'https:'].includes(parsed.protocol) || parsed.pathname !== '/' ||
      parsed.username || parsed.password || parsed.search || parsed.hash) {
    throw new Error('baseURL must be an absolute origin without a path or credentials.');
  }
  return {
    baseOrigin: parsed.origin,
    artifactRoot: path.resolve(argv[1]),
    superIdentity: { staffId: argv[2], tenantId: argv[3], email: argv[4] },
    staffIdentity: { staffId: argv[5], tenantId: argv[3], email: argv[6] },
    legacyRequestId: argv[7],
    primaryRequestId: argv[8],
    otherRequestId: argv[11],
    mobileCopyId: argv[16],
    staleTitleBId: argv[21]
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
      'X-ASAP-Test-Staff-Email': identity.email
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
      .every(image => image.complete && image.naturalWidth > 0),
    overflowing: [...document.querySelectorAll('body *')]
      .filter(element => {
        const bounds = element.getBoundingClientRect();
        return bounds.width > 0 && (bounds.right > innerWidth + 1 || bounds.left < -1);
      })
      .slice(0, 12)
      .map(element => ({ tag: element.tagName, id: element.id, className: element.className, bounds: element.getBoundingClientRect().toJSON() }))
  }));
  await page.screenshot({ path: path.join(artifactRoot, `legacy-${viewport}-${state}.png`), fullPage: true });
  report.states.push({ viewport, state, accessibility, layout });
  assert.equal(
    accessibility.length,
    0,
    `Serious or critical accessibility violation in ${viewport}/${state}: ${JSON.stringify(accessibility.map(item => ({ id: item.id, targets: item.nodes.map(node => node.target) })))}`
  );
  assert.ok(layout.scrollWidth <= layout.width, `Horizontal document overflow in ${viewport}/${state}: ${JSON.stringify(layout)}`);
  assert.equal(layout.visibleImagesLoaded, true, `Visible image failed in ${viewport}/${state}`);
}

async function session(context, baseOrigin) {
  const response = await context.request.get(`${baseOrigin}/api/asap/staff/session`);
  assert.equal(response.status(), 200);
  return response.json();
}

async function post(context, baseOrigin, route, data) {
  const current = await session(context, baseOrigin);
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
      await page.locator('#login-container').waitFor({ state: 'visible' });
      assert.equal(await page.locator('#app-container').isVisible(), false);
      await page.getByRole('button', { name: 'Sign in with Microsoft' }).waitFor();
      await scan(page, axeSource, args.artifactRoot, report, name, 'anonymous-sign-in');
      assert.deepEqual(errors, [], `${name} anonymous legacy shell raised a browser error`);
      assert.equal(traffic.externalRequests, 0, `${name} anonymous legacy shell requested an external asset`);
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
    await page.locator('#app-container').waitFor({ state: 'visible' });
    try {
      await page.locator('#editModal[open]').waitFor({ timeout: 10000 });
    } catch (error) {
      const diagnostics = await page.evaluate(() => ({
        job: document.getElementById('job-msg')?.textContent || '',
        announcement: document.getElementById('status-announcer')?.textContent || ''
      }));
      throw new Error(`Legacy request dialog did not open: ${JSON.stringify(diagnostics)} at ${page.url()}`);
    }
    assert.equal(await page.locator('#edit-id').inputValue(), args.primaryRequestId);
    assert.equal(new URL(page.url()).searchParams.get('request'), args.primaryRequestId);
    assert.equal(await page.evaluate(() => document.activeElement.id), 'close-modal-btn');
    await scan(page, axeSource, args.artifactRoot, report, 'desktop', 'deep-link-detail');

    await page.locator('#close-modal-x').click();
    await page.locator('#editModal').waitFor({ state: 'hidden' });
    const tabs = page.locator('#status-tabs [data-status]');
    assert.ok(await tabs.count() >= 8, 'Legacy status/settings navigation was not rendered');
    await page.locator('#grid-search-input').fill('no-result-browser-filter');
    await page.getByText('No suggestions found.', { exact: true }).waitFor();
    await page.locator('#grid-search-input').fill('');
    await page.locator('#grid-container .gridjs-container').waitFor();
    await scan(page, axeSource, args.artifactRoot, report, 'desktop', 'queue-search-tabs');

    const targetResponse = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/title-requests/${args.staleTitleBId}`
    );
    assert.equal(targetResponse.status(), 200);
    const target = await targetResponse.json();
    const staleResponse = await post(
      context,
      args.baseOrigin,
      `/api/asap/staff/title-requests/${args.staleTitleBId}/assign`,
      { version: 'AAAAAAAAAAA=', assigneeId: args.superIdentity.staffId }
    );
    assert.equal(staleResponse.status(), 409, 'Stale rowversion did not produce the current conflict contract');

    await page.goto(
      `${args.baseOrigin}/staff/?stage=${encodeURIComponent(target.status)}&request=${args.staleTitleBId}`,
      { waitUntil: 'networkidle' }
    );
    await page.locator('#editModal[open]').waitFor();
    assert.equal(await page.locator('#edit-id').inputValue(), args.staleTitleBId);
    await page.locator('#close-modal-x').click();
    const marker = page.locator(`[data-suggestion-id="${args.staleTitleBId}"]`).first();
    const row = marker.locator('xpath=ancestor::tr');
    await row.waitFor();
    const menu = row.locator('.row-action-menu-trigger');
    await menu.click();
    const claim = page.getByRole('menuitem', { name: 'Claim', exact: true });
    const unclaim = page.getByRole('menuitem', { name: 'Unclaim', exact: true });
    if (await claim.count()) {
      await claim.click();
      await page.getByText('Request claimed.', { exact: true }).waitFor();
      const refreshedMarker = page.locator(`[data-suggestion-id="${args.staleTitleBId}"]`).first();
      const refreshedRow = refreshedMarker.locator('xpath=ancestor::tr');
      await refreshedRow.locator('.row-action-menu-trigger').click();
      await page.getByRole('menuitem', { name: 'Unclaim', exact: true }).click();
      await page.getByText('Request unclaimed.', { exact: true }).waitFor();
    } else if (await unclaim.count()) {
      await menu.press('Escape');
    }
    report.concurrency = { staleStatus: staleResponse.status() };

    await page.goto(`${args.baseOrigin}/staff/?stage=additional_copies&request=${args.mobileCopyId}`, { waitUntil: 'networkidle' });
    await page.locator('#editModal[open]').waitFor();
    assert.equal(await page.locator('#edit-id').inputValue(), args.mobileCopyId);
    assert.match(await page.locator('#editModalLabel').textContent(), /additional-copy|edit/i);
    await scan(page, axeSource, args.artifactRoot, report, 'desktop', 'additional-copy-detail');
    await page.locator('#close-modal-x').click();

    await page.locator('[data-status="settings"]').click();
    await page.locator('#settings-container').waitFor({ state: 'visible' });
    await page.locator('#tab-staff').click();
    await page.locator('#settings-staff.active').waitFor();
    await scan(page, axeSource, args.artifactRoot, report, 'desktop', 'settings-staff-access');

    await page.locator('[data-status="analytics"]').click();
    await page.locator('#analytics-title').waitFor();
    assert.equal(await page.locator('#analytics-scope').inputValue(), 'all');
    await scan(page, axeSource, args.artifactRoot, report, 'desktop', 'analytics');

    await page.locator('#profile-btn').click();
    await page.locator('#profile-dialog[open]').waitFor();
    await page.locator('#profile-weekly-action-summary-email').waitFor();
    await scan(page, axeSource, args.artifactRoot, report, 'desktop', 'profile');
    await page.locator('#profile-cancel').click();

    await page.locator('#logout-btn').click();
    await page.locator('#login-container').waitFor({ state: 'visible' });
    assert.equal(await page.locator('#app-container').isVisible(), false);
    assert.deepEqual(errors, [], `Legacy super-admin flow raised a browser error: ${errors.join('; ')}`);
    assert.equal(traffic.externalRequests, 0, 'Legacy super-admin flow requested an external asset');
  } finally {
    await context.close();
  }
}

async function runScopedStaff(browser, args, axeSource, report) {
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
    const forbidden = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/title-requests/${args.otherRequestId}`
    );
    assert.ok([403, 404].includes(forbidden.status()), 'Out-of-scope request was exposed');
    await page.goto(`${args.baseOrigin}/staff/?request=${args.otherRequestId}`, { waitUntil: 'networkidle' });
    await page.locator('#app-container').waitFor({ state: 'visible' });
    await page.getByText('That request is no longer available.', { exact: true }).waitFor();
    assert.equal(await page.locator('#editModal[open]').count(), 0);
    assert.equal(await page.locator('#nav-settings').isVisible(), false);
    await scan(page, axeSource, args.artifactRoot, report, 'mobile', 'library-scope-denied');
    assert.deepEqual(errors, [], `Legacy scoped staff flow raised a browser error: ${errors.join('; ')}`);
    assert.equal(traffic.externalRequests, 0, 'Legacy scoped staff flow requested an external asset');
  } finally {
    await context.close();
  }
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
    await runScopedStaff(browser, args, axeSource, report);
    assert.equal(report.states.length, 8, 'Expected eight primary legacy staff browser states');
    await fs.writeFile(
      path.join(args.artifactRoot, 'staff-legacy-browser-results.json'),
      JSON.stringify(report, null, 2),
      'utf8'
    );
  } finally {
    await browser.close();
  }
}

main().catch(error => {
  console.error(`Legacy staff browser acceptance failed: ${error.stack || error.message || 'unknown failure'}`);
  process.exitCode = 1;
});
