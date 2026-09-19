const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { URL } = require('node:url');

function parseArguments(argv) {
  if (argv.length !== 19) {
    throw new Error('Usage: node tests/browser/legacy-links.cjs <baseURL> <artifactDirectory> <superId> <tenantId> <superEmail> <staffId> <staffEmail> <sharedLegacyId> <numericLegacyId> <sharedTitleId> <sharedCopyId> <numericTitleId> <numericCopyId> <missingTitleId> <deletedTitleId> <outOfScopeTitleId> <missingCopyId> <deletedCopyId> <outOfScopeCopyId>');
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
    sharedLegacyId: argv[7],
    numericLegacyId: argv[8],
    sharedTitleId: argv[9],
    sharedCopyId: argv[10],
    numericTitleId: argv[11],
    numericCopyId: argv[12],
    missingTitleId: argv[13],
    deletedTitleId: argv[14],
    outOfScopeTitleId: argv[15],
    missingCopyId: argv[16],
    deletedCopyId: argv[17],
    outOfScopeCopyId: argv[18]
  };
}

function sameOrigin(url, origin) {
  try { return new URL(url).origin === origin; } catch { return false; }
}

async function createContext(browser, viewport, baseOrigin, identity) {
  const context = await browser.newContext({
    viewport,
    extraHTTPHeaders: {
      'X-ASAP-Test-Staff-Id': identity.staffId,
      'X-ASAP-Test-Tenant-Id': identity.tenantId,
      'X-ASAP-Test-Staff-Email': identity.email
    }
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
  await page.screenshot({ path: path.join(artifactRoot, `${viewport}-${state}.png`), fullPage: true });
  report.states.push({ viewport, state, accessibility, layout });
  assert.equal(
    accessibility.length,
    0,
    `Serious or critical accessibility violation in ${viewport}/${state}: ${JSON.stringify(accessibility.map(item => ({ id: item.id, targets: item.nodes.map(node => node.target) })))}`
  );
  assert.ok(layout.scrollWidth <= layout.width, `Horizontal document overflow in ${viewport}/${state}: ${JSON.stringify(layout)}`);
  assert.equal(layout.visibleImagesLoaded, true, `Visible image failed in ${viewport}/${state}`);
}

function deepLink(origin, stage, legacyId, marker) {
  const url = new URL('/staff/', origin);
  if (stage) url.searchParams.set(stage.name, stage.value);
  url.searchParams.set('request', legacyId);
  url.searchParams.set('marker', marker);
  url.hash = 'details';
  return url.href;
}

async function openAndAssert(page, args, type, viewport, legacyId, targetId, stage, state, axeSource, report) {
  await page.goto(
    deepLink(args.baseOrigin, stage, legacyId, `${type}-${viewport}`),
    { waitUntil: 'networkidle' });
  await page.locator('#app-container').waitFor({ state: 'visible' });
  try {
    await page.locator('#editModal[open]').waitFor({ timeout: 10000 });
  } catch (error) {
    const diagnostics = await page.evaluate(() => ({
      job: document.getElementById('job-msg')?.textContent || '',
      announcement: document.getElementById('status-announcer')?.textContent || '',
      appVisible: !document.getElementById('app-container')?.classList.contains('hidden'),
      apiResources: performance.getEntriesByType('resource')
        .map(entry => entry.name)
        .filter(name => name.includes('/api/asap/'))
    }));
    throw new Error(`Legacy request dialog did not open: ${JSON.stringify(diagnostics)} at ${page.url()}`);
  }
  assert.equal(await page.locator('#edit-id').inputValue(), targetId);
  const normalized = new URL(page.url());
  assert.equal(normalized.searchParams.get('request'), targetId);
  assert.equal(normalized.searchParams.get('marker'), `${type}-${viewport}`);
  assert.equal(normalized.hash, '#details');
  assert.equal(
    normalized.searchParams.get(stage.name),
    stage.value,
    `supported ${stage.name} parameter was lost for ${type}/${viewport}`);
  await scan(page, axeSource, args.artifactRoot, report, viewport, state);
  await page.locator('#close-modal-x').click();
  await page.locator('#editModal').waitFor({ state: 'hidden' });
}

async function assertUnavailable(page, args, type, viewport, legacyId, stage, state, axeSource, report) {
  await page.goto(
    deepLink(args.baseOrigin, stage, legacyId, `${type}-failure-${state}`),
    { waitUntil: 'networkidle' });
  await page.locator('#app-container').waitFor({ state: 'visible' });
  const message = type === 'title'
    ? 'That request is no longer available.'
    : 'That additional-copy task is no longer available.';
  await page.locator('#job-msg').getByText(message, { exact: true }).waitFor();
  assert.equal(await page.locator('#editModal[open]').count(), 0);
  const current = new URL(page.url());
  assert.equal(current.searchParams.get('request'), legacyId);
  assert.equal(current.searchParams.get(stage.name), stage.value);
  assert.equal(current.searchParams.get('marker'), `${type}-failure-${state}`);
  await scan(page, axeSource, args.artifactRoot, report, viewport, `${type}-failure-${state}`);
}

async function loadDependencies() {
  let chromium;
  try { ({ chromium } = require('playwright')); } catch {
    throw new Error('Missing local Playwright.');
  }
  let axePath;
  try { axePath = require.resolve('axe-core/axe.min.js'); } catch {
    throw new Error('Missing local axe-core.');
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
    for (const [viewport, size] of [
      ['desktop', { width: 1280, height: 900 }],
      ['mobile', { width: 390, height: 844 }]
    ]) {
      const { context, traffic } = await createContext(browser, size, args.baseOrigin, args.superIdentity);
      const page = await context.newPage();
      const errors = [];
      page.on('pageerror', error => errors.push(error.message));
      try {
        await openAndAssert(
          page, args, 'title', viewport, args.sharedLegacyId, args.sharedTitleId,
          { name: 'status', value: 'submitted' }, `${viewport}-title-shared`, axeSource, report);
        await openAndAssert(
          page, args, 'title', viewport, args.numericLegacyId, args.numericTitleId,
          { name: 'stage', value: 'new' }, `${viewport}-title-numeric`, axeSource, report);
        await openAndAssert(
          page, args, 'title', viewport, args.sharedTitleId, args.sharedTitleId,
          { name: 'status', value: 'submitted' }, `${viewport}-title-current`, axeSource, report);
        await openAndAssert(
          page, args, 'copy', viewport, args.sharedLegacyId, args.sharedCopyId,
          { name: 'stage', value: 'additional_copies' }, `${viewport}-copy-shared`, axeSource, report);
        await openAndAssert(
          page, args, 'copy', viewport, args.numericLegacyId, args.numericCopyId,
          { name: 'stage', value: 'additional_copies' }, `${viewport}-copy-numeric`, axeSource, report);
        await openAndAssert(
          page, args, 'copy', viewport, args.sharedCopyId, args.sharedCopyId,
          { name: 'stage', value: 'additional_copies' }, `${viewport}-copy-current`, axeSource, report);
        assert.deepEqual(errors, [], `${viewport} legacy-link success flow raised a browser error`);
        assert.equal(traffic.externalRequests, 0, `${viewport} legacy-link success flow requested an external asset`);
      } finally {
        await context.close();
      }
    }

    const { context, traffic } = await createContext(
      browser,
      { width: 390, height: 844 },
      args.baseOrigin,
      args.staffIdentity);
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    try {
      for (const [type, ids, stage] of [
        ['title', [args.missingTitleId, args.deletedTitleId, args.outOfScopeTitleId], { name: 'status', value: 'submitted' }],
        ['copy', [args.missingCopyId, args.deletedCopyId, args.outOfScopeCopyId], { name: 'stage', value: 'additional_copies' }]
      ]) {
        for (const [index, legacyId] of ids.entries()) {
          await assertUnavailable(page, args, type, 'mobile', legacyId, stage, `${type}-${index}`, axeSource, report);
        }
      }
      assert.deepEqual(errors, [], 'Legacy-link failure flow raised a browser error');
      assert.equal(traffic.externalRequests, 0, 'Legacy-link failure flow requested an external asset');
    } finally {
      await context.close();
    }

    await fs.writeFile(
      path.join(args.artifactRoot, 'legacy-links-results.json'),
      JSON.stringify(report, null, 2),
      'utf8');
  } finally {
    await browser.close();
  }
}

main().catch(error => {
  console.error(`Legacy-link browser acceptance failed: ${error.stack || error.message || 'unknown failure'}`);
  process.exitCode = 1;
});
