'use strict';
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { Blocked, safeError } = require('./reporting.cjs');

async function verifyStaffSession(context, origin, expected) {
  const response = await context.request.get(origin + '/api/asap/staff/session', { maxRedirects: 0 });
  const session = response.ok() ? await response.json() : null;
  if (!session?.authenticated || !session.accessAllowed || session.staff?.role !== 'super_admin' ||
      session.staff.organizationId !== 1 || !session.staff.id || !session.staff.tenantId ||
      (expected && (session.staff.id !== expected.staff.id || session.staff.tenantId !== expected.staff.tenantId ||
        session.staff.authenticationEmail !== expected.staff.authenticationEmail))) {
    throw new Blocked('Authenticated reviewer identity or authorization changed; mutations are blocked.');
  }
  const queueResponse = await context.request.get(origin + '/api/asap/staff/title-requests?scope=2&status=suggestion', { maxRedirects: 0 });
  const queue = queueResponse.ok() ? await queueResponse.json() : null;
  if (queue?.scope !== '2') throw new Blocked('Authenticated reviewer cannot access Review Library (2).');
  return session;
}

async function authenticateLocal(context, origin) {
  let credential;
  try { credential = await fs.readFile('/review/auth/credential', 'utf8'); }
  catch { throw new Blocked('Local review authentication credential is unavailable.'); }
  if (!/^[a-f0-9]{64}$/.test(credential)) throw new Blocked('Local review authentication credential is malformed.');
  try {
    // APIRequestContext shares the context's normal cookie jar. The credential
    // is never installed as an extra header or passed into browser JavaScript.
    const response = await context.request.post(origin + '/_review/auth/session', {
      headers: { 'X-ASAP-Review-Auth': credential }, maxRedirects: 0, timeout: 25000
    });
    if (response.status() !== 204) throw new Blocked('Protected local review authentication failed.');
  } finally { credential = undefined; }
  const cookies = (await context.cookies(origin)).filter(cookie => cookie.name === '__Host-ASAP.Staff');
  if (cookies.length !== 1 || !cookies[0].httpOnly || !cookies[0].secure || cookies[0].path !== '/' ||
      cookies[0].sameSite !== 'Lax' || cookies[0].expires !== -1) {
    throw new Blocked('Local review did not establish the normal ephemeral staff cookie.');
  }
}

async function openReviewerBrowser(origin, signal) {
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
    if (signal?.aborted) throw new Blocked('Acceptance run was cancelled during browser startup.');
    signal?.addEventListener('abort', abort, { once: true });
    const page = context.pages()[0] || await context.newPage();
    // Block external traffic and page mutations until the scenario guard
    // takes over. The trusted API bootstrap follows no redirects and installs
    // no identity headers on ordinary requests.
    await context.route('**/*', route => new URL(route.request().url()).origin === origin &&
      ['GET', 'HEAD', 'OPTIONS'].includes(route.request().method()) ? route.continue() : route.abort('blockedbyclient'));
    await authenticateLocal(context, origin);
    await page.goto(origin + '/staff/');
    const session = await verifyStaffSession(context, origin);
    if (session.staff.authenticationEmail !== 'REVIEWER@EXAMPLE.ORG') {
      throw new Blocked('Local review authenticated an unexpected identity.');
    }
    await page.locator('#workspace').waitFor({ state: 'visible' });
    const verifyReviewer = () => verifyStaffSession(context, origin, session);
    return { context, page, mode: 'local', role: 'super_admin', library: 2,
      verifyReviewer, close: async () => {
      signal?.removeEventListener('abort', abort);
      try { await context.close(); }
      finally { await fs.rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }); }
    } };
  } catch (error) {
    signal?.removeEventListener('abort', abort);
    await context?.close().catch(() => {});
    await fs.rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
    if (signal?.aborted) throw new Blocked('Acceptance run was cancelled during authentication.');
    if (error instanceof Blocked) throw error;
    throw new Blocked('Chromium/authentication unavailable. ' + safeError(error));
  }
}
module.exports = { openReviewerBrowser, authenticateLocal, verifyStaffSession };
