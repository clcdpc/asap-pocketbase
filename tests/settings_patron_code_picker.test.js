const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');

const reply = (status, body) => ({ ok: status < 400, status, statusText: 'Test response', json: async () => body });
const flush = () => new Promise(resolve => setImmediate(resolve));
async function until(check) {
  for (let attempt = 0; attempt < 60 && !check(); attempt++) await flush();
  assert.ok(check(), 'Expected controller operation to complete');
}

(async () => {
  const frontend = path.join(__dirname, '../src/Asap.Web/Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-code-picker-controller-'));
  let controller, dom;
  try {
    for (const folder of ['staff', 'shared']) fs.cpSync(path.join(frontend, folder), path.join(temporary, folder), { recursive: true });
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');
    const { createSettingsController } = await import(pathToFileURL(path.join(temporary, 'staff/js/settings.js')).href);
    const http = await import(pathToFileURL(path.join(temporary, 'staff/js/http.js')).href);
    dom = new JSDOM(fs.readFileSync(path.join(frontend, 'staff/index.html'), 'utf8'), { url: 'https://localhost/staff/' });
    Object.assign(global, { window: dom.window, document: dom.window.document, Node: dom.window.Node,
      Event: dom.window.Event, FormData: dom.window.FormData });
    require('./helpers/confirmation.cjs').answerConfirmations(dom.window, () => true);
    const actor = { id: '1', tenantId: 'tenant', objectId: 'object', role: 'super_admin', organizationId: 1 };
    const sets = { system: [1, 77], 2: [2], 3: [] }, versions = { system: 1, 2: 1, 3: 1 };
    const choices = [{ id: 1, description: 'Adult' }, { id: 2, description: 'Branch' }, { id: 3, description: 'eCard' }];
    const posts = [];
    let failCatalog = false, conflict = false, deferCatalog, lateCatalog, lateSignal, invalidations = 0;
    http.onSessionInvalid(() => invalidations++);
    function snapshot(scope) {
      const empty = { exists: false, values: [] };
      const configured = { workflow: {}, patron: {}, email: {}, publicationOptions: empty, commonCreators: empty,
        allowedPatronCodeIds: { exists: true, values: sets.system }, providers: [], formats: [], templates: [],
        branding: { hasLogo: false, altText: null } };
      const library = scope === 'system' ? null : { ...configured,
        allowedPatronCodeIds: { exists: sets[scope] !== null, values: sets[scope] || [] } };
      return JSON.parse(JSON.stringify({ orgId: scope, version: scope + '-v' + versions[scope],
        organization: scope === 'system' ? null : { id: Number(scope), name: 'Library ' + scope, active: true },
        stored: { systemSettings: { enabledLibraryOrgIds: [2, 3], libraryOrgIds: [2, 3] }, polaris: {},
          configuredSystem: configured, libraryOverride: library, workflow: {}, patron: {}, email: {},
          origins: [], formats: [], providers: [], customFields: [], formatRules: [], templates: [], autoClaimRules: [],
          branding: { hasLogo: false, altText: null } },
        effective: { allowedPatronCodeIds: sets[scope] ?? sets.system, publicationOptions: [], commonCreators: [],
          externalSearchProviders: [], formats: [], customFields: [], email: {} },
        workflow: {}, ui_text: {}, emails: { templates: [] }, autoClaimStaff: [] }));
    }
    global.fetch = async (url, options = {}) => {
      const request = new URL(url, 'https://localhost');
      if (request.pathname.endsWith('/session')) return reply(200, { authenticated: true, antiforgeryToken: 'test' });
      if (request.pathname.endsWith('/organizations')) return reply(200, { data: [
        { id: 1, name: 'System', abbreviation: null, version: 'org-1', parentOrganizationId: null, isActive: true, organizationCodeId: 1 },
        ...[2, 3].map(id => ({ id, name: 'Library ' + id, abbreviation: null, version: 'org-' + id,
          parentOrganizationId: 1, isActive: true, organizationCodeId: 2 }))
      ] });
      if (request.pathname.endsWith('/patron-codes')) {
        if (deferCatalog) {
          deferCatalog = false; lateSignal = options.signal;
          return new Promise(resolve => { lateCatalog = resolve; });
        }
        return failCatalog ? reply(503, { message: 'Catalog unavailable' }) : reply(200, { data: choices });
      }
      if (request.pathname.endsWith('/settings') && options.method === 'POST') {
        const body = JSON.parse(options.body); posts.push(body);
        if (conflict) return reply(409, { code: 'stale_version', message: 'Settings changed. Reload.' });
        if (Object.hasOwn(body.workflow, 'allowedPatronCodeIds')) sets[body.orgId] = body.workflow.allowedPatronCodeIds;
        versions[body.orgId]++;
        return reply(200, { code: 'saved', data: { orgId: body.orgId, version: body.orgId + '-v' + versions[body.orgId] } });
      }
      if (request.pathname.endsWith('/settings')) return reply(200, snapshot(request.searchParams.get('orgId')));
      throw new Error('Unexpected request: ' + url);
    };
    controller = createSettingsController({ root: document.getElementById('settings-view'),
      tab: document.getElementById('settings-view-tab'), getStaff: () => actor, announce() {},
      prepareDeparture: async () => ({ revalidate: async () => true, commit: () => true }) });
    controller.bind(); controller.setStaff(actor); await controller.activate();
    assert.equal(document.getElementById('settings-form').hidden, false, document.getElementById('settings-message').textContent);
    const input = document.getElementById('patron-code-picker');
    const selected = () => [...document.querySelectorAll('#patron-codes-editor [data-code-id]')].map(n => n.dataset.codeId);
    const search = text => { input.value = text; input.dispatchEvent(new window.Event('input', { bubbles: true })); };
    const add = text => { search(text); document.querySelector('#patron-code-options [role="option"]').click(); };
    const remove = id => document.querySelector(`#patron-codes-editor [data-code-id="${id}"] button`).click();
    const scope = async value => {
      document.getElementById('settings-scope').value = value;
      document.getElementById('settings-scope').dispatchEvent(new window.Event('change', { bubbles: true }));
      await until(() => document.getElementById('settings-version').value === value + '-v' + versions[value]);
    };
    const save = async () => {
      const previous = posts.length;
      document.getElementById('settings-form').dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
      await until(() => posts.length > previous);
      await until(() => !controller.hasPendingMutation());
      return posts.at(-1);
    };
    search('branch');
    assert.equal(controller.isDirty(), false);
    assert.equal(document.getElementById('settings-save').disabled, true);
    remove('1'); add('2');
    assert.equal(controller.isDirty(), true, 'Changing IDs at the same cardinality is a real draft');
    assert.deepEqual((await save()).workflow.allowedPatronCodeIds, [77, 2]);
    assert.deepEqual(selected(), ['77', '2']);
    assert.equal(controller.isDirty(), false, 'Authoritative reload establishes the new baseline');
    await scope('2');
    assert.deepEqual(selected(), ['2']);
    remove('2');
    assert.deepEqual((await save()).workflow.allowedPatronCodeIds, []);
    assert.equal(document.getElementById('patron-codes-use-system').checked, false);
    document.getElementById('patron-codes-use-system').click();
    assert.equal((await save()).workflow.allowedPatronCodeIds, null);
    assert.equal(document.getElementById('patron-codes-use-system').checked, true);
    assert.deepEqual(selected(), ['77', '2']);
    await scope('3');
    assert.deepEqual(selected(), [], 'Explicit empty IDs in another library do not inherit');
    add('1');
    document.getElementById('settings-discard').click();
    await until(() => !controller.isDirty());
    assert.deepEqual(selected(), []);
    add('3'); conflict = true;
    assert.deepEqual((await save()).workflow.allowedPatronCodeIds, [3]);
    assert.deepEqual(selected(), [], 'Version conflict retains the existing authoritative reload behavior');
    assert.equal(controller.isDirty(), false);
    conflict = false;
    await scope('system');
    assert.deepEqual(selected(), ['77', '2']);
    document.getElementById('patron-code-eligibility-message').value = 'Unrelated text';
    document.getElementById('patron-code-eligibility-message').dispatchEvent(new window.Event('input', { bubbles: true }));
    assert.equal(Object.hasOwn((await save()).workflow, 'allowedPatronCodeIds'), false, 'Unrelated saves omit the ID key');

    failCatalog = true; await controller.load();
    assert.deepEqual(selected(), ['77', '2']);
    assert.equal(input.disabled, true);
    assert.equal(controller.isDirty(), false);
    deferCatalog = true;
    document.getElementById('patron-code-retry').click();
    await until(() => Boolean(lateCatalog));
    failCatalog = false;
    await scope('2');
    assert.equal(lateSignal.aborted, true, 'Scope acceptance cancels the old catalog retry');
    lateCatalog(reply(401, { code: 'staff_session_invalid' }));
    await flush(); await flush();
    assert.equal(invalidations, 0, 'Cancelled stale retry cannot invalidate the current session');
    assert.deepEqual(selected(), ['77', '2']);
    assert.equal(input.disabled, true, 'Late retry cannot enable inherited chips');
    assert.equal(controller.isDirty(), false);
    console.log('Patron-code picker controller payload, reload, discard, conflict and stale-retry checks passed');
  } finally {
    controller?.dispose(); dom?.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exit(1); });
