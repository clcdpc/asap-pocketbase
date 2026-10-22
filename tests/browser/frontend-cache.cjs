'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');
const [baseURL, artifacts, webRoot, previousURL] = process.argv.slice(2);

(async () => {
  const browser = await chromium.launch({ headless: true,
    ...(process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH ? { executablePath: process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH } : {}) });
  const report = { externalRequests: [], pageErrors: [], states: [], network: [] };
  const axe = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  const write = async (relative, content, age) => {
    const file = path.join(webRoot, relative);
    await fs.mkdir(path.dirname(file), { recursive: true });
    await fs.writeFile(file, content);
    // Heuristically fresh responses reproduce the former cache contract. Distinct
    // deployment mtimes also exercise ASP.NET Core's standard ETag/date validators.
    const modified = new Date(Date.now() - age * 86400000);
    await fs.utimes(file, modified, modified);
  };
  try {
    for (const ui of ['staff', 'patron']) {
      const folder = `${ui}/cache-fixture`;
      const deploy = async (version, nestedOnly = false) => {
        const age = { A: 3, B: 2, C: 1 }[version];
        if (!nestedOnly) {
          const prefix = ui === 'patron' ? 'cache-fixture/' : '';
          await write(`${folder}/index.html`, `<!doctype html><html lang="en"><head><meta charset="utf-8">
            ${ui === 'patron' ? '<base href="/patron/">' : ''}<title>Cache regression</title>
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <link rel="stylesheet" href="${prefix}styles.css?v=${version}"></head>
            <body><main><h1>Cache regression ${version}</h1><p id="executed" role="status"></p>
            <label>Unsaved work <input id="draft"></label><img src="/jpl.png" alt="" width="32" height="32"></main>
            <script src="/vendor/gridjs/6.2.0/gridjs.umd.js"></script>
            <script type="module" src="${prefix}app.js?v=${version}"></script></body></html>`, age);
          await write(`${folder}/app.js`, `import { nested } from './js/nested.js';
            import { shared } from '/shared/cache-fixture.js';
            document.getElementById('executed').textContent = 'entry-${version}|' + nested + '|' + shared;`, age);
          await write(`${folder}/styles.css`, `body { --deployed-version: ${version}; }`, age);
        }
        await write(`${folder}/js/nested.js`, `export const nested = 'nested-${version}';`, age);
      };
      await write('shared/cache-fixture.js', "export const shared = 'shared-A';", 4);
      await deploy('A');
      const context = await browser.newContext({ viewport: { width: 390, height: 800 } });
      const control = await browser.newContext();
      // Do not use context.route/page.route: Playwright routing disables HTTP caching.
      for (const current of [context, control]) {
        current.on('request', request => {
          if (![new URL(baseURL).origin, new URL(previousURL).origin].includes(new URL(request.url()).origin)) {
            report.externalRequests.push(request.url());
          }
        });
        current.on('page', page => page.on('pageerror', error => report.pageErrors.push(error.message)));
      }
      const page = await context.newPage();
      const oldPage = await control.newPage();
      const cdp = await context.newCDPSession(page);
      await cdp.send('Network.enable');
      let phase = `${ui}-A`;
      const requests = new Map();
      const record = id => {
        if (!requests.has(id)) {
          const item = { phase };
          requests.set(id, item);
          report.network.push(item);
        }
        return requests.get(id);
      };
      // CDP extra-info events can precede their response event. Enrich the same
      // record when its URL arrives so real 304s cannot lose their resource name.
      cdp.on('Network.requestWillBeSent', event => Object.assign(record(event.requestId), { phase, url: new URL(event.request.url).pathname }));
      cdp.on('Network.responseReceivedExtraInfo', event => { record(event.requestId).status = event.statusCode; });
      cdp.on('Network.requestServedFromCache', event => { record(event.requestId).cache = true; });
      cdp.on('Network.responseReceived', event => {
        Object.assign(record(event.requestId), { url: new URL(event.response.url).pathname, disk: event.response.fromDiskCache });
      });
      const visit = async (target, origin) => {
        await target.goto('about:blank');
        await target.goto(`${origin}/${folder}/`);
        await target.waitForFunction(() => document.querySelector('#executed')?.textContent);
      };
      const scan = async version => {
        await page.evaluate(axe);
        const state = await page.evaluate(async () => ({
          executed: document.querySelector('#executed').textContent,
          css: getComputedStyle(document.body).getPropertyValue('--deployed-version').trim(),
          violations: (await axe.run()).violations.filter(item => ['serious', 'critical'].includes(item.impact)),
          overflow: document.documentElement.scrollWidth > innerWidth,
          missingImages: [...document.images].some(image => !image.complete || !image.naturalWidth),
          vendor: performance.getEntriesByType('resource').filter(item => item.name.includes('/vendor/'))
            .map(item => ({ transfer: item.transferSize, body: item.encodedBodySize }))
        }));
        assert.deepEqual(state.violations, []);
        assert.equal(state.overflow, false);
        assert.equal(state.missingImages, false);
        report.states.push({ ui, version, ...state });
        return state;
      };
      await visit(page, baseURL);
      await visit(oldPage, previousURL);
      assert.equal((await scan('A')).executed, 'entry-A|nested-A|shared-A');
      phase = `${ui}-warm`;
      await visit(page, baseURL);
      await visit(oldPage, previousURL);
      await page.locator('#draft').fill('keep my work');
      await deploy('B');
      assert.equal(await page.locator('#executed').textContent(), 'entry-A|nested-A|shared-A');
      assert.equal(await page.locator('#draft').inputValue(), 'keep my work');

      phase = `${ui}-B`;
      await visit(page, baseURL);
      await visit(oldPage, previousURL);
      const stale = await oldPage.locator('#executed').textContent();
      assert.match(stale, /nested-A/, 'negative control must reproduce stale frontend content');
      report.states.push({ ui, previousContractAfterDeployment: stale });
      // Fetch fresh HTML and its new entry URL while retaining the old module cache.
      await oldPage.goto(`${previousURL}/${folder}/?deployment=B`);
      await oldPage.waitForFunction(() => document.querySelector('#executed')?.textContent);
      assert.equal(await oldPage.locator('#executed').textContent(), 'entry-B|nested-A|shared-A', 'entry-only versioning leaves nested modules stale');
      const changed = await scan('B');
      assert.equal(changed.executed, 'entry-B|nested-B|shared-A');
      assert.equal(changed.css, 'B');
      assert.equal(changed.vendor.length, 1);
      assert.equal(changed.vendor[0].transfer, 0, 'immutable vendor response should be reused from cache');
      assert.ok(changed.vendor[0].body > 0);
      assert.equal(await page.locator('h1').textContent(), 'Cache regression B');

      await deploy('C', true);
      phase = `${ui}-C`;
      await page.reload(); // Ordinary browser reload; no cache clearing or bypass flag.
      await page.waitForFunction(() => document.querySelector('#executed')?.textContent === 'entry-B|nested-C|shared-A');
      assert.equal((await scan('C')).executed, 'entry-B|nested-C|shared-A');
      for (const suffix of ['/app.js', '/js/nested.js', '/styles.css', '/index.html']) {
        const url = suffix === '/index.html' ? `/${folder}/` : `/${folder}${suffix}`;
        assert.ok(report.network.some(item => item.phase === `${ui}-warm` && item.url === url && item.status === 304), `unchanged ${url} must revalidate without retransferring its body`);
      }
      assert.ok(report.network.some(item => item.phase === `${ui}-B` && item.url === `/${folder}/js/nested.js` && item.status === 200));
      assert.ok(report.network.some(item => item.phase === `${ui}-C` && item.url === `/${folder}/app.js` && item.status === 304));
      await context.close();
      await control.close();
    }
    assert.deepEqual(report.externalRequests, []);
    assert.deepEqual(report.pageErrors, []);
  } finally {
    await fs.writeFile(path.join(artifacts, 'browser-results.json'), JSON.stringify(report, null, 2));
    await browser.close();
  }
  console.log('Old cache contract reproduced stale modules; published staff/patron navigation and reload refreshed entry, nested modules and CSS with real 304/cache reuse.');
})().catch(error => { console.error(error); process.exitCode = 1; });
