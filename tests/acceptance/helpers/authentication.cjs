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

async function authenticateEntra(context, page, origin, timeoutMs, signal) {
  let oidcCallback;
  const callback = response => {
    const url = new URL(response.url());
    if (url.origin === origin && url.pathname === '/signin-oidc' && response.request().method() === 'POST') {
      oidcCallback = { method: 'POST', path: '/signin-oidc', status: response.status() };
    }
  };
  // Record only the local callback's method/path/status. Never capture Entra
  // URLs, headers, bodies, screenshots, console, traces or storage during MFA.
  page.on('response', callback);
  console.log('Complete normal Entra sign-in (including MFA) in Chromium. Authentication is not recorded.');
  try {
    await page.goto(origin + '/staff/');
    const deadline = Date.now() + timeoutMs;
    let authenticated = false;
    while (Date.now() < deadline && !page.isClosed() && !signal?.aborted) {
      try {
        if (new URL(page.url()).origin === origin) {
          const response = await context.request.get(origin + '/api/asap/staff/session', { maxRedirects: 0 });
          const session = response.ok() ? await response.json() : null;
          if (session?.authenticated && session.accessAllowed) { authenticated = true; break; }
        }
      } catch { /* Login navigation can temporarily retire the page. */ }
      await new Promise(resolve => setTimeout(resolve, 750));
    }
    if (!authenticated) throw new Blocked('Entra sign-in timed out, was cancelled or access was denied. Check the registration Web redirect URI https://localhost:8080/signin-oidc and enable ID tokens; no client secret is required.');
    if (!oidcCallback || ![302, 303].includes(oidcCallback.status)) {
      throw new Blocked('The real Entra form POST callback at https://localhost:8080/signin-oidc was not verified.');
    }
    return oidcCallback;
  } finally { page.off('response', callback); }
}

async function openReviewerBrowser(origin, timeoutMs = 300000, signal, mode = 'local') {
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
    let oidcCallback;
    if (mode === 'local') {
      // Block external traffic and page mutations until the scenario guard
      // takes over. The trusted API bootstrap bypasses browser routing, follows
      // no redirects, and installs no identity headers on ordinary requests.
      await context.route('**/*', route => new URL(route.request().url()).origin === origin &&
        ['GET', 'HEAD', 'OPTIONS'].includes(route.request().method()) ? route.continue() : route.abort('blockedbyclient'));
      await authenticateLocal(context, origin);
      await page.goto(origin + '/staff/');
    } else if (mode === 'entra') oidcCallback = await authenticateEntra(context, page, origin, timeoutMs, signal);
    else throw new Blocked('Unknown authentication mode.');
    const session = await verifyStaffSession(context, origin);
    if (mode === 'local' && session.staff.authenticationEmail !== 'REVIEWER@EXAMPLE.ORG') {
      throw new Blocked('Local review authenticated an unexpected identity.');
    }
    await page.locator('#workspace').waitFor({ state: 'visible' });
    const verifyReviewer = () => verifyStaffSession(context, origin, session);
    return { context, page, mode, role: 'super_admin', library: 2,
      ...(mode === 'entra' ? { oidcCallback } : {}), verifyReviewer, close: async () => {
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
module.exports = { openReviewerBrowser, authenticateLocal, authenticateEntra, verifyStaffSession };
