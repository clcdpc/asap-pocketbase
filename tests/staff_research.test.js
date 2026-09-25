const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');

(async () => {
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-research-'));
  const dom = new JSDOM('<section id="links"></section>', { url: 'https://localhost/staff/' });
  try {
    fs.copyFileSync(path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend', 'staff', 'js', 'research.js'),
      path.join(temporary, 'research.js'));
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}');
    global.document = dom.window.document;
    const { applyPolarisResultToControls, createPolarisLookup, mergeCatalogValue,
      researchUrl, renderResearchLinks } = await import(pathToFileURL(path.join(temporary, 'research.js')).href);
    assert.equal(researchUrl('https://leap.example.test/bib/{{bibid}}', { bibid: '90/01' }, 'bibid'),
      'https://leap.example.test/bib/90%2F01');
    assert.equal(researchUrl('http://search.example.test/?q={{title}}', { title: 'A & B' }),
      'http://search.example.test/?q=A%20%26%20B');
    assert.equal(researchUrl('https://user:secret@search.example.test/?q={{title}}', { title: 'Book' }), '');
    assert.equal(researchUrl('javascript:alert(1)', { bibid: '9001' }), '');
    assert.equal(researchUrl('https://search.example.test/?q={{author}}', { author: 'Someone' }), '');
    assert.equal(researchUrl('https://leap.example.test/bib/', { bibid: '9001' }, 'bibid'), '');

    const links = document.querySelector('#links');
    const request = { title: 'A & B / C', identifier: '978 1', bibid: '9001' };
    renderResearchLinks(links, request, {
      leapBibUrlPattern: 'https://leap.example.test/bib/{{bibid}}',
      leapPatronUrlPattern: 'https://leap.example.test/patron/{{patronId}}',
      patronId: 7001,
      externalSearchProviders: [
        { sortOrder: 30, label: 'Identifier source', urlTemplate: 'https://search.example.test/?id={{identifier}}' },
        { sortOrder: 10, label: 'Title source', urlTemplate: 'https://search.example.test/?q={{title}}' },
        { sortOrder: 20, label: 'Disabled source', isEnabled: false,
          urlTemplate: 'https://search.example.test/?q={{title}}' },
        { sortOrder: 40, label: 'Unsupported source', urlTemplate: 'https://search.example.test/?q={{author}}' }
      ]
    });
    assert.deepEqual([...links.querySelectorAll('a')].map(link => link.textContent),
      ['Open BIB in LEAP', 'Open patron in LEAP', 'Title source', 'Identifier source']);
    assert.equal(links.querySelectorAll('a')[2].href, 'https://search.example.test/?q=A%20%26%20B%20%2F%20C');
    assert.equal(links.querySelectorAll('a')[3].href, 'https://search.example.test/?id=978%201');

    renderResearchLinks(links, { ...request, bibid: 'not-a-bib' }, {
      leapBibUrlPattern: 'https://leap.example.test/bib/{{bibid}}', externalSearchProviders: []
    });
    assert.equal(links.querySelectorAll('a').length, 0);

    renderResearchLinks(links, request, { externalSearchProviders: [] });
    assert.equal(links.hidden, true);
    assert.equal(links.querySelectorAll('a').length, 0);

    assert.equal(mergeCatalogValue('Catalog Title', ''), 'Catalog Title');
    assert.equal(mergeCatalogValue('Catalog Title', 'Catalog Title'), 'Catalog Title');
    assert.equal(mergeCatalogValue('Catalog Title', 'Patron Title'), 'Catalog Title (Patron Title)');
    assert.equal(mergeCatalogValue('Catalog Title', 'Catalog Title (Patron Title)'),
      'Catalog Title (Patron Title)');
    assert.equal(mergeCatalogValue('Catalog Title', 'Catalog Title: subtitle'), 'Catalog Title: subtitle');
    assert.equal(mergeCatalogValue('Alice Writer', 'A. Writer'), 'Alice Writer (A. Writer)');
    const reconciledAuthor = mergeCatalogValue('Alice Writer', 'A. Writer');
    assert.equal(mergeCatalogValue('Alice Writer', reconciledAuthor), reconciledAuthor);

    const makeControl = (value, disabled = false) => {
      const control = document.createElement('input');
      control.value = value;
      control.disabled = disabled;
      return control;
    };
    const catalogSelection = {
      bibId: '9001',
      title: 'Catalog Title',
      author: 'Alice Writer',
      identifier: '9782222222222'
    };
    const blankIdentifierControls = {
      bib: makeControl(''),
      title: makeControl(''),
      author: makeControl(''),
      identifier: makeControl('')
    };
    applyPolarisResultToControls(catalogSelection, blankIdentifierControls);
    assert.equal(blankIdentifierControls.bib.value, '9001');
    assert.equal(blankIdentifierControls.identifier.value, '9782222222222');
    const populatedIdentifierControls = {
      bib: makeControl(''),
      title: makeControl('Patron Title'),
      author: makeControl('A. Writer'),
      identifier: makeControl('9781111111111')
    };
    applyPolarisResultToControls(catalogSelection, populatedIdentifierControls);
    const reconciledTitle = 'Catalog Title (Patron Title)';
    const retainedAuthor = 'Alice Writer (A. Writer)';
    assert.equal(populatedIdentifierControls.title.value, reconciledTitle);
    assert.equal(populatedIdentifierControls.author.value, retainedAuthor);
    assert.equal(populatedIdentifierControls.identifier.value, '9781111111111');
    applyPolarisResultToControls(catalogSelection, populatedIdentifierControls);
    assert.equal(populatedIdentifierControls.title.value, reconciledTitle);
    assert.equal(populatedIdentifierControls.author.value, retainedAuthor);
    const lockedIdentifierControls = {
      bib: makeControl(''),
      title: makeControl(''),
      author: makeControl(''),
      identifier: makeControl('9783333333333', true)
    };
    applyPolarisResultToControls(catalogSelection, lockedIdentifierControls);
    assert.equal(lockedIdentifierControls.identifier.value, '9783333333333');

    document.body.insertAdjacentHTML('beforeend', `
      <dialog id="polaris-dialog" aria-labelledby="polaris-dialog-title">
        <select id="polaris-mode"><option value="title">Title</option></select>
        <label id="polaris-query-field"><span id="polaris-query-label"></span><input id="polaris-query"></label>
        <div id="polaris-pair-fields"><input id="polaris-title"><input id="polaris-author"></div>
        <p id="polaris-status"></p><ul id="polaris-results"></ul>
        <form id="polaris-form"></form><button id="close-polaris"></button>
      </dialog>`);
    const dialog = document.querySelector('#polaris-dialog');
    dialog.showModal = () => { dialog.open = true; };
    dialog.close = () => { dialog.open = false; };
    const outgoingBodies = [];
    const exactDetail = {
      bibId: '9001',
      title: 'Catalog Title',
      author: 'Alice Writer',
      publication: '',
      format: null,
      identifier: '',
      publisher: 'Exact Catalog Publisher',
      holdingsSummary: {
        myLibraryCount: 3,
        otherLibraryCount: 4,
        consortiumCount: 7,
        isHoldable: true,
        hasHoldableAtMyLibrary: true
      },
      holdingsUnavailable: false,
      patronHasHold: true
    };
    const searchRow = {
      bibId: '9001',
      title: 'Search Row Title',
      author: 'Search Row Author',
      publication: '2026',
      format: 'Book',
      identifier: '9780000000001'
    };
    const lookup = createPolarisLookup({
      authorizedJson: async (url, options) => {
        assert.equal(url, '/api/asap/staff/bib-lookup');
        outgoingBodies.push(options.body);
        if (options.body.mode === 'bib') return exactDetail;
        return {
          status: 'found',
          totalMatches: 1,
          results: [searchRow]
        };
      },
      isAbortError: () => false,
      announce: () => {}
    });
    lookup.open({
      requestId: '9007199254740993',
      libraryOrgId: 2,
      mode: 'title',
      query: 'Bigint title',
      isCurrent: () => true,
      canApply: false,
      apply: () => assert.fail('A search result should not be applied by the identity test')
    });
    document.querySelector('#polaris-form').dispatchEvent(new dom.window.Event('submit', {
      bubbles: true,
      cancelable: true
    }));
    await new Promise(resolve => setTimeout(resolve, 0));
    assert.equal(typeof outgoingBodies[0].requestId, 'string');
    assert.equal(outgoingBodies[0].requestId, '9007199254740993');
    assert.equal(JSON.parse(JSON.stringify(outgoingBodies[0])).requestId, '9007199254740993');
    lookup.close();

    const editor = {
      bib: makeControl(''),
      title: makeControl('Patron Title'),
      author: makeControl('A. Writer'),
      identifier: makeControl('9781111111111'),
      publication: makeControl('Library backlist'),
      format: makeControl('book')
    };
    let verified;
    lookup.open({
      requestId: '9007199254740993',
      libraryOrgId: 2,
      mode: 'title',
      query: 'Catalog Title',
      isCurrent: () => true,
      canApply: true,
      apply: (selected, detail) => {
        applyPolarisResultToControls(selected, editor);
        verified = { requestId: '9007199254740993', bibId: selected.bibId, selected, detail };
      }
    });
    document.querySelector('#polaris-form').dispatchEvent(new dom.window.Event('submit', {
      bubbles: true,
      cancelable: true
    }));
    await new Promise(resolve => setTimeout(resolve, 0));
    document.querySelector('#polaris-results button').click();
    await new Promise(resolve => setTimeout(resolve, 0));
    assert.equal(editor.bib.value, '9001');
    assert.equal(editor.title.value, 'Catalog Title (Patron Title)');
    assert.equal(editor.author.value, 'Alice Writer (A. Writer)');
    assert.equal(editor.identifier.value, '9781111111111');
    assert.equal(editor.publication.value, 'Library backlist');
    assert.equal(editor.format.value, 'book');
    assert.equal(verified.requestId, '9007199254740993');
    assert.equal(verified.bibId, '9001');
    assert.strictEqual(verified.detail, verified.selected);
    assert.equal(verified.detail.publication, '2026');
    assert.equal(verified.detail.format, 'Book');
    assert.equal(verified.detail.identifier, '9780000000001');
    assert.equal(verified.detail.title, 'Catalog Title');
    assert.equal(verified.detail.author, 'Alice Writer');
    assert.equal(verified.detail.publisher, 'Exact Catalog Publisher');
    assert.deepEqual(verified.detail.holdingsSummary, exactDetail.holdingsSummary);
    assert.equal(verified.detail.holdingsUnavailable, false);
    assert.equal(verified.detail.patronHasHold, true);
    assert.equal(dialog.open, false);
    lookup.close();

    let completeVerification;
    const pendingVerification = new Promise(resolve => { completeVerification = resolve; });
    let staleRequestIsCurrent = true;
    let staleSelectionApplied = false;
    const staleLookup = createPolarisLookup({
      authorizedJson: async (url, options) => options.body.mode === 'bib'
        ? pendingVerification
        : { status: 'found', totalMatches: 1, results: [searchRow] },
      isAbortError: () => false,
      announce: () => {}
    });
    staleLookup.open({
      requestId: '9007199254740994',
      libraryOrgId: 2,
      mode: 'title',
      query: 'Stale verification',
      isCurrent: () => staleRequestIsCurrent,
      canApply: true,
      apply: () => { staleSelectionApplied = true; }
    });
    document.querySelector('#polaris-form').dispatchEvent(new dom.window.Event('submit', {
      bubbles: true,
      cancelable: true
    }));
    await new Promise(resolve => setTimeout(resolve, 0));
    document.querySelector('#polaris-results button').click();
    staleRequestIsCurrent = false;
    staleLookup.close();
    completeVerification(exactDetail);
    await new Promise(resolve => setTimeout(resolve, 0));
    assert.equal(staleSelectionApplied, false);
    assert.equal(dialog.open, false);

    console.log('Staff research link contracts passed');
  } finally {
    dom.window.close();
    delete global.document;
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
