'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { URL } = require('node:url');
const {
  LEGACY_ACCESSIBILITY_BASELINE,
  unexpectedLegacyAccessibilityFindings
} = require('./legacy-accessibility-baseline.cjs');

function parseArguments(argv) {
  if (argv.length !== 26) {
    throw new Error('Usage: node tests/browser/staff-legacy.cjs <baseURL> <artifactDirectory> <superId> <tenantId> <superEmail> <staffId> <staffEmail> <legacyRequestId> <primaryRequestId> <blockedRequestId> <resolutionRequestId> <otherRequestId> <copySourceRequestId> <invalidClosedCopyId> <invalidClaimantId> <legacyRuleId> <mobileCopyId> <foreignStaffId> <invalidTenantStaffId> <unboundStaffId> <staleTitleAId> <staleTitleBId> <staleCopyAId> <staleCopyBId> <staleCreateSourceId> <collidingRequestId>');
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
    staleTitleBId: argv[21],
    collidingRequestId: argv[25]
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
  // Report accessibility findings after all journeys so one inherited defect cannot
  // prevent verification of the remaining compatibility workflows.
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

async function runLegacySettingsNoEditRoundTrip(
  page,
  context,
  baseOrigin,
  orgId,
  expectedSuggestionLimit,
  expectedSuggestionLimitInput = String(expectedSuggestionLimit)
) {
  const select = page.locator('#select-library-context');
  if (await select.inputValue() !== orgId) {
    await select.selectOption(orgId, { force: true });
  }
  await page.waitForFunction(async ({ expectedOrgId, expectedInput }) => {
    const { currentLibraryContextOrgId } = await import('/staff/js/state.js');
    const { currentLegacySettingsFormModel } = await import('/staff/js/state.js');
    return currentLibraryContextOrgId === expectedOrgId &&
      currentLegacySettingsFormModel?.contextOrgId === expectedOrgId &&
      document.getElementById('suggestion-limit')?.value === expectedInput;
  }, { expectedOrgId: orgId, expectedInput: expectedSuggestionLimitInput });

  const beforeResponse = await context.request.get(
    `${baseOrigin}/api/asap/staff/settings/library?orgId=${encodeURIComponent(orgId)}`
  );
  assert.equal(beforeResponse.status(), 200);
  const before = await beforeResponse.json();
  assert.equal(String(before.workflow.suggestionLimit), String(expectedSuggestionLimit));
  const organizationsBeforeResponse = await context.request.get(`${baseOrigin}/api/asap/staff/organizations`);
  assert.equal(organizationsBeforeResponse.status(), 200);
  const participationBefore = ((await organizationsBeforeResponse.json()).data || [])
    .filter(item => String(item.id) !== '1' && (item.isActive === true || item.active === true))
    .map(item => String(item.id))
    .sort();
  if (orgId === 'system') {
    try {
      await page.waitForFunction(expectedIds => {
        const container = document.getElementById('enabled-libraries-checkbox-container');
        if (container?.getAttribute('data-loaded') !== 'true') return false;
        const enabledIds = Array.from(container.querySelectorAll('.lib-participation-cb:checked'))
          .map(item => item.value)
          .sort();
        return JSON.stringify(enabledIds) === JSON.stringify(expectedIds);
      }, participationBefore, { timeout: 5000 });
    } catch (error) {
      const state = await page.evaluate(() => {
        const container = document.getElementById('enabled-libraries-checkbox-container');
        return {
          loaded: container?.getAttribute('data-loaded'),
          activeIds: container?.getAttribute('data-active-organizations'),
          rendered: container?.getAttribute('data-rendered-participation'),
          checkboxes: Array.from(container?.querySelectorAll('.lib-participation-cb') || [])
            .map(item => ({ id: item.value, checked: item.checked }))
        };
      });
      throw new Error(`Organization participation controls did not settle for ${orgId}: ${JSON.stringify({
        expected: participationBefore,
        state,
        cause: error.message
      })}`);
    }
  }

  const serialized = await page.evaluate(async () => {
    const { buildSettingsPayload } = await import('/staff/js/settings/serialize-save.js');
    return buildSettingsPayload();
  });
  if (orgId === 'system') {
    const participationDom = await page.evaluate(() => Array.from(
      document.querySelectorAll('#enabled-libraries-checkbox-container .lib-participation-cb')
    ).map(item => ({ id: item.value, checked: item.checked })));
    const serializedContext = await page.evaluate(async () => {
      const state = await import('/staff/js/state.js');
      return {
        currentLibraryContextOrgId: state.currentLibraryContextOrgId,
        modelContextOrgId: state.currentLegacySettingsFormModel?.contextOrgId
      };
    });
    assert.deepEqual((serialized.enabledLibraryOrgIds || []).map(String).sort(), participationBefore,
      `System no-edit form must serialize the actual enabled organization IDs from the .NET DTO: ${JSON.stringify({
        dom: participationDom,
        serializedContext,
        serializedHasPolaris: Object.hasOwn(serialized, 'polaris'),
        serialized: serialized.enabledLibraryOrgIds
      })}`);
    const systemSettings = before.stored.systemSettings;
    const actualSystemStaffUrl = await page.locator('#system-staff-url').inputValue();
    const actualSystemNotEnabledMessage = await page.locator('#ui-system-not-enabled-msg').inputValue();
    const actualMisconfiguredMessage = await page.locator('#ui-misconfigured-msg').inputValue();
    assert.equal(actualSystemStaffUrl, systemSettings.staffUrl,
      `system staff URL must be populated from the real Settings DTO: ${JSON.stringify({ actualSystemStaffUrl, expected: systemSettings.staffUrl })}`);
    assert.equal(actualSystemNotEnabledMessage, systemSettings.systemNotEnabledMessage,
      `system participation text must be populated from the real Settings DTO: ${JSON.stringify({ actualSystemNotEnabledMessage, expected: systemSettings.systemNotEnabledMessage })}`);
    assert.equal(actualMisconfiguredMessage, systemSettings.misconfiguredMessage,
      `system error text must be populated from the real Settings DTO: ${JSON.stringify({ actualMisconfiguredMessage, expected: systemSettings.misconfiguredMessage })}`);
    assert.equal(serialized.staffUrl, actualSystemStaffUrl);
    assert.equal(serialized.ui_text.systemNotEnabledMessage, systemSettings.systemNotEnabledMessage);
    assert.equal(serialized.ui_text.misconfiguredMessage, systemSettings.misconfiguredMessage);
  }
  const posted = {
    ...serialized,
    orgId,
    version: before.version
  };
  if (orgId === 'system' || orgId === '92327') {
    const nullSystemWorkflowFields = Object.entries(before.stored.configuredSystem.workflow || {})
      .filter(([key, value]) => key !== 'version' && value === null)
      .map(([key]) => key);
    for (const key of nullSystemWorkflowFields) {
      assert.equal(Object.hasOwn(posted.workflow || {}, key), false,
        orgId + ' no-edit save must not materialize the form or serializer fallback for null system workflow.' + key);
    }
  }
  const saveResponse = await post(context, baseOrigin, '/api/asap/staff/settings/library', posted);
  assert.equal(saveResponse.status(), 200, `${orgId} no-edit Settings save failed: ${await saveResponse.text()}`);
  assert.equal(posted.orgId, orgId);
  assert.equal(posted.version, before.version);
  const standardTemplateKeys = ['suggestion_submitted', 'purchase_approved', 'already_owned', 'rejected', 'hold_placed'];
  assert.deepEqual(Object.keys(posted.emails || {}).filter(key => standardTemplateKeys.includes(key) || key === 'rejection_templates'), [],
    `${orgId} no-edit save must not create or rewrite backend email templates from JavaScript defaults`);

  if (orgId === 'system') {
    assert.equal(posted.polaris.systemPolarisUserId, 731);
    assert.equal(posted.polaris.organizationIdForRequests, 910);
    assert.equal(posted.polaris.pickupOrganizationId, 4201);
    assert.equal(Object.hasOwn(posted.polaris, 'userId'), false);
    assert.equal(Object.hasOwn(posted.polaris, 'requestingOrgId'), false);
    assert.equal(Object.hasOwn(posted.polaris, 'pickupOrgId'), false);
  }

  const afterResponse = await context.request.get(
    `${baseOrigin}/api/asap/staff/settings/library?orgId=${encodeURIComponent(orgId)}`
  );
  assert.equal(afterResponse.status(), 200);
  const after = await afterResponse.json();
  const organizationsAfterResponse = await context.request.get(`${baseOrigin}/api/asap/staff/organizations`);
  assert.equal(organizationsAfterResponse.status(), 200);
  const participationAfter = ((await organizationsAfterResponse.json()).data || [])
    .filter(item => String(item.id) !== '1' && (item.isActive === true || item.active === true))
    .map(item => String(item.id))
    .sort();
  assert.deepEqual(participationAfter, participationBefore,
    `No-edit Settings save changed organization participation for ${orgId}: ${JSON.stringify({
      before: participationBefore,
      after: participationAfter,
      submitted: posted.enabledLibraryOrgIds
    })}`);
  const changedStoredKeys = changedPaths(before.stored, after.stored);
  assert.deepEqual(after.stored, before.stored,
    `${orgId} no-edit save changed persisted Settings state: ${changedStoredKeys.join(', ')}; templates=${JSON.stringify({
      emailKeys: Object.keys(posted.emails || {}),
      before: (before.stored.templates || []).map(({ id, organizationId, templateKey, sourceTemplateId, isCustom }) =>
        ({ id, organizationId, templateKey, sourceTemplateId, isCustom })),
      after: (after.stored.templates || []).map(({ id, organizationId, templateKey, sourceTemplateId, isCustom }) =>
        ({ id, organizationId, templateKey, sourceTemplateId, isCustom }))
    })}`);

  if (orgId === '92327') {
    assert.equal(before.stored.libraryOverride.workflow, null,
      'The zero-override library must inherit the system workflow without a local row.');
  }
  if (orgId === '92328') {
    assert.equal(before.stored.libraryOverride.workflow.suggestionLimit, 73);
    assert.equal(before.stored.libraryOverride.workflow.outstandingTimeoutDays, null,
      'The sparse library fixture must contain exactly one workflow scalar override.');
  }

  return {
    orgId,
    suggestionLimit: expectedSuggestionLimit,
    postedWorkflowKeys: Object.keys(posted.workflow || {}).sort(),
    persistedSnapshotUnchanged: true
  };
}

function changedPaths(before, after, prefix = '') {
  if (before && after && typeof before === 'object' && typeof after === 'object') {
    if (Array.isArray(before) !== Array.isArray(after)) return [prefix || '$'];
    if (Array.isArray(before) && before.length !== after.length) return [prefix || '$'];
    const keys = new Set([...Object.keys(before), ...Object.keys(after)]);
    return [...keys].flatMap(key => changedPaths(before[key], after[key], prefix ? `${prefix}.${key}` : key));
  }
  return JSON.stringify(before) === JSON.stringify(after) ? [] : [prefix || '$'];
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

    await page.locator('#edit-title-polaris-search').click();
    await page.locator('#polarisSearchDialog[open]').waitFor();
    await page.locator('.polaris-search-result-title-text').getByText('Catalog title 9001', { exact: true }).waitFor();
    for (const mode of ['identifier', 'title', 'author', 'title_author']) {
      await page.locator('#polaris-search-mode').selectOption(mode);
      await page.locator('#polaris-search-input').fill(mode === 'identifier' ? '9780000000001' : 'Catalog title');
      if (mode === 'title_author') await page.locator('#polaris-search-author').fill('Catalog author');
      const search = page.waitForResponse(response => response.url().endsWith('/staff/bib-lookup') &&
        response.request().postDataJSON()?.mode === mode && !response.request().postDataJSON()?.bibId);
      await page.locator('#polaris-search-rerun-btn').click();
      assert.equal((await search).status(), 200);
      await page.locator('.polaris-search-result-meta').getByText(/Publication: 2020.*Format: Book.*Identifier: 9780000000001/).waitFor();
    }
    await page.getByText('Owned by you (1)', { exact: true }).waitFor();
    assert.equal(await page.locator('#polaris-additional-copy-action').count(), 1,
      'The inherited additional-copy action must expose its stable accessibility identity');
    await scan(page, axeSource, args.artifactRoot, report, 'desktop', 'polaris-search');
    report.polarisSearchModes = ['identifier', 'title', 'author', 'title_author'];
    await page.locator('#close-polaris-search-btn').click();
    await page.locator('#editModal[open]').waitFor();

    await page.locator('#close-modal-x').click();
    await page.locator('#editModal').waitFor({ state: 'hidden' });
    const patronLookupResponse = await post(
      context,
      args.baseOrigin,
      '/api/asap/staff/patron-lookup',
      { query: '20000000000001', libraryOrgId: '2' }
    );
    assert.equal(patronLookupResponse.status(), 200);
    const patronLookup = await patronLookupResponse.json();
    assert.equal(patronLookup.barcode, '20000000000001');
    assert.ok(patronLookup.pickupBranches.length > 0);
    report.patronLookup = { status: patronLookupResponse.status(), barcode: patronLookup.barcode };
    // Load this fixture library's publication options through the existing settings context.
    await page.locator('[data-status="settings"]').click();
    await page.waitForFunction(() => {
      const select = document.getElementById('select-library-context');
      return select && !select.disabled && select.options.length > 1;
    });
    report.settingsNoEditRoundTrips = [];
    for (const [orgId, suggestionLimit, suggestionLimitInput] of [
      ['system', 5, ''],
      ['92327', 5, ''],
      ['92328', 73, '73']
    ]) {
      report.settingsNoEditRoundTrips.push(
        await runLegacySettingsNoEditRoundTrip(page, context, args.baseOrigin, orgId, suggestionLimit, suggestionLimitInput)
      );
    }
    await page.locator('#select-library-context').selectOption('2', { force: true });
    await page.waitForFunction(() => [...document.getElementById('new-publication').options]
      .some(option => option.value === 'Library early release'));
    await page.locator('[data-status="suggestion"]').click();
    await page.locator('#btn-new-suggestion').click();
    await page.locator('#newSuggestionModal[open]').waitFor();
    await page.locator('#new-barcode').fill('Test Patron');
    const newPatronLookup = page.waitForResponse(response =>
      response.url().endsWith('/api/asap/staff/patron-lookup') && response.request().method() === 'POST');
    await page.locator('#btn-lookup-patron').click();
    const newPatronResponse = await newPatronLookup;
    assert.equal(newPatronResponse.status(), 200, await newPatronResponse.text());
    await page.waitForFunction(() => document.getElementById('new-barcode').value === '20000000000001');
    await page.locator('#new-barcode').fill('Multiple Patrons');
    await page.locator('#btn-lookup-patron').click();
    await page.locator('#patronSearchDialog[open]').waitFor();
    await page.locator('#patron-search-results button').first().click();
    await page.locator('#newSuggestionModal[open]').waitFor();
    await page.locator('#new-title').fill('Staff port browser submission');
    await page.locator('#new-author').fill('Browser author');
    await page.locator('#new-identifier').fill('9780000000881');
    await page.locator('#new-suggestion-library').selectOption('2');
    // Book has a required custom-field fixture; the pinned staff form has no custom-field editor.
    await page.locator('#new-format').selectOption('dvd');
    await page.locator('#new-publication').selectOption('Library early release');
    await page.locator('#new-pickup-branch').selectOption('101');
    const createdSuggestion = page.waitForResponse(response => response.url().endsWith('/staff/suggestions'));
    await page.locator('#btn-submit-new').click();
    const created = await createdSuggestion;
    assert.equal(created.status(), 201, await created.text());
    await page.locator('#newSuggestionModal').waitFor({ state: 'hidden' });
    const tabs = page.locator('#status-tabs [data-status]');
    assert.ok(await tabs.count() >= 8, 'Legacy status/settings navigation was not rendered');
    await page.locator('#grid-search-input').fill('no-result-browser-filter');
    await page.getByText('No matching records found', { exact: false }).waitFor();
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
    await page.locator('#editModal').waitFor({ state: 'hidden' });
    const openRowMenu = async () => {
      const marker = page.locator(`[data-suggestion-id="${args.staleTitleBId}"]`).first();
      const row = marker.locator('xpath=ancestor::tr');
      await row.waitFor();
      await row.locator('.row-action-menu-trigger').evaluate(button => button.click());
      return page.evaluate(() => Array.from(document.querySelectorAll('#action-menu-layer [role="menuitem"]'))
        .map(button => button.textContent?.trim() || ''));
    };
    const menuActions = await openRowMenu();
    if (menuActions.includes('Claim')) {
      await page.evaluate(() => Array.from(document.querySelectorAll('#action-menu-layer [role="menuitem"]'))
        .find(button => button.textContent?.trim() === 'Claim')?.click());
      await page.getByText('Request claimed.', { exact: true }).waitFor();
      if (await page.locator('#editModal[open]').count()) {
        await page.locator('#close-modal-x').click();
        await page.locator('#editModal').waitFor({ state: 'hidden' });
      }
      const refreshedActions = await openRowMenu();
      assert.ok(refreshedActions.includes('Unclaim'), `Expected Unclaim after claiming, saw ${refreshedActions.join(', ')}`);
      await page.evaluate(() => Array.from(document.querySelectorAll('#action-menu-layer [role="menuitem"]'))
        .find(button => button.textContent?.trim() === 'Unclaim')?.click());
      await page.getByText('Request unclaimed.', { exact: true }).waitFor();
    } else if (menuActions.includes('Unclaim')) {
      await page.evaluate(() => Array.from(document.querySelectorAll('#action-menu-layer [role="menuitem"]'))
        .find(button => button.textContent?.trim() === 'Unclaim')?.click());
      await page.getByText('Request unclaimed.', { exact: true }).waitFor();
    } else {
      throw new Error(`Expected a claim action for request ${args.staleTitleBId}, saw ${menuActions.join(', ')}`);
    }
    report.concurrency = { staleStatus: staleResponse.status() };

    await page.goto(`${args.baseOrigin}/staff/?stage=additional_copies&request=${args.mobileCopyId}`, { waitUntil: 'networkidle' });
    await page.locator('#editModal[open]').waitFor();
    assert.equal(await page.locator('#edit-id').inputValue(), args.mobileCopyId);
    assert.match(await page.locator('#editModalLabel').textContent(), /additional-copy|edit/i);
    await scan(page, axeSource, args.artifactRoot, report, 'desktop', 'additional-copy-detail');
    await page.locator('#close-modal-x').click();

    const collisionUrl = requestType =>
      `${args.baseOrigin}/staff/?stage=closed&request=${args.collidingRequestId}&requestType=${requestType}`;
    await page.goto(collisionUrl('title_request'), { waitUntil: 'networkidle' });
    await page.locator('#editModal[open]').waitFor();
    assert.equal(await page.locator('#edit-title').inputValue(), 'Browser collision title');
    assert.equal(await page.locator('#edit-id').getAttribute('data-request-type'), 'title_request');
    await page.locator('#close-modal-x').click();

    await page.goto(collisionUrl('additional_copy'), { waitUntil: 'networkidle' });
    await page.locator('#editModal[open]').waitFor();
    assert.equal(await page.locator('#edit-title').inputValue(), 'Browser collision additional copy');
    assert.equal(await page.locator('#edit-id').getAttribute('data-request-type'), 'additional_copy');
    const exactBibResponse = page.waitForResponse(response => {
      if (!response.url().endsWith('/staff/bib-lookup')) return false;
      const body = response.request().postDataJSON();
      return body?.bibId === '92921';
    });
    await page.locator('#btn-bib-lookup').click();
    const exactBib = await exactBibResponse;
    assert.equal(exactBib.status(), 200, await exactBib.text());
    assert.deepEqual(
      {
        requestType: exactBib.request().postDataJSON().requestType,
        requestId: exactBib.request().postDataJSON().requestId
      },
      { requestType: 'additional_copy', requestId: args.collidingRequestId }
    );
    await page.locator('#close-modal-x').click();
    await page.locator('#grid-search-input').fill('Browser collision');

    const collisionRow = type => page
      .locator(`[data-suggestion-id="${args.collidingRequestId}"][data-request-type="${type}"]`)
      .locator('xpath=ancestor::tr');
    const titleCollisionRow = collisionRow('title_request');
    const copyCollisionRow = collisionRow('additional_copy');
    await page.waitForTimeout(250);
    const collisionMarkers = await page.evaluate(id =>
      [...document.querySelectorAll('[data-suggestion-id]')]
        .filter(element => element.getAttribute('data-suggestion-id') === id)
        .map(element => ({ type: element.getAttribute('data-request-type'), html: element.outerHTML })),
      args.collidingRequestId);
    assert.deepEqual([...new Set(collisionMarkers.map(marker => marker.type))].sort(), ['additional_copy', 'title_request'],
      `Expected both colliding rows in the closed grid: ${JSON.stringify(collisionMarkers)}`);

    await titleCollisionRow.locator('.staff-title-main').click();
    await page.locator('#editModal[open]').waitFor();
    assert.equal(await page.locator('#edit-title').inputValue(), 'Browser collision title');
    await page.locator('#close-modal-x').click();
    await copyCollisionRow.locator('.staff-title-main').click();
    await page.locator('#editModal[open]').waitFor();
    assert.equal(await page.locator('#edit-title').inputValue(), 'Browser collision additional copy');
    await page.locator('#close-modal-x').click();

    await titleCollisionRow.locator('.truncate-note').click();
    await page.locator('#noteDialog[open]').waitFor();
    assert.match(await page.locator('#noteDialogContent').textContent(), /Title collision note/);
    assert.doesNotMatch(await page.locator('#noteDialogContent').textContent(), /Additional-copy collision note/);
    await page.locator('#noteDialogCloseBtn').click();
    await copyCollisionRow.locator('.truncate-note').click();
    await page.locator('#noteDialog[open]').waitFor();
    assert.match(await page.locator('#noteDialogContent').textContent(), /Additional-copy collision note/);
    assert.doesNotMatch(await page.locator('#noteDialogContent').textContent(), /Title collision note/);
    await page.locator('#noteDialogCloseBtn').click();

    const assertPolarisRowIdentity = async (row, requestType) => {
      const requestPromise = page.waitForRequest(request => {
        if (!request.url().endsWith('/staff/bib-lookup')) return false;
        const body = request.postDataJSON();
        return body?.mode === 'title' && !body?.bibId && body?.requestId === args.collidingRequestId;
      });
      await row.locator('[data-polaris-search-mode="title"]').click();
      const request = await requestPromise;
      assert.equal(request.postDataJSON().requestType, requestType);
      await page.locator('#polarisSearchDialog[open]').waitFor();
      await page.locator('#close-polaris-search-btn').click();
    };
    await assertPolarisRowIdentity(titleCollisionRow, 'title_request');
    await assertPolarisRowIdentity(copyCollisionRow, 'additional_copy');
    await page.locator('#grid-search-input').fill('');
    report.requestIdentityCollision = {
      rowClicks: true,
      deepLinks: true,
      notes: true,
      polarisSearches: true,
      additionalCopyBibLookup: true
    };

    await page.locator('[data-status="settings"]').click();
    await page.locator('#settings-container').waitFor({ state: 'visible' });
    await page.locator('#tab-staff').click();
    await page.locator('#settings-staff.active').waitFor();
    await scan(page, axeSource, args.artifactRoot, report, 'desktop', 'settings-staff-access');
    await page.waitForFunction(() => {
      const select = document.getElementById('select-library-context');
      return select && !select.disabled && select.options.length > 1;
    });
    await page.locator('#select-library-context').selectOption('2', { force: true });
    await page.locator('#library-context-display').getByText(/ID 2/).waitFor();
    assert.ok(await page.locator('#btn-reset-library-settings').count(), 'Inherited override reset control is missing');
    assert.ok(await page.locator('#settings-form button[type="submit"]').count(), 'Settings save control is missing');

    await page.locator('#tab-smtp').click();
    await page.locator('#settings-smtp.active').waitFor();
    assert.equal(await page.locator('#smtp-host, #smtp-port, #smtp-password').count(), 0);
    const beforeEmailSettings = await (await context.request.get(`${args.baseOrigin}/api/asap/staff/settings?orgId=2`)).json();
    const saveEmail = async () => {
      const saved = page.waitForResponse(response => response.url().endsWith('/staff/settings/library') && response.request().method() === 'POST');
      await page.locator('#settings-form button[type="submit"]').click();
      assert.equal((await saved).status(), 200);
      await page.waitForFunction(() => document.getElementById('postmark-token').value === '' &&
        document.querySelector('#settings-form button[type="submit"]').disabled === false);
    };
    await page.locator('#postmark-token').fill('browser-postmark-token');
    await page.locator('#smtp-from').fill('browser-port@example.org');
    await page.locator('#smtp-from-name').fill('Browser sender');
    await saveEmail();
    await page.locator('#postmark-token-status').waitFor({ state: 'visible' });
    await page.locator('#smtp-readiness-message').getByText(/configuration is complete/).waitFor();
    await scan(page, axeSource, args.artifactRoot, report, 'desktop', 'postmark-settings');
    await page.locator('#smtp-from-name').fill('Updated browser sender');
    await saveEmail();
    await page.locator('#postmark-token-status').waitFor({ state: 'visible' });
    await page.locator('#postmark-token').fill('replacement-browser-token');
    await saveEmail();
    await page.locator('#postmark-clear-token').focus();
    await page.locator('#postmark-clear-token').press('Space');
    assert.equal(await page.locator('#postmark-clear-token').isChecked(), true);
    await saveEmail();
    assert.equal(await page.locator('#postmark-token').inputValue(), '');
    const afterEmailSettings = await (await context.request.get(`${args.baseOrigin}/api/asap/staff/settings?orgId=2`)).json();
    assert.deepEqual(afterEmailSettings.stored.libraryOverride.publicationOptions,
      beforeEmailSettings.stored.libraryOverride.publicationOptions, 'Email saves must preserve library publication choices');
    assert.deepEqual(afterEmailSettings.stored.libraryOverride.workflow,
      beforeEmailSettings.stored.libraryOverride.workflow, 'Email saves must preserve workflow settings');

    await page.locator('[data-status="analytics"]').click();
    await page.locator('#analytics-title').waitFor();
    assert.equal(await page.locator('#analytics-scope').inputValue(), 'all');
    await scan(page, axeSource, args.artifactRoot, report, 'desktop', 'analytics');

    await page.locator('#profile-btn').click();
    await page.locator('#profile-dialog[open]').waitFor();
    await page.locator('#profile-weekly-action-summary-email').waitFor();
    await scan(page, axeSource, args.artifactRoot, report, 'desktop', 'profile');
    await page.locator('#profile-save').click();
    await page.getByText('Profile preferences saved.', { exact: true }).first().waitFor();
    await page.locator('#profile-dialog').waitFor({ state: 'hidden' });

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
    await page.locator('#job-msg').getByText('That request is no longer available.', { exact: true }).waitFor();
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
    assert.equal(report.states.length, 11, 'Expected eleven primary legacy staff browser states');
    report.legacyAccessibilityBaseline = LEGACY_ACCESSIBILITY_BASELINE;
    report.unexpectedAccessibility = unexpectedLegacyAccessibilityFindings(report.states);
    await fs.writeFile(
      path.join(args.artifactRoot, 'staff-legacy-browser-results.json'),
      JSON.stringify(report, null, 2),
      'utf8'
    );
    assert.deepEqual(
      report.unexpectedAccessibility,
      [],
      `Unexpected serious or critical accessibility violations: ${JSON.stringify(report.unexpectedAccessibility)}`
    );
  } finally {
    await browser.close();
  }
}

main().catch(error => {
  console.error(`Legacy staff browser acceptance failed: ${error.stack || error.message || 'unknown failure'}`);
  process.exitCode = 1;
});
