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

const source = fs.readFileSync(path.resolve(__dirname, '../lib/staff_routes.js'), 'utf8');
const fnCode = [
  extractFunction(source, 'looksLikeBarcodeCandidate'),
  extractFunction(source, 'shouldFallBackToPatronNameSearch'),
  extractFunction(source, 'staffPatronLookupResponse'),
  extractFunction(source, 'resolveStaffPatronByBarcode'),
  extractFunction(source, 'filterPatronSearchResultsForStaffLibrary'),
  extractFunction(source, 'staffLookupPatron'),
].join('\n');

function load(env) {
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
  assert.strictEqual(response.body.message, 'This patron belongs to a different library.');
  assert.strictEqual(calls.searchPatrons, 0);
}

console.log('Staff patron lookup barcode candidate tests passed.');
