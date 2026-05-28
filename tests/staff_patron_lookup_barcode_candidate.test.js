const assert = require('assert');
const fs = require('fs');
const path = require('path');

function extractFunction(source, name) {
  const marker = `function ${name}(`;
  const start = source.indexOf(marker);
  if (start < 0) throw new Error(`Could not find ${name}`);
  let depth = 0;
  let opened = false;
  for (let i = start; i < source.length; i++) {
    const ch = source[i];
    if (ch === '{') {
      depth++;
      opened = true;
    }
    if (ch === '}') {
      depth--;
      if (opened && depth === 0) return source.slice(start, i + 1);
    }
  }
  throw new Error(`Could not parse ${name}`);
}

const source = fs.readFileSync(path.resolve(__dirname, '../lib/staff/lookup_routes.js'), 'utf8');
const fnCode = [
  extractFunction(source, 'looksLikeBarcodeCandidate'),
  extractFunction(source, 'beautifyPatronError'),
  extractFunction(source, 'shouldFallBackToPatronNameSearch'),
  extractFunction(source, 'resolveEffectiveStaffLibraryContext'),
  extractFunction(source, 'allowCrossLibraryPatronLookup'),
  extractFunction(source, 'patronMatchesStaffLookupScope'),
  extractFunction(source, 'staffPatronLookupScopeMeta'),
  extractFunction(source, 'withScopeMeta'),
  extractFunction(source, 'staffPatronLookupResponse'),
  extractFunction(source, 'resolveStaffPatronByBarcode'),
  extractFunction(source, 'filterPatronSearchResultsForStaffLibrary'),
  extractFunction(source, 'staffLookupPatron'),
].join('\n');

function load(env) {
  env.config = env.config || { workflowSettings: () => ({ allowAnyRegisteredCardLogin: false }) };
  env.orgs = env.orgs || {};
  env.effectiveLibrary = env.effectiveLibrary || {
    resolveEffectiveStaffLibraryContext: (e, staff, data) => {
      const staffLibraryOrgId = String(staff.get('libraryOrgId') || '').trim();
      const role = String(staff.get('role') || '').toLowerCase();
      const requestedOrgId = String((data && (data.libraryOrgId || data.effectiveLibraryOrgId)) || '').trim();
      const libraryOrgId = role === 'super_admin' && requestedOrgId ? requestedOrgId : staffLibraryOrgId;
      return { libraryOrgId, libraryOrgName: staff.get('libraryOrgName') || '' };
    },
    allowCrossLibraryPatronLookup: (e, orgId) => !!env.config.workflowSettings(e.app, orgId).allowAnyRegisteredCardLogin,
    patronMatchesStaffLookupScope: (staff, patronData, orgId, allowAny) => allowAny || String((patronData && patronData.LibraryOrgID) || '').trim() === String(orgId || staff.get('libraryOrgId') || '').trim(),
    staffPatronLookupScopeMeta: (e, orgId, name, allowAny) => ({ patronSearchScope: allowAny ? 'system' : 'library', patronSearchLimitedToLibrary: !allowAny, effectiveLibraryOrgId: String(orgId || ''), effectiveLibraryOrgName: String(name || '') })
  };
  return new Function('env', `with (env) { ${fnCode}; return { looksLikeBarcodeCandidate, staffLookupPatron }; }`)(env);
}

function makeEvent(requestBody) {
  let response = null;
  return {
    e: {
      requestBody,
      app: {
        logger: () => ({ warn: () => {}, error: () => {} }),
      },
      json: (status, body) => {
        response = { status, body };
        return response;
      },
    },
    getResponse: () => response,
  };
}

{
  const { looksLikeBarcodeCandidate } = load({});

  assert.strictEqual(looksLikeBarcodeCandidate('P123456'), true);
  assert.strictEqual(looksLikeBarcodeCandidate('ABC123'), true);
  assert.strictEqual(looksLikeBarcodeCandidate('21170000570016'), true);
  assert.strictEqual(looksLikeBarcodeCandidate('Jane Smith'), false);
}

{
  const calls = { lookupPatron: 0, searchPatrons: 0 };
  const { staffLookupPatron } = load({
    routeUtils: {
      requireAuth: () => ({ get: () => '9' }),
      body: e => e.requestBody,
      sameLibrary: () => true,
    },
    polaris: {
      adminStaffAuth: () => ({ token: 'staff' }),
      lookupPatron: (staffAuth, barcode) => {
        calls.lookupPatron++;
        assert.strictEqual(barcode, 'P123456');
        return {
          PatronID: 'patron-1',
          Barcode: barcode,
          NameFirst: 'Jane',
          NameLast: 'Smith',
          LibraryOrgID: '9',
        };
      },
      searchPatrons: () => {
        calls.searchPatrons++;
        throw new Error('name search should not run for a barcode hit');
      },
    },
    orgs: {
      attachPatronScope: (app, patron) => patron,
    },
    records: {
      upsertPatronUser: (app, patron) => ({
        get: key => ({
          barcode: patron.Barcode,
          nameFirst: patron.NameFirst,
          nameLast: patron.NameLast,
          notificationEmail: '',
          patronOrgId: '',
          libraryOrgId: patron.LibraryOrgID,
          libraryOrgName: '',
        })[key] || '',
        email: () => '',
      }),
    },
  });
  const { e, getResponse } = makeEvent({ query: 'P123456' });

  staffLookupPatron(e);

  const response = getResponse();
  assert.strictEqual(response.status, 200);
  assert.strictEqual(response.body.status, 'selected');
  assert.strictEqual(response.body.barcode, 'P123456');
  assert.strictEqual(calls.lookupPatron, 1);
  assert.strictEqual(calls.searchPatrons, 0);
}

