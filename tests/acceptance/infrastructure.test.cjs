'use strict';
// Explicit development checks for runner plumbing, NOT browser acceptance.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const http = require('node:http');
const { execFile } = require('node:child_process');
const { promisify } = require('node:util');
const { randomUUID } = require('node:crypto');
const { nativeZoom, checkpoint } = require('./helpers/accessibility.cjs');
const repository = path.resolve(__dirname, '../..');

test('review HostingStartup loads through additional deps without changing Web deps', async t => {
  const target = path.join(repository, '.artifacts', 'acceptance-infrastructure', randomUUID());
  const web = path.join(repository, 'src/Asap.Web/bin/Release/net10.0');
  const providers = path.join(repository, 'dev/review/src/Asap.ReviewProviders/bin/Release/net10.0');
  await fs.cp(web, target, { recursive: true });
  t.after(() => fs.rm(target, { recursive: true, force: true }));
  await fs.copyFile(path.join(providers, 'Asap.ReviewProviders.dll'), path.join(target, 'Asap.ReviewProviders.dll'));
  let output = '', exit;
  try {
    await promisify(execFile)('dotnet', [path.join(target, 'Asap.Web.dll'), '--Asap:ConfigFile=' + path.join(target, 'absent.json')],
      { cwd: target, timeout: 15000, windowsHide: true, maxBuffer: 1024 * 1024,
        env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Testing', ASPNETCORE_URLS: 'http://127.0.0.1:0',
          Testing__UseDeterministicPatronProvider: 'true', ASPNETCORE_HOSTINGSTARTUPASSEMBLIES: 'Asap.ReviewProviders',
          DOTNET_ADDITIONAL_DEPS: path.join(providers, 'Asap.ReviewProviders.deps.json') } });
  } catch (error) { output = String(error.stdout) + String(error.stderr); exit = error.code; }
  // Missing app config intentionally omits application services. Reaching this
  // specific activation error proves the review startup assembly actually loaded.
  assert.ok(Number.isInteger(exit) && exit !== 0, 'Missing configuration terminates the probe');
  assert.match(output, /Unable to resolve service for type 'Asap.Web.Infrastructure.Testing.DeterministicTestingPatronProvider'/);
  assert.match(output, /Asap.ReviewProviders.ReviewFixtures/);
  assert.doesNotMatch(output, /Could not load file or assembly 'Asap.ReviewProviders/);
});

test('native Chromium 200% helper and accessibility screenshot plumbing run with an ephemeral profile', async t => {
  const { chromium } = require('playwright');
  const server = http.createServer((_request, response) => {
    response.setHeader('Content-Type', 'text/html');
    response.end('<!doctype html><html lang="en"><title>Runner plumbing fixture</title><body><main id="workspace"><h1>Runner plumbing fixture</h1><p>Synthetic content only. This is not application acceptance.</p></main></body></html>');
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const profile = await fs.mkdtemp(path.join(os.tmpdir(), 'asap-acceptance-plumbing-'));
  let context;
  t.after(async () => {
    await context?.close();
    await fs.rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
    server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
  });
  const extension = path.join(repository, 'tests/browser/native-zoom-extension');
  context = await chromium.launchPersistentContext(profile, {
    channel: 'chromium', headless: true, viewport: { width: 1280, height: 900 }, serviceWorkers: 'block',
    args: ['--disable-extensions-except=' + extension, '--load-extension=' + extension]
  });
  context.setDefaultTimeout(15000);
  const page = context.pages()[0];
  await page.goto('http://127.0.0.1:' + server.address().port);
  const directory = path.join(repository, '.artifacts', 'acceptance-infrastructure', randomUUID());
  await fs.mkdir(directory, { recursive: true });
  await nativeZoom(context, page, 2);
  await checkpoint(page, directory, 'native-zoom-plumbing-only');
  assert.equal(await page.evaluate(() => innerWidth), 640);
});
