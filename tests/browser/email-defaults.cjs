'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');

async function main() {
  const [baseURL, artifactDirectory, library, staffId, tenantId, email] = process.argv.slice(2);
  assert.ok(email, 'Usage: email-defaults.cjs baseURL artifacts library staff tenant email');
  const origin = new URL(baseURL).origin;
  assert.equal(baseURL, origin);
  const artifacts = path.resolve(artifactDirectory);
  const expected = JSON.parse(await fs.readFile(path.join(artifacts, 'expected.json'), 'utf8'));
  const axe = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  const browser = await chromium.launch({ headless: true,
    ...(process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH
      ? { executablePath: process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH } : {}) });
  const report = { externalRequests: [], pageErrors: [], scans: [], saves: 0 };
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, extraHTTPHeaders: {
    'X-ASAP-Test-Staff-Id': staffId, 'X-ASAP-Test-Tenant-Id': tenantId, 'X-ASAP-Test-Staff-Email': email
  } });
  await context.route('**/*', route => {
    if (new URL(route.request().url()).origin === origin) return route.continue();
    report.externalRequests.push(route.request().url());
    return route.abort('blockedbyclient');
  });
  const page = await context.newPage();
  page.on('pageerror', error => report.pageErrors.push(error.message));
  const field = key => page.locator(`[data-template-key="${key}"]`);
  const snapshot = scope => page.evaluate(async scope => {
    const response = await fetch(`/api/asap/staff/settings?orgId=${scope}`);
    assertResponse(response);
    return response.json();
    function assertResponse(response) { if (!response.ok) throw new Error(`Settings GET ${response.status}`); }
  }, scope);
  async function open(scope) {
    await page.goto(`${origin}/staff/?stage=settings&settingsScope=${scope}`, { waitUntil: 'networkidle' });
    await page.locator('#settings-form').waitFor({ state: 'visible' });
    assert.equal(await page.locator('#settings-scope').inputValue(), scope);
    await page.locator('#settings-nav-templates').click();
  }
  async function save() {
    const responsePromise = page.waitForResponse(response => response.request().method() === 'POST' &&
      new URL(response.url()).pathname === '/api/asap/staff/settings');
    await page.locator('#settings-save').click();
    const response = await responsePromise;
    assert.equal(response.status(), 200, await response.text());
    await page.locator('#settings-message').filter({ hasText: /^Settings saved\./ }).waitFor();
    report.saves++;
  }
  async function scan(name) {
    await page.evaluate(axe);
    const violations = await page.evaluate(async () => (await axe.run()).violations
      .filter(item => ['serious', 'critical'].includes(item.impact)));
    const layout = await page.evaluate(() => ({ width: innerWidth, scrollWidth: document.documentElement.scrollWidth,
      imagesLoaded: [...document.images].filter(image => {
        const bounds = image.getBoundingClientRect();
        return bounds.width > 0 && bounds.height > 0;
      }).every(image => image.complete && image.naturalWidth > 0) }));
    report.scans.push({ name, violations, layout });
    await page.screenshot({ path: path.join(artifacts, `${name}.png`), fullPage: true });
    assert.deepEqual(violations, [], name);
    assert.ok(layout.scrollWidth <= layout.width, `Horizontal overflow: ${name}`);
    assert.equal(layout.imagesLoaded, true, `Missing image: ${name}`);
  }
  try {
    await open('system');
    for (const item of expected) {
      assert.equal(await field(item.key).locator('input[type=text]').inputValue(), item.subject);
      assert.equal(await field(item.key).locator('textarea').inputValue(), item.body);
      await field(item.key).locator('input[type=text]').fill(item.subject + ' {{barcode}}');
      await field(item.key).locator('textarea').fill(item.body + '\n\n{{firstName}} {{lastName}}');
    }
    await save();
    await open('system');
    for (const item of expected) {
      assert.equal(await field(item.key).locator('input[type=text]').inputValue(), item.subject + ' {{barcode}}');
      assert.equal(await field(item.key).locator('textarea').inputValue(), item.body + '\n\n{{firstName}} {{lastName}}');
    }
    await scan('system-desktop');
    await open(library);
    for (const item of expected) {
      assert.equal(await field(item.key).locator('input[type=text]').inputValue(), item.subject + ' {{barcode}}');
      assert.equal(await field(item.key).locator('textarea').inputValue(), item.body + '\n\n{{firstName}} {{lastName}}');
      assert.equal(await field(item.key).locator('textarea').isDisabled(), true);
    }
    await field('suggestion_submitted').locator('.settings-template-override').check();
    await field('suggestion_submitted').locator('textarea').fill('Library receipt {{title}} for {{name}}');
    await save();
    const overridden = (await snapshot(library)).stored.templates.find(row => String(row.organizationId) === library);
    assert.ok(overridden);
    assert.equal(overridden.subject, null);
    assert.equal(overridden.body, 'Library receipt {{title}} for {{name}}');
    await open('system');
    await field('suggestion_submitted').locator('input[type=text]').fill(expected[0].subject + ' {{barcode}} updated');
    await save();
    await open(library);
    assert.equal(await field('suggestion_submitted').locator('input[type=text]').inputValue(),
      expected[0].subject + ' {{barcode}} updated');
    assert.equal(await field('suggestion_submitted').locator('textarea').inputValue(), 'Library receipt {{title}} for {{name}}');
    const inherited = (await snapshot(library)).stored.templates.find(row => String(row.organizationId) === library);
    assert.deepEqual(inherited, overridden, 'A System edit must preserve the library override identity, version and sparse content.');
    await scan('library-desktop');
    await page.setViewportSize({ width: 390, height: 844 });
    await scan('library-mobile');
    assert.deepEqual(report.externalRequests, []);
    assert.deepEqual(report.pageErrors, []);
    assert.equal(report.saves, 3);
  } finally {
    await fs.writeFile(path.join(artifacts, 'email-defaults-report.json'), JSON.stringify(report, null, 2));
    await context.close();
    await browser.close();
  }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