{
  const calls = { lookupPatron: 0, searchPatrons: 0 };
  const { staffLookupPatron } = load({
    routeUtils: {
      requireAuth: () => ({ get: () => '9' }),
      body: e => e.requestBody,
      sameLibrary: () => true,
    },
    polaris: {
      adminStaffAuth: () => ({ token: 'staff' }),
      lookupPatron: () => {
        calls.lookupPatron++;
        throw new Error('Patron not found');
      },
      searchPatrons: (staffAuth, options) => {
        calls.searchPatrons++;
        assert.strictEqual(options.query, 'ABC123');
        return { status: 'ok', results: [] };
      },
    },
    orgs: {
      attachPatronScope: (app, patron) => patron,
    },
    records: {},
  });
  const { e, getResponse } = makeEvent({ query: 'ABC123' });

  staffLookupPatron(e);

  const response = getResponse();
  assert.strictEqual(response.status, 404);
  assert.strictEqual(response.body.status, 'not_found');
  assert.strictEqual(calls.lookupPatron, 1);
  assert.strictEqual(calls.searchPatrons, 1);
}

{
  const calls = { searchPatrons: 0 };
  const { staffLookupPatron } = load({
    routeUtils: {
      requireAuth: () => ({ get: () => '9' }),
      body: e => e.requestBody,
      sameLibrary: () => false,
    },
    polaris: {
      adminStaffAuth: () => ({ token: 'staff' }),
      lookupPatron: () => ({
        PatronID: 'patron-2',
        Barcode: 'ABC123',
        LibraryOrgID: '10',
      }),
      searchPatrons: () => {
        calls.searchPatrons++;
        throw new Error('name search should not run for a scoped barcode failure');
      },
    },
    orgs: {
      attachPatronScope: (app, patron) => patron,
    },
    records: {},
  });
  const { e, getResponse } = makeEvent({ query: 'ABC123' });

  staffLookupPatron(e);

  const response = getResponse();
  assert.strictEqual(response.status, 403);
  assert.strictEqual(response.body.message, 'This patron belongs to another library. You can only submit suggestions for patrons in your own library system.');
  assert.strictEqual(calls.searchPatrons, 0);
}

{
  const { staffLookupPatron } = load({
    routeUtils: {
      requireAuth: () => ({ get: key => ({ libraryOrgId: '9', role: 'admin', libraryOrgName: 'Library A' })[key] || '' }),
      body: e => e.requestBody,
      sameLibrary: () => false,
    },
    config: { workflowSettings: () => ({ allowAnyRegisteredCardLogin: true }) },
    polaris: {
      adminStaffAuth: () => ({ token: 'staff' }),
      lookupPatron: () => ({ PatronID: 'patron-3', Barcode: 'ABC123', NameFirst: 'Cross', NameLast: 'Patron', LibraryOrgID: '10', LibraryOrgName: 'Library B' }),
      searchPatrons: () => { throw new Error('name search should not run for enabled barcode hit'); },
    },
    orgs: {
      attachPatronScope: (app, patron) => patron,
      findOrganization: () => ({ get: () => 'Library A' }),
    },
    records: {
      upsertPatronUser: (app, patron) => ({
        get: key => ({ barcode: patron.Barcode, nameFirst: patron.NameFirst, nameLast: patron.NameLast, notificationEmail: '', patronOrgId: '', libraryOrgId: patron.LibraryOrgID, libraryOrgName: patron.LibraryOrgName })[key] || '',
        email: () => '',
      }),
    },
  });
  const { e, getResponse } = makeEvent({ query: 'ABC123' });
  staffLookupPatron(e);
  const response = getResponse();
  assert.strictEqual(response.status, 200);
  assert.strictEqual(response.body.status, 'selected');
  assert.strictEqual(response.body.libraryOrgId, '10');
  assert.strictEqual(response.body.patronHomeLibraryOrgId, '10');
  assert.strictEqual(response.body.patronSearchLimitedToLibrary, false);
}

{
  const { staffLookupPatron } = load({
    routeUtils: {
      requireAuth: () => ({ get: key => ({ libraryOrgId: '9', role: 'admin', libraryOrgName: 'Library A' })[key] || '' }),
      body: e => e.requestBody,
      sameLibrary: () => false,
    },
    config: { workflowSettings: () => ({ allowAnyRegisteredCardLogin: true }) },
    polaris: {
      adminStaffAuth: () => ({ token: 'staff' }),
      lookupPatron: (staffAuth, barcode) => ({ PatronID: barcode, Barcode: barcode, NameFirst: barcode, NameLast: 'Patron', LibraryOrgID: barcode === 'A' ? '9' : '10', LibraryOrgName: barcode === 'A' ? 'Library A' : 'Library B' }),
      searchPatrons: () => ({ status: 'ok', results: [{ barcode: 'A' }, { barcode: 'B' }] }),
    },
    orgs: {
      attachPatronScope: (app, patron) => patron,
      findOrganization: () => ({ get: () => 'Library A' }),
    },
    records: {},
  });
  const { e, getResponse } = makeEvent({ query: 'Jane Smith' });
  staffLookupPatron(e);
  const response = getResponse();
  assert.strictEqual(response.status, 200);
  assert.strictEqual(response.body.status, 'multiple');
  assert.deepStrictEqual(response.body.results.map(r => r.libraryOrgId), ['9', '10']);
  assert.strictEqual(response.body.patronSearchLimitedToLibrary, false);
}

console.log('Staff patron lookup barcode candidate tests passed.');
