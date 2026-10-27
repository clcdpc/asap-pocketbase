'use strict';
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { Blocked, safeError } = require('./reporting.cjs');

async function openReviewerBrowser(origin, timeoutMs = 300000, signal) {
  const { chromium } = require('playwright');
  const profile = await fs.mkdtemp(path.join(os.tmpdir(), 'asap-acceptance-'));
  let context;
  const abort = () => { context?.close().catch(() => {}); };
  try {
    if (signal?.aborted) throw new Blocked('Acceptance run was cancelled before authentication.');
    context = await chromium.launchPersistentContext(profile, {
      channel: 'chromium', headless: false, viewport: { width: 1280, height: 900 },
      serviceWorkers: 'block',
      args: [
        '--disable-extensions-except=' + path.resolve(__dirname, '../../browser/native-zoom-extension'),
        '--load-extension=' + path.resolve(__dirname, '../../browser/native-zoom-extension')
      ]
    });
    context.setDefaultTimeout(15000);
    signal?.addEventListener('abort', abort, { once: true });
    const page = context.pages()[0] || await context.newPage();
    // No trace, screenshots, request logging, storage export or console capture during Entra/MFA.
    console.log('Complete normal Entra sign-in (including MFA) in Chromium. Authentication is not recorded.');
    await page.goto(origin + '/staff/');
    const deadline = Date.now() + timeoutMs;
    let session;
    while (Date.now() < deadline && !page.isClosed() && !signal?.aborted) {
      try {
        if (new URL(page.url()).origin === origin) {
          const response = await context.request.get(origin + '/api/asap/staff/session', { maxRedirects: 0 });
          if (response.ok()) {
            const current = await response.json();
            if (current.authenticated && current.accessAllowed) { session = current; break; }
          }
        }
      } catch { /* A normal login navigation can temporarily retire the page. */ }
      await new Promise(resolve => setTimeout(resolve, 750));
    }
    if (!session) throw new Blocked('Entra sign-in timed out, was cancelled or access was denied.');
    if (session.staff?.role !== 'super_admin' || !session.staff.id || !session.staff.tenantId) {
      throw new Blocked('pr410 requires an active super-admin reviewer with access to Review Library (2).');
    }
    const response = await context.request.get(origin + '/api/asap/staff/title-requests?scope=2&status=suggestion', { maxRedirects: 0 });
    const queue = response.ok() ? await response.json() : null;
    if (queue?.scope !== '2') throw new Blocked('Authenticated reviewer cannot access Review Library (2).');
    await page.locator('#workspace').waitFor({ state: 'visible' });
    return { context, page, role: 'super_admin', library: 2, close: async () => {
      signal?.removeEventListener('abort', abort);
      await context.close();
      await fs.rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
    } };
  } catch (error) {
    signal?.removeEventListener('abort', abort);
    await context?.close().catch(() => {});
    await fs.rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
    if (error instanceof Blocked) throw error;
    throw new Blocked('Interactive Chromium/authentication unavailable. ' + safeError(error));
  }
}
module.exports = { openReviewerBrowser };
