const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { pathToFileURL } = require('url');
const { JSDOM } = require('jsdom');

(async () => {
  const repositoryRoot = path.join(__dirname, '..');
  const frontendRoot = path.join(repositoryRoot, 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-settings-a11y-layout-'));
  fs.cpSync(path.join(frontendRoot, 'staff'), path.join(temporary, 'staff'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');

  try {
    const module = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings-domains.js')).href);
    const html = fs.readFileSync(path.join(frontendRoot, 'staff', 'index.html'), 'utf8');
    const dom = new JSDOM(html, { url: 'http://localhost/staff/' });
    global.window = dom.window;
    global.document = dom.window.document;
    global.Node = dom.window.Node;

    const panels = [...document.querySelectorAll('[data-settings-panel-content]')];
    assert.equal(document.getElementById('settings-nav').getAttribute('aria-orientation'), 'vertical');
    for (const [id, name] of [
      ['leap-bib-url-pattern', 'LEAP BIB record URL pattern'], ['leap-patron-url-pattern', 'LEAP patron record URL pattern'],
      ['ui-system-not-enabled-msg', 'Nonparticipating-library message'], ['ui-misconfigured-msg', 'Misconfigured system message'],
      ['staff-add-email', 'Email address']
    ]) {
      const control = document.getElementById(id), label = document.querySelector(`label[for="${id}"]`);
      const help = document.getElementById(control.getAttribute('aria-describedby'));
      assert.equal(label.textContent, name);
      assert.ok(help?.textContent.trim(), `${name} retains associated help`);
      assert.equal(label.contains(help), false, `${name} help is separate from its accessible name`);
    }
    assert.strictEqual(panels.filter(panel => !panel.hidden).map(panel => panel.id).join(','), 'settings-start');
    assert.strictEqual(document.getElementById('settings-organizations-list').tagName, 'UL');
    const organizationCatalog = document.getElementById('settings-organization-catalog');
    assert.strictEqual(organizationCatalog.closest('[data-settings-panel-content]').id, 'settings-polaris');
    assert.strictEqual(organizationCatalog.querySelector('legend').textContent, 'Polaris organizations and library participation');
    assert.deepStrictEqual([...organizationCatalog.querySelectorAll('h3')].map(heading => heading.textContent),
      ['ASAP library participation', 'Other synchronized organizations']);
    assert.strictEqual(organizationCatalog.getAttribute('data-system-only'), 'true');
    assert.strictEqual(document.querySelector('#settings-staff #settings-organizations-list'), null);
    assert.strictEqual(document.getElementById('enabled-libraries-checkbox-container'), null);
    assert.strictEqual(document.getElementById('manage-library-participation').type, 'button');
    assert.strictEqual(document.getElementById('manage-library-participation').getAttribute('aria-controls'), organizationCatalog.id);

    const header = document.querySelector('#settings-view .settings-header');
    assert.ok(header && !header.classList.contains('section-heading'));
    assert.deepStrictEqual([...header.querySelectorAll('[id]')].map(element => element.id), [
      'settings-title', 'settings-scope-field', 'settings-scope', 'settings-refresh', 'settings-message'
    ], 'the compact header must omit repeated scope text and preserve its controls');
    assert.strictEqual(header.querySelector('#settings-context-summary'), null);
    const toolbar = header.querySelector('.settings-toolbar');
    assert.strictEqual(document.getElementById('settings-scope-field').parentElement, toolbar);
    assert.strictEqual(document.getElementById('settings-refresh').parentElement, toolbar);
    assert.strictEqual(document.getElementById('settings-message').parentElement, toolbar);
    assert.strictEqual(document.querySelector('#settings-scope-field > span').textContent, 'Settings scope');
    assert.strictEqual(document.getElementById('settings-scope').getAttribute('aria-label'), 'Settings scope');
    assert.strictEqual(document.getElementById('settings-message').getAttribute('role'), 'status');
    assert.strictEqual(document.getElementById('settings-message').getAttribute('aria-live'), 'polite');
    const settingsSource = fs.readFileSync(path.join(frontendRoot, 'staff', 'js', 'settings.js'), 'utf8');
    for (const match of settingsSource.matchAll(/(?:querySelector\(['"]#|getElementById\(['"])([\w-]+)['"]\)/g)) {
      assert.strictEqual(document.querySelectorAll(`#${match[1]}`).length, 1, match[1]);
    }

    const patronFieldsets = [...document.querySelectorAll('#settings-patron > fieldset.settings-fieldset')];
    assert.deepStrictEqual(patronFieldsets.map(fieldset => fieldset.querySelector('legend').textContent.trim()), [
      'Branding', 'Public patron portal', 'Patron text', 'Publication options and formats'
    ], 'Branding is the first Patron experience section while the other sections retain their order');
    assert.strictEqual(patronFieldsets[0].previousElementSibling, document.querySelector('#settings-patron > .settings-panel-heading'),
      'Branding follows the Patron experience introduction immediately');
    assert.deepStrictEqual([...patronFieldsets[0].querySelectorAll('[id]')].map(element => element.id), [
      'branding-preview-source', 'branding-preview', 'branding-alt', 'branding-logo', 'save-branding-logo',
      'discard-branding-draft', 'clear-branding-logo', 'branding-status'
    ], 'The complete existing Branding editor remains together in one fieldset');

    const controls = scope => [...scope.querySelectorAll('input, select, textarea')].map(control => control.id);
    const fieldset = id => document.getElementById(id).closest('fieldset');
    const legend = id => fieldset(id).querySelector('legend').textContent;
    const wide = id => assert.ok(document.getElementById(id).closest('.settings-field').classList.contains('wide'), id);
    assert.deepStrictEqual(controls(document.getElementById('settings-polaris')), [
      'polaris-host', 'polaris-access-id', 'polaris-api-key', 'polaris-domain',
      'polaris-admin-user', 'polaris-admin-pass', 'polaris-workstation-id', 'polaris-system-user-id'
    ]);
    for (const [ids, name] of [
      [['polaris-host', 'polaris-access-id', 'polaris-api-key'], 'Polaris API'],
      [['polaris-domain', 'polaris-admin-user', 'polaris-admin-pass'], 'System staff account'],
      [['polaris-workstation-id', 'polaris-system-user-id'], 'System identity']
    ]) {
      assert.deepStrictEqual(controls(fieldset(ids[0])), ids);
      assert.strictEqual(legend(ids[0]), name);
    }
    assert.deepStrictEqual(controls(document.getElementById('settings-system-links')), [
      'system-staff-url', 'leap-bib-url-pattern', 'leap-patron-url-pattern',
      'format-icon-url-pattern', 'patron-embed-allowed-origins', 'ui-system-not-enabled-msg', 'ui-misconfigured-msg'
    ]);
    const staffUrl = document.getElementById('system-staff-url');
    assert.strictEqual(staffUrl.closest('label').querySelector('span').textContent, 'Staff URL');
    assert.strictEqual(staffUrl.closest('.settings-staff-url-control').classList.contains('wide'), true);
    const useCurrentAddress = document.getElementById('use-current-staff-url');
    assert.strictEqual(useCurrentAddress.closest('#settings-system-links'), document.getElementById('settings-system-links'));
    assert.strictEqual(useCurrentAddress.type, 'button');
    assert.strictEqual(useCurrentAddress.textContent, 'Use current address');
    const staffUrlCandidate = document.getElementById('system-staff-url-candidate');
    assert.strictEqual(staffUrlCandidate.getAttribute('role'), 'status');
    assert.strictEqual(staffUrlCandidate.getAttribute('aria-live'), 'polite');
    for (const id of ['polaris-api-key-status', 'polaris-admin-pass-status']) {
      assert.strictEqual(document.getElementById(id).getAttribute('role'), 'status');
      assert.strictEqual(document.getElementById(id).getAttribute('aria-live'), 'polite');
    }
    assert.deepStrictEqual(controls(fieldset('email-from-address')), ['email-from-address', 'email-from-name']);
    assert.strictEqual(legend('email-from-address'), 'Sender identity');
    assert.deepStrictEqual(controls(fieldset('email-postmark-token')), ['email-postmark-token', 'email-clear-postmark-token']);
    assert.strictEqual(legend('email-postmark-token'), 'Postmark connection');
    assert.strictEqual(document.getElementById('email-token-status').closest('fieldset'), fieldset('email-postmark-token'));
    assert.strictEqual(document.getElementById('email-postmark-token').getAttribute('aria-describedby'), 'email-token-status');
    assert.strictEqual(document.getElementById('email-token-status').getAttribute('role'), 'status');
    assert.strictEqual(document.getElementById('email-token-status').getAttribute('aria-live'), 'polite');
    assert.deepStrictEqual(controls(fieldset('staff-add-email')), ['staff-add-email', 'staff-add-role', 'staff-add-organization']);
    assert.deepStrictEqual(controls(fieldset('patron-page-title')).slice(0, 3), ['patron-page-title', 'patron-barcode-label', 'patron-pin-label']);
    assert.strictEqual(legend('auto-promote'), 'Suggestion and eligibility');
    assert.strictEqual(legend('patron-code-picker'), 'Patron code eligibility');
    assert.deepStrictEqual(controls(fieldset('patron-code-picker')), [
      'patron-code-eligibility-enabled', 'patron-code-picker',
      'patron-code-selection', 'patron-code-eligibility-message'
    ]);
    const patronCodeEligibility = document.getElementById('patron-code-eligibility');
    assert.equal([...patronCodeEligibility.querySelectorAll('p')]
      .some(paragraph => paragraph.textContent.trim() === 'The patron-code eligibility message is displayed as written.'), false);
    const eligibilityMessage = document.getElementById('patron-code-eligibility-message');
    assert.equal(eligibilityMessage.closest('label').querySelector('span').textContent, 'Patron-code eligibility message');
    const suggestionMessage = document.getElementById('suggestion-limit-message');
    const suggestionMessageField = suggestionMessage.closest('[data-setting-section="workflow"][data-setting-key="suggestionLimitMessage"]');
    assert.equal(suggestionMessageField.tagName, 'DIV');
    assert.equal(document.querySelector('label[for="suggestion-limit-message"] span').textContent, 'Suggestion limit message');
    const suggestionTools = document.querySelector('.suggestion-message-placeholder-tools');
    const suggestionTrigger = document.getElementById('suggestion-limit-placeholder-trigger');
    const suggestionPicker = document.getElementById('suggestion-limit-placeholder-picker');
    assert.equal(suggestionTools.getAttribute('data-settings-ignore'), 'true',
      'The insertion presentation controls do not enter the settings draft snapshot.');
    assert.equal(suggestionTools.contains(suggestionTrigger), true);
    assert.equal(suggestionTools.contains(suggestionPicker), true);
    assert.equal(suggestionMessage.closest('[data-settings-ignore="true"]'), null,
      'The actual workflow editor remains part of normal save/dirty handling.');
    assert.equal(suggestionTrigger.type, 'button');
    assert.equal(suggestionTrigger.textContent, 'Insert placeholder');
    assert.equal(suggestionTrigger.getAttribute('aria-label'), 'Insert placeholder into Suggestion limit message');
    assert.equal(suggestionTrigger.getAttribute('aria-controls'), suggestionPicker.id);
    assert.equal(suggestionTrigger.getAttribute('aria-expanded'), 'false');
    assert.equal(suggestionPicker.getAttribute('role'), 'group');
    assert.equal(suggestionPicker.getAttribute('aria-label'), 'Placeholders for Suggestion limit message');
    const suggestionOptions = suggestionPicker.querySelectorAll('[data-suggestion-placeholder]');
    assert.equal(suggestionOptions.length, 1, 'Only the runtime-supported workflow token is offered.');
    assert.equal(suggestionOptions[0].dataset.suggestionPlaceholder, 'next_available_date');
    assert.equal(suggestionOptions[0].getAttribute('aria-label'),
      'Insert Next available submission date/time — {{next_available_date}}');
    assert.equal(suggestionOptions[0].querySelector('code').textContent, '{{next_available_date}}');
    assert.equal(document.getElementById('suggestion-limit-placeholder-status').getAttribute('role'), 'status');
    assert.equal(document.getElementById('suggestion-limit-placeholder-status').getAttribute('aria-live'), 'polite');
    assert.equal([...document.querySelectorAll('#settings-workflow .settings-callout')].some(callout =>
      callout.textContent.includes('The suggestion limit message supports')), false,
    'The detached suggestion-limit callout is removed.');
    assert.strictEqual(document.querySelector('label[for="patron-code-picker"]').textContent, 'Allowed patron-code IDs');
    assert.strictEqual(document.getElementById('patron-code-picker').getAttribute('role'), 'combobox');
    assert.strictEqual(document.getElementById('patron-code-picker').getAttribute('aria-controls'), 'patron-code-options');
    assert.strictEqual(document.getElementById('patron-code-options').getAttribute('role'), 'listbox');
    const codeSummary = document.querySelector('.patron-code-summary');
    for (const id of ['patron-code-availability', 'patron-code-retry', 'patron-code-selection', 'patron-codes-editor', 'patron-code-count']) {
      assert.ok(codeSummary.contains(document.getElementById(id)), `${id} shares the selection summary`);
    }
    assert.equal(document.getElementById('patron-code-availability').getAttribute('role'), 'status');
    assert.equal(document.getElementById('patron-code-availability').hidden, false,
      'The empty availability live region remains mounted and exposed.');
    assert.equal(document.getElementById('patron-code-selection').closest('[data-settings-ignore="true"]'), null,
      'The selected IDs remain owned by the settings draft.');
    assert.equal(codeSummary.contains(document.getElementById('patron-code-warning')), false,
      'The conditional warning keeps its own separated stack position.');
    const publicNotes = document.querySelector('.patron-public-notes');
    assert.ok(publicNotes.contains(document.getElementById('patron-copy-status')));
    assert.equal(document.getElementById('patron-copy-status').getAttribute('aria-live'), 'polite');
    assert.equal(document.getElementById('patron-public-links').hidden, true,
      'Public links stay hidden until an active library has an authoritative URL.');
    assert.strictEqual(document.getElementById('common-creators-editor').closest('fieldset').querySelector('[id^="patron-code"]'), null);
    const commonCreators = fieldset('common-authors-enabled');
    const commonAuthorsEnabled = document.getElementById('common-authors-enabled');
    const commonAuthorsOptions = document.getElementById('common-authors-options');
    assert.equal(legend('common-authors-enabled'), 'Common creators');
    assert.equal(commonAuthorsEnabled.closest('label').querySelector('span').textContent, 'Show common creators');
    assert.equal(commonAuthorsEnabled.getAttribute('aria-controls'), 'common-authors-options');
    assert.equal(commonAuthorsEnabled.hasAttribute('aria-expanded'), false,
      'A checkbox does not expose an unsupported disclosure state.');
    assert.equal(commonAuthorsOptions.tagName, 'FIELDSET');
    assert.equal(commonAuthorsOptions.disabled, true);
    assert.equal(commonAuthorsOptions.hidden, true);
    assert.equal(commonAuthorsOptions.getAttribute('aria-labelledby'), 'common-authors-options-label');
    assert.equal(commonAuthorsOptions.querySelector('legend').textContent, 'Common creators options');
    assert.equal(commonAuthorsOptions.parentElement, commonCreators.querySelector('.settings-grid'));
    assert.equal(commonAuthorsOptions.contains(commonAuthorsEnabled), false,
      'The controlling checkbox remains outside the hidden options.');
    assert.deepStrictEqual(controls(commonAuthorsOptions), [
      'common-authors-label', 'common-authors-help', 'common-authors-message'
    ]);
    for (const id of ['add-common-creator', 'common-creators-editor']) {
      assert.ok(commonAuthorsOptions.contains(document.getElementById(id)), `${id} stays inside the collapsed options.`);
    }
    for (const id of ['add-patron-code', 'patron-code-search', 'patron-codes-select-all', 'patron-codes-clear-all']) {
      assert.strictEqual(document.getElementById(id), null, `${id} is retired`);
    }
    const providerHelp = document.getElementById('external-search-provider-help');
    assert.match(providerHelp.textContent, /URL templates: use \{\{title\}\} for the request title/);
    assert.match(providerHelp.textContent, /https:\/\/example.com\/search\?q=\{\{title\}\}/);
    assert.ok(providerHelp.compareDocumentPosition(document.getElementById('external-search-provider-editor')) & window.Node.DOCUMENT_POSITION_FOLLOWING);
    assert.strictEqual(document.getElementById('external-search-provider-editor').contains(providerHelp), false);
    assert.strictEqual(legend('outstanding-timeout-enabled'), 'Timeouts');
    assert.deepStrictEqual(controls(fieldset('outstanding-timeout-enabled')), [
      'outstanding-timeout-enabled', 'outstanding-timeout-days',
      'outstanding-timeout-send-email', 'outstanding-timeout-rejection-template-id',
      'hold-pickup-timeout-enabled', 'hold-pickup-timeout-days',
      'pending-hold-timeout-enabled', 'pending-hold-timeout-days',
      'additional-copy-timeout-enabled', 'additional-copy-timeout-days'
    ]);
    const timeoutRules = [...document.querySelectorAll('.settings-timeout-rule')];
    assert.deepStrictEqual(timeoutRules.map(rule => rule.dataset.timeoutRule), [
      'outstanding', 'hold-pickup', 'pending-hold', 'additional-copy'
    ]);
    for (const rule of timeoutRules) {
      const groupName = document.getElementById(rule.getAttribute('aria-labelledby'));
      assert.ok(groupName && rule.contains(groupName), `${rule.dataset.timeoutRule} has a programmatic rule name`);
    }
    const outstandingRule = timeoutRules[0];
    assert.deepStrictEqual([...outstandingRule.querySelectorAll(':scope > .settings-field')]
      .map(field => field.querySelector('input, select').id), ['outstanding-timeout-enabled', 'outstanding-timeout-days']);
    const timeoutSubgroup = outstandingRule.querySelector('.settings-timeout-subgroup');
    assert.strictEqual(document.getElementById(timeoutSubgroup.getAttribute('aria-labelledby')).textContent,
      'Send auto-rejection email', 'The email/template subgroup is named by its existing checkbox label.');
    assert.deepStrictEqual([...timeoutSubgroup.querySelectorAll('.settings-field input, .settings-field select')]
      .map(control => control.id), ['outstanding-timeout-send-email', 'outstanding-timeout-rejection-template-id']);
    ['polaris-host', 'polaris-domain', 'format-icon-url-pattern',
      'email-postmark-token', 'email-clear-postmark-token', 'staff-add-email', 'patron-page-title'].forEach(wide);
    const ids = [...document.querySelectorAll('[id]')].map(element => element.id);
    assert.strictEqual(new Set(ids).size, ids.length, 'control IDs must remain unique');
    for (const control of document.querySelectorAll('#settings-view input:not([type="hidden"]), #settings-view select, #settings-view textarea')) {
      assert.ok(control.closest('label') || document.querySelector(`label[for="${control.id}"]`), control.id);
    }
    const css = fs.readFileSync(path.join(frontendRoot, 'staff', 'styles.css'), 'utf8');
    assert.match(css, /\.settings-callout\.settings-scope-explanation\s*\{\s*margin-bottom:\s*10px;/,
      'Library explanations keep their visible gap despite the shared callout margin reset.');
    assert.match(css, /\.settings-domain-block\s*\{\s*display:\s*grid;\s*gap:\s*10px;\s*min-width:\s*0;/);
    assert.match(css, /\.settings-fieldset\s*>\s*\.settings-domain-block\s*\+\s*\.settings-domain-block,/,
      'Between-domain margins apply outside the existing Settings grid.');
    assert.match(css, /#patron-public-links:not\(\[hidden\]\)\s*\{[^}]*display:\s*grid;[^}]*gap:\s*10px/);
    assert.match(css, /#patron-public-links\s*>\s*button\s*\{\s*justify-self:\s*start;/,
      'Copy actions remain content-sized.');
    assert.match(css, /#patron-public-links\s+\.patron-public-notes\s*>\s*\.settings-result:not\(:empty\)\s*\{\s*margin-top:\s*10px;/,
      'A successful copy notice outranks the Public links paragraph margin reset.');
    assert.doesNotMatch(css, /#patron-code-availability[^{}]*\{[^}]*display:\s*none/,
      'Empty picker announcements do not rely on hiding the live region.');
    assert.match(css, /\.settings-timeouts\s*\{\s*container-type:\s*inline-size;/);
    assert.match(css, /@container\s*\(max-width:\s*40rem\)[^]*?\.settings-timeouts\s*>\s*\.settings-grid,/,
      'Timeouts respond to their actual available width at CSS zoom.');
    assert.match(css, /\.settings-save-bar\s*\{\s*position:\s*static;/);
    assert.match(css, /\.settings-save-bar\.attention\s*\{\s*position:\s*sticky;\s*bottom:\s*0;/);
    assert.match(css, /@media[^{}]*max-width:\s*760px[^]*?\.settings-grid\s*\{\s*grid-template-columns:\s*1fr;/);
    assert.match(css, /\.settings-timeout-rule,\s*\.settings-timeout-subgroup\s*\{\s*grid-template-columns:\s*minmax\(0,\s*1fr\);/);
    assert.match(css, /\.settings-timeout-rule\s+\.settings-field:not\(\.check-field\)\s*>\s*:is\(input,\s*select\)\s*\{\s*box-sizing:\s*border-box;\s*min-width:\s*0;\s*width:\s*100%;\s*max-width:\s*100%;/);
    assert.match(css, /\.settings-common-authors-options\s*\{\s*min-width:\s*0;\s*margin:\s*0;\s*padding:\s*0;\s*border:\s*0;/);
    assert.match(css, /\.suggestion-message-editor\s*\{[^}]*grid-template-columns:\s*minmax\(0,\s*1fr\)\s+auto;/);
    assert.match(css, /\.suggestion-message-placeholder-picker\s*\{[^}]*position:\s*fixed;[^}]*max-width:\s*calc\(100vw\s*-\s*24px\)/);
    assert.match(css, /@media\s*\(max-width:\s*760px\)[^]*?\.suggestion-message-editor\s*\{\s*grid-template-columns:\s*minmax\(0,\s*1fr\);/);

    const formats = Array.from({ length: 6 }, (_, index) => ({
      id: String(index + 10),
      code: `format_${index + 1}`,
      ownerOrganizationId: 1,
      label: 'Shared format label',
      isEnabled: true,
      messageBehavior: 'none',
      fields: {}
    }));
    const root = document.getElementById('settings-view');
    const editors = module.createSettingsDomainEditors({ root });
    editors.populate({
      orgId: 'system',
      stored: {
        configuredSystem: {
          publicationOptions: { exists: false, values: [] },
          commonCreators: { exists: false, values: [] },
          allowedPatronCodeIds: { exists: false, values: [] },
          providers: [],
          formats,
          templates: []
        },
        libraryOverride: {},
        customFields: [],
        autoClaimRules: [],
        templates: []
      },
      effective: {
        externalSearchProviders: [],
        formats,
        customFields: []
      },
      patronCodeChoices: [],
      autoClaimStaff: []
    }, true);

    const labels = [...document.querySelectorAll('input[data-format-label]')];
    const modes = [...document.querySelectorAll('select[data-format-field]')];
    assert.strictEqual(labels.length, 24);
    assert.strictEqual(modes.length, 24);
    assert.ok(labels.every(input => input.getAttribute('aria-label')));
    assert.ok(modes.every(select => select.getAttribute('aria-label')));
    assert.strictEqual(new Set(modes.map(select => select.getAttribute('aria-label'))).size, modes.length,
      'Requirement names distinguish every field and configured format.');
    const cards = [...root.querySelectorAll('#format-rules-editor [data-rule-code]')];
    for (const [index, card] of cards.entries()) {
      const toggle = card.querySelector('[data-rule-toggle]');
      const panel = document.getElementById(toggle.getAttribute('aria-controls'));
      assert.ok(panel && panel.hidden && card.contains(panel));
      assert.strictEqual(toggle.getAttribute('aria-expanded'), 'false');
      assert.ok(panel.querySelectorAll('[data-domain-editable]').length >= 10);
      assert.strictEqual(card.querySelectorAll('button button').length, 0);
      assert.ok([...card.querySelectorAll('.settings-row-actions button')]
        .every(button => !toggle.contains(button) && button.getAttribute('aria-label')
          .includes(`Shared format label, format ${index + 1}`)));
      toggle.click();
      assert.strictEqual(panel.hidden, false);
      assert.strictEqual(toggle.getAttribute('aria-expanded'), 'true');
      assert.match(toggle.getAttribute('aria-label'), /^Collapse Shared format label/);
      assert.deepStrictEqual([...panel.querySelectorAll('legend')].map(node => node.textContent),
        ['Message behavior', 'Field requirements', 'Custom field rules']);
      for (const control of panel.querySelectorAll('input,select,textarea')) {
        assert.ok(control.closest('label'), control.outerHTML);
      }
    }
    assert.strictEqual(new Set(cards.map(card => card.querySelector('[data-rule-toggle]').getAttribute('aria-controls'))).size, cards.length);
    const headerNames = cards.flatMap(card => [...card.querySelectorAll('.settings-row-actions button')]
      .map(button => button.getAttribute('aria-label')));
    assert.strictEqual(new Set(headerNames).size, headerNames.length, 'Duplicate format labels have distinct action names.');
    assert.match(css, /#format-rules-editor\s*\{\s*container-type:\s*inline-size;/);
    assert.match(css, /#format-rules-editor \[hidden\]\s*\{\s*display:\s*none;/);
    assert.match(css, /@container \(min-width: 42rem\)[^]*?#format-rules-editor \.settings-rule-fields[^]*?repeat\(2, minmax\(0, 1fr\)\)/);
    assert.match(css, /\.settings-header\s*\{[^}]*grid-template-columns:\s*max-content minmax\(0, 1fr\)/,
      'The heading retains its intrinsic width while the toolbar shrinks and wraps.');
    assert.match(css, /@container \(min-width: 48rem\)\s*\{\s*\.settings-template-layout[^}]*1\.65fr/,
      'Two-column email previews require a spacious card, including at native 200% zoom.');

    dom.window.close();
    console.log('Settings header hooks, Patron section order, save-bar styles, field layout, and dynamic labels passed');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
