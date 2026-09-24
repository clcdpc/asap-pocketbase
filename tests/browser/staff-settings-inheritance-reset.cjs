'use strict';

const assert = require('node:assert/strict');
const path = require('node:path');
const { URL } = require('node:url');

function parseArguments(argv) {
  if (argv.length !== 5) {
    throw new Error('Usage: node staff-settings-inheritance-reset.cjs <baseURL> <artifactDirectory> <superId> <tenantId> <superEmail>');
  }
  const parsed = new URL(argv[0]);
  if (!['http:', 'https:'].includes(parsed.protocol) || parsed.pathname !== '/' || parsed.username || parsed.password || parsed.search || parsed.hash) {
    throw new Error('baseURL must be an absolute origin without a path or credentials.');
  }
  return {
    baseOrigin: parsed.origin,
    artifactRoot: path.resolve(argv[1]),
    identity: { staffId: argv[2], tenantId: argv[3], email: argv[4] }
  };
}

async function main() {
  const args = parseArguments(process.argv.slice(2));
  let chromium;
  try {
    ({ chromium } = require('playwright'));
  } catch {
    throw new Error('Missing local Playwright. Run npm install with playwright@1.62.1.');
  }
  const executablePath = process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH;
  const browser = await chromium.launch({ headless: true, ...(executablePath ? { executablePath } : {}) });
  const context = await browser.newContext({
    viewport: { width: 1280, height: 900 },
    extraHTTPHeaders: {
      'X-ASAP-Test-Staff-Id': args.identity.staffId,
      'X-ASAP-Test-Tenant-Id': args.identity.tenantId,
      'X-ASAP-Test-Staff-Email': args.identity.email
    }
  });
  const externalRequests = [];
  await context.route('**/*', route => {
    if (new URL(route.request().url()).origin === args.baseOrigin) return route.continue();
    externalRequests.push(route.request().url());
    return route.abort('blockedbyclient');
  });
  const page = await context.newPage();
  const pageErrors = [];
  page.on('pageerror', error => pageErrors.push(error.message));
  try {
    await page.goto(`${args.baseOrigin}/staff/`, { waitUntil: 'networkidle' });
    await page.locator('#app-container').waitFor({ state: 'visible' });
    await page.locator('[data-status="settings"]').click();
    const selector = page.locator('#select-library-context');
    await page.waitForFunction(() => {
      const select = document.getElementById('select-library-context');
      return select && !select.disabled && [...select.options].some(option => option.value === '92329');
    });
    await selector.selectOption('92329', { force: true });
    await page.waitForFunction(async () => {
      const state = await import('/staff/js/state.js');
      return document.getElementById('ui-ebook-msg')?.value === 'Library eBook override before reset' &&
        document.getElementById('ui-eaudiobook-msg')?.value === 'Library eAudiobook override before reset' &&
        state.currentLibraryContextOrgId === '92329' &&
        state.currentLegacySettingsFormModel?.contextOrgId === '92329';
    });

    const beforeResponse = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/settings/library?orgId=92329`
    );
    assert.equal(beforeResponse.status(), 200, await beforeResponse.text());
    const before = await beforeResponse.json();
    for (const [key, expected] of Object.entries({
      commonAuthorsLabel: 'Library creators override before reset',
      commonAuthorsHelp: 'Library creator help override before reset'
    })) {
      assert.equal(before.stored.libraryOverride.workflow[key], expected);
    }
    assert.equal(before.stored.libraryOverride.patron.ebookMessage, 'Library eBook override before reset');
    assert.equal(before.stored.libraryOverride.patron.eaudiobookMessage, 'Library eAudiobook override before reset');

    await page.locator('#tab-patron').click();
    await page.locator('#accordion-patron-popular-creators > .asap-accordion-header').click();
    await page.locator('#wf-common-authors-label').fill('');
    await page.locator('#wf-common-authors-help').fill('');
    await page.locator('#accordion-patron-suggestion-messages > .asap-accordion-header').click();
    await page.locator('#ui-ebook-msg').fill('');
    await page.locator('#ui-eaudiobook-msg').fill('Library eAudiobook override intentionally edited');
    const currentSerializerState = await page.evaluate(async () => {
      const state = await import('/staff/js/state.js');
      const { buildSettingsPayload } = await import('/staff/js/settings/serialize-save.js');
      return {
        ebookValue: document.getElementById('ui-ebook-msg').value,
        ebookOverride: state.currentLegacySettingsFormModel?.provenance?.patronOverride?.ebookMessage,
        payload: buildSettingsPayload()
      };
    });
    assert.equal(currentSerializerState.ebookValue, '');
    assert.equal(currentSerializerState.payload.ui_text.ebookMessage, '',
      `The actual DOM serializer must preserve the cleared message: ${JSON.stringify(currentSerializerState)}`);
    const postResponse = page.waitForResponse(response =>
      response.url().endsWith('/api/asap/staff/settings/library') && response.request().method() === 'POST');
    await page.locator('#settings-save-btn').click();
    const saved = await postResponse;
    assert.equal(saved.status(), 200, await saved.text());
    const posted = saved.request().postDataJSON();
    assert.equal(posted.orgId, '92329');
    for (const key of ['commonAuthorsLabel', 'commonAuthorsHelp']) {
      assert.equal(posted.workflow[key], '', `The actual Settings POST must preserve cleared workflow.${key}.`);
    }
    assert.equal(posted.ui_text.ebookMessage, '', 'The actual Settings POST must preserve the cleared eBook message.');
    assert.equal(posted.ui_text.eaudiobookMessage, 'Library eAudiobook override intentionally edited');

    const afterResponse = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/settings/library?orgId=92329`
    );
    assert.equal(afterResponse.status(), 200, await afterResponse.text());
    const after = await afterResponse.json();
    assert.equal(after.stored.libraryOverride.workflow, null,
      'Clearing the only local workflow values must remove the library row and restore inheritance.');
    assert.equal(after.workflow.commonAuthorsLabel, 'System creators inherited after reset');
    assert.equal(after.workflow.commonAuthorsHelp, 'System creator help inherited after reset');
    assert.equal(after.workflow.patronCodeEligibilityMessage, 'System patron-code message inherited after reset');
    assert.equal(after.stored.libraryOverride.patron.ebookMessage, null,
      'Clearing eBook text must remove that patron override.');
    assert.equal(after.ui_text.ebookMessage, 'Closure ebook message',
      'The eBook message must resolve from the system after the library override is cleared.');
    assert.equal(after.stored.libraryOverride.patron.eaudiobookMessage,
      'Library eAudiobook override intentionally edited');
    assert.deepEqual(externalRequests, [], 'The settings flow must not request external resources.');
    assert.deepEqual(pageErrors, [], `The staff page raised an error: ${pageErrors.join('; ')}`);

    await page.waitForFunction(async () => {
      const state = await import('/staff/js/state.js');
      return state.currentLegacySettingsFormModel?.contextOrgId === '92329' &&
        document.getElementById('ui-ebook-msg')?.value === 'Closure ebook message' &&
        document.getElementById('ui-eaudiobook-msg')?.value === 'Library eAudiobook override intentionally edited';
    });
    console.log(JSON.stringify({
      orgId: '92329',
      clearedWorkflow: Object.keys(before.stored.libraryOverride.workflow),
      workflowRowRemoved: after.stored.libraryOverride.workflow === null,
      clearedEbookInherited: after.ui_text.ebookMessage,
      editedEaudiobook: after.ui_text.eaudiobookMessage
    }));
  } finally {
    await context.close();
    await browser.close();
  }
}

main().catch(error => {
  console.error(`Settings inheritance reset browser acceptance failed: ${error.stack || error.message || 'unknown failure'}`);
  process.exitCode = 1;
});
