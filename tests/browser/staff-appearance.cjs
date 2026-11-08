'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');
const { decideConfirmation, rejectNativeDialogs } = require('./confirmation.cjs');
const { nativeZoom } = require('../acceptance/helpers/accessibility.cjs');

// Use the same ancestor/opacity compositing as settings-accessibility.cjs.
// Transparent text nodes and mobile table cells must be checked as displayed.
async function assertStaffAppearance(page, scheme, name, { surfaces = [], text = [], controls = [], actions = [], disabledControls = [], disabledActions = [] } = {}, evidence = []) {
  const state = await page.evaluate(({ surfaces, text, controls, actions, disabledControls, disabledActions }) => {
    const rgba = value => {
      const values = value.match(/[\d.]+/g).map(Number);
      return [...values.slice(0, 3), values[3] ?? 1];
    };
    const over = (top, bottom) => {
      const alpha = top[3] + bottom[3] * (1 - top[3]);
      return [...top.slice(0, 3).map((value, index) => alpha ?
        (value * top[3] + bottom[index] * bottom[3] * (1 - top[3])) / alpha : 0), alpha];
    };
    const rendered = (node, color) => {
      for (let current = node; current; current = current.parentElement) {
        const style = getComputedStyle(current);
        color = over(color, rgba(style.backgroundColor));
        color[3] *= Number(style.opacity);
      }
      return over(color, [255, 255, 255, 1]);
    };
    const luminance = color => color.slice(0, 3).map(value => {
      const channel = value / 255;
      return channel <= .04045 ? channel / 12.92 : ((channel + .055) / 1.055) ** 2.4;
    }).reduce((sum, value, index) => sum + value * [.2126, .7152, .0722][index], 0);
    const ratio = (a, b) => (Math.max(luminance(a), luminance(b)) + .05) / (Math.min(luminance(a), luminance(b)) + .05);
    const sample = selector => {
      const node = [...document.querySelectorAll(selector)].find(node => node.checkVisibility());
      if (!node) return { selector, missing: true };
      const style = getComputedStyle(node), background = rendered(node, [0, 0, 0, 0]);
      const foreground = rendered(node, rgba(style.color));
      const boundary = rendered(node, rgba(style.borderTopColor));
      const outside = rendered(node.parentElement, [0, 0, 0, 0]);
      return { selector, background, foreground, luminance: luminance(background), contrast: ratio(foreground, background),
        boundaryContrast: Math.min(ratio(boundary, background), ratio(boundary, outside)),
        colorScheme: style.colorScheme, disabled: node.matches(':disabled'), opacity: Number(style.opacity),
        textSource: node.matches('input, textarea') ? node.value ? 'value' : 'empty' : 'content',
        displayedCharacters: (node.matches('input, textarea') ? node.value : node.textContent).trim().length };
    };
    return { body: sample('body'), surfaces: surfaces.map(sample), text: text.map(sample), controls: controls.map(sample), actions: actions.map(sample),
      disabledControls: disabledControls.map(sample), disabledActions: disabledActions.map(sample) };
  }, { surfaces, text, controls, actions, disabledControls, disabledActions });
  evidence.push({ name, scheme, ...state });
  for (const item of [state.body, ...state.surfaces, ...state.text, ...state.controls, ...state.actions, ...state.disabledControls, ...state.disabledActions]) {
    assert.equal(item.missing, undefined, `${name}: visible ${item.selector}`);
  }
  for (const item of [state.body, ...state.surfaces, ...state.controls, ...state.disabledControls]) {
    assert.ok(scheme === 'dark' ? item.luminance < .18 : item.luminance > .65,
      `${name}: ${item.selector} visibly resolves ${scheme}, luminance=${item.luminance}`);
  }
  for (const item of [state.body, ...state.text, ...state.controls, ...state.actions]) {
    // Disabled buttons are intentionally dimmed; informational text and native
    // disabled inputs are sampled independently without weakening text checks.
    if (!item.disabled) assert.ok(item.contrast >= 4.5, `${name}: ${item.selector} text contrast ${item.contrast}`);
  }
  for (const item of state.controls) {
    assert.equal(item.colorScheme, scheme, `${name}: ${item.selector} native scheme`);
    if (!item.disabled) assert.ok(item.boundaryContrast >= 3, `${name}: ${item.selector} boundary contrast ${item.boundaryContrast}`);
  }
  for (const item of state.disabledControls) {
    assert.equal(item.disabled, true, `${name}: ${item.selector} remains disabled in library scope`);
    assert.equal(item.textSource, 'value');
    assert.ok(item.displayedCharacters > 0, `${name}: ${item.selector} samples a displayed value, not an unused input foreground`);
    assert.equal(item.colorScheme, scheme, `${name}: ${item.selector} disabled native scheme`);
    // These normal-size System values remain information staff must read even
    // though library scope cannot edit them; preserve readable value text.
    assert.ok(item.contrast >= 4.5, `${name}: ${item.selector} disabled value contrast ${item.contrast}`);
  }
  for (const item of state.disabledActions) {
    assert.equal(item.disabled, true, `${name}: ${item.selector} remains unavailable in library scope`);
    assert.ok(item.displayedCharacters > 0);
    assert.equal(item.colorScheme, scheme, `${name}: ${item.selector} disabled action native scheme`);
    // Unavailable actions are intentionally dimmed. Retain a 3:1 label floor
    // separately from readable settings values and enabled-action contrast.
    assert.ok(item.contrast >= 3, `${name}: ${item.selector} disabled action contrast ${item.contrast}`);
  }
  assert.equal(state.body.colorScheme, scheme, `${name}: document native scheme`);
  return state;
}

async function verifyStaffAppearance({ page, baseURL, library, staffIds, scan, report, headers, artifacts }) {
  const writesBefore = report.writes.length;
  const settingsReady = () => page.waitForFunction(() => document.querySelector('#settings-version').value &&
    !document.querySelector('#settings-form').inert && document.querySelector('#settings-form').getAttribute('aria-busy') !== 'true' &&
    !document.querySelector('#settings-refresh').disabled && !document.querySelector('#staff-refresh').disabled);
  const operationsReady = () => page.waitForFunction(() => {
    if (document.querySelector('#refresh-operations').disabled) return false;
    const section = document.querySelector('[data-operations-section][aria-selected="true"]').dataset.operationsSection;
    if (section === 'workflow') return Boolean(document.querySelector('#queue-progress-table td'));
    const target = document.querySelector(section === 'email' ? '#email-operations-table' : '#audit-results');
    return target.getAttribute('aria-busy') !== 'true' && Boolean(target.querySelector('td')) &&
      document.querySelector(section === 'email' ? '#email-count' : '#audit-count').textContent.includes('matching');
  });
  const analyticsReady = () => page.waitForFunction(() => document.querySelector('.analytics-card') &&
    document.querySelector('.analytics-results').getAttribute('aria-busy') !== 'true' &&
    document.querySelector('.analytics-status').textContent.includes('Analytics report updated.'));
  const profileReady = () => page.waitForFunction(() => document.querySelector('#notification-email').textContent &&
    document.querySelector('#profile-form').getAttribute('aria-busy') !== 'true' && !document.querySelector('#weekly-email').disabled);
  const searchReady = quick => page.waitForFunction(quick => {
    const list = document.querySelector(quick ? '#search-suggestions' : '#search-view [data-search="list"]');
    return list.getAttribute('aria-busy') === 'false' && Boolean(list.querySelector('.search-result'));
  }, quick);
  const goto = async query => { await page.goto(`${baseURL}/staff/?${query}`, { waitUntil: 'networkidle' }); };
  const tab = async (name, selector) => {
    await page.locator(name === 'profile' ? '#profile-button' : `.view-tab[data-view="${name}"]`).click();
    await page.locator(selector).waitFor();
    if (name === 'operations') await operationsReady();
    if (name === 'settings') await settingsReady();
    if (name === 'profile') await profileReady();
    if (name === 'queue' || name === 'additional-copies') {
      await page.waitForFunction(copy => !document.querySelector(copy ? '#refresh-additional-copies' : '#refresh-queue').disabled &&
        document.querySelector(copy ? '#additional-copy-grid td' : '#request-grid td'), name === 'additional-copies');
    }
  };
  const row = id => page.locator(`.settings-staff-row[data-staff-id="${id}"]`);
  for (const scheme of ['dark', 'light']) {
    await page.emulateMedia({ colorScheme: scheme });
    for (const width of [1280, 390]) {
      await page.setViewportSize({ width, height: 900 });
      const prefix = `appearance-${scheme}-${width}`;
      const check = async (name, samples, capture = true, resolved = scheme) => {
        await assertStaffAppearance(page, resolved, `${prefix}-${name}`, samples, report.appearance);
        if (capture) await scan(`${prefix}-${name}`);
      };
      await goto(`stage=suggestion&scope=${library}`);
      await page.locator('#request-grid .request-title-link').first().waitFor();
      await check('direct-requests', { surfaces: ['#request-grid td', '#request-grid th', '#request-grid .gridjs-footer'],
        text: ['#request-grid .request-title-link', '#request-grid .status-badge'], controls: ['#request-search'], actions: ['#new-suggestion'] });
      await page.reload({ waitUntil: 'networkidle' });
      await check('requests-refresh', { surfaces: ['#request-grid td'], controls: ['#claim-filter'] }, false);
      for (const status of ['outstanding_purchase', 'pending_hold', 'hold_placed', 'closed', 'suggestion']) {
        await page.locator(`#status-tabs [data-status="${status}"]`).click();
        await page.locator('#request-grid .request-title-link').filter({ hasText: status === 'suggestion' ? 'Remediation title 0' : `Remediation ${status} title` }).waitFor();
        await page.waitForFunction(() => !document.querySelector('#refresh-queue').disabled);
        await check(`requests-${status}`, { surfaces: ['#request-grid td'], text: ['#request-grid .status-badge', '#request-grid .request-title-link'] }, false);
      }
      const more = page.locator('#request-grid .request-row-more').first();
      await more.click(); await page.locator('.request-table-menu:popover-open').waitFor();
      await check('request-menu', { surfaces: ['.request-table-menu'], text: ['.request-table-menu button'] });
      await page.keyboard.press('Escape');
      assert.equal(await more.evaluate(node => node === document.activeElement), true, 'Menu Escape restores its opener.');

      await page.locator('#request-grid .request-title-link').filter({ hasText: 'Remediation title 0' }).click();
      const detail = page.locator('#request-dialog[open]'); await detail.waitFor();
      await page.reload({ waitUntil: 'networkidle' }); await detail.waitFor();
      await check('direct-detail-refresh', { surfaces: ['#request-dialog .dialog-header', '.request-save-footer'],
        controls: ['#request-dialog textarea'] }, false);
      await detail.getByRole('tab', { name: 'Activity', exact: true }).click();
      await page.locator('.request-activity-email').first().waitFor();
      await check('email-activity', { surfaces: ['#request-dialog .dialog-header', '.request-activity-email', '.request-save-footer'],
        text: ['.request-activity-email > strong', '.request-email-history-note'] });
      await detail.getByRole('tab', { name: 'Details', exact: true }).click();
      const notes = detail.locator('.edit-form textarea[maxlength="10000"]');
      await notes.fill('Retained request appearance draft'); await notes.focus();
      await notes.evaluate(node => node.setSelectionRange(7, 15));
      const notesHandle = await notes.elementHandle();
      const other = scheme === 'dark' ? 'light' : 'dark';
      await page.emulateMedia({ colorScheme: other });
      assert.equal(await notes.evaluate((node, saved) => node === saved, notesHandle), true);
      assert.deepEqual(await notes.evaluate(node => [node.value, node.selectionStart, node.selectionEnd, document.activeElement === node]),
        ['Retained request appearance draft', 7, 15, true], 'Preference changes keep the mounted request draft, selection and focus.');
      await check(`detail-live-${other}`, { surfaces: ['#request-dialog .dialog-shell', '.request-save-footer'],
        text: ['#request-dialog .field-help'], controls: ['#request-dialog textarea'], actions: ['#request-dialog .primary-button:not(:disabled)'] }, true, other);
      await page.emulateMedia({ colorScheme: scheme });
      await notes.press('End'); await notes.pressSequentially('x'); await notes.press('Control+z');
      assert.equal(await notes.inputValue(), 'Retained request appearance draft', 'Native Undo survives a preference change.');
      await decideConfirmation(page, () => page.locator('#close-request').click(), async decision => {
        await check('detail-discard-canceled', { surfaces: ['#confirmation-dialog .dialog-shell'],
          text: ['#confirmation-message'], actions: ['#confirmation-dialog .danger-button'] });
        await decision.dismiss();
      });
      assert.equal(await notes.inputValue(), 'Retained request appearance draft');
      await decideConfirmation(page, () => page.locator('#close-request').click(), decision => decision.accept());
      await detail.waitFor({ state: 'hidden' });
      await page.locator('#new-suggestion').click(); await page.locator('#staff-suggestion-dialog[open]').waitFor();
      await check('new-suggestion', { surfaces: ['#staff-suggestion-dialog .dialog-shell'], controls: ['#staff-suggestion-dialog input'] });
      await page.keyboard.press('Escape'); await page.locator('#staff-suggestion-dialog').waitFor({ state: 'hidden' });

      // Operations must relinquish only its visibility, never the shared palette.
      await goto(`stage=operations&opSection=workflow&runScope=${library}&emailScope=${library}&auditScope=${library}`);
      await operationsReady();
      await check('operations-workflow', { surfaces: ['.operations-panel'], text: ['#queue-progress-table td'], controls: ['#operations-scope'] });
      await tab('additional-copies', '#additional-copy-view');
      await page.locator('#additional-copy-library-scope').selectOption(library);
      await page.locator('#additional-copy-grid .additional-copy-title').filter({ hasText: 'Remediation open additional copy' }).waitFor();
      await page.waitForFunction(() => !document.querySelector('#refresh-additional-copies').disabled);
      await check('operations-to-copies', { surfaces: ['#additional-copy-grid td', '#additional-copy-grid .gridjs-footer'],
        text: ['.additional-copy-title'], controls: ['#additional-copy-search'] });
      await page.locator('#additional-copy-status-tabs [data-copy-status="closed"]').click();
      await page.locator('#additional-copy-grid .additional-copy-title').filter({ hasText: 'Remediation closed additional copy' }).waitFor();
      await page.waitForFunction(() => !document.querySelector('#refresh-additional-copies').disabled);
      await check('copies-closed', { surfaces: ['#additional-copy-grid td'], text: ['.additional-copy-title'] }, false);
      await page.locator('#additional-copy-grid .additional-copy-open').first().click(); await detail.waitFor();
      await check('copy-detail', { surfaces: ['#request-dialog .dialog-shell', '#request-dialog .dialog-header'], text: ['#request-dialog .notes-history'] });
      await page.keyboard.press('Escape'); await detail.waitFor({ state: 'hidden' });
      await tab('operations', '#operations-view');
      await check('copies-to-operations', { surfaces: ['.operations-panel'], controls: ['#operations-scope'] }, false);
      for (const section of ['email', 'audit']) {
        await page.locator(`#operations-${section}-tab`).click(); await operationsReady();
        await check(`operations-${section}`, { surfaces: [`#${section}-filters`, section === 'email' ? '#email-operations-table' : '#audit-results'],
          text: [section === 'email' ? '#email-operations-table td' : '#audit-results td'], controls: [`#${section}-search`] });
        if (section === 'audit') {
          await page.locator('#audit-results summary').first().click();
          await check('audit-expanded', { surfaces: ['.audit-detail'], text: ['.audit-detail dd'] }, false);
        }
      }

      await goto(`stage=analytics&scope=${library}`);
      await analyticsReady();
      await page.locator('#analytics-date-range').selectOption('custom');
      await page.locator('#analytics-start-date').fill('2026-10-01'); await page.locator('#analytics-end-date').fill('2026-10-31');
      await check('analytics-custom-dates', { surfaces: ['.analytics-card', '.analytics-panel'],
        text: ['.analytics-range-summary', '.analytics-card-label'], controls: ['#analytics-start-date', '#analytics-end-date'], actions: ['#analytics-apply-range'] });
      await page.locator('#analytics-end-date').fill('2026-09-30'); await page.locator('#analytics-apply-range').click();
      await page.locator('.analytics-field-error:not([hidden])').first().waitFor();
      await check('analytics-invalid', { text: ['.analytics-field-error:not([hidden])'], controls: ['#analytics-start-date'] }, false);

      await tab('profile', '#profile-view');
      await check('profile', { surfaces: ['.profile-form'], text: ['#notification-email', '#weekly-email-help'], controls: ['#weekly-email'] });
      await tab('queue', '#queue-view'); await page.goBack({ waitUntil: 'networkidle' }); await page.locator('#profile-view').waitFor(); await profileReady();
      await check('history-back-profile', { surfaces: ['.profile-form'], controls: ['#weekly-email'] }, false);
      await page.goForward({ waitUntil: 'networkidle' }); await page.locator('#queue-view').waitFor();
      await page.waitForFunction(() => !document.querySelector('#refresh-queue').disabled && document.querySelector('#request-grid td'));
      await check('history-forward-requests', { surfaces: ['#request-grid td'], controls: ['#request-search'] }, false);

      await page.locator('#search-asap').click(); await page.locator('#search-dialog[open]').waitFor();
      await page.locator('#search-quick-input').fill('Remediation');
      await searchReady(true);
      await page.keyboard.press('ArrowDown');
      await check('search-quick', { surfaces: ['#search-dialog', '#search-suggestions .search-result'],
        text: ['#search-suggestions .search-result-meta'], controls: ['#search-quick-input'] });
      assert.equal(await page.locator('#search-dialog [data-search="all"]').isHidden(), true, 'The compact fixture fits the quick preview.');
      await page.keyboard.press('Escape'); await page.locator('#search-dialog').waitFor({ state: 'hidden' });
      await goto(`stage=search&scope=${library}`); await page.locator('#search-view').waitFor();
      await page.locator('#search-results-input').fill('Remediation');
      await searchReady(false);
      await check('search-full', { surfaces: ['#search-view'], text: ['#search-view .search-result-meta'], controls: ['#search-results-input'] });

      for (const scope of ['system', library]) {
        await goto(`stage=settings&settingsScope=${scope}#settings-staff`); await settingsReady();
        await page.locator('#staff-show-inactive').check();
        for (const id of staffIds) {
          if (await row(id).locator('.settings-staff-summary').getAttribute('aria-expanded') !== 'true') await row(id).locator('.settings-staff-summary').click();
        }
        await check(`roster-scope-${scope}`, { surfaces: ['.settings-staff-list', '.settings-staff-editor', '.settings-save-bar'],
          text: ['.settings-staff-email', '.settings-staff-row .status-badge', '#staff-audit-history'],
          controls: [`[data-staff-id="${staffIds[0]}"] input`, '#settings-scope'], actions: ['#staff-add-toggle', '#settings-nav-staff'] });
        await page.locator('#staff-add-toggle').click();
        await check(`add-staff-scope-${scope}`, { surfaces: ['#staff-add-form'], controls: ['#staff-add-email'] }, false);
        await page.locator('#staff-add-cancel').click();
        if (scope === 'system' && width === 1280) {
          for (const section of ['start', 'polaris', 'smtp', 'workflow', 'patron', 'templates']) {
            await page.locator(`#settings-nav-${section}`).click();
            await check(`system-settings-${section}`, { surfaces: [`#settings-${section} .settings-fieldset, #settings-${section} .settings-template-card`],
              text: [`#settings-${section} .settings-panel-heading p`],
              controls: [`#settings-${section} input:not([type="checkbox"]):not([type="hidden"]):not(:disabled)`] }, false);
          }
        }
      }
      const display = row(staffIds[0]).getByRole('textbox', { name: /^Display name/ });
      const secondDisplay = row(staffIds[1]).getByRole('textbox', { name: /^Display name/ });
      const savedName = await display.inputValue(), savedSecondName = await secondDisplay.inputValue();
      await display.fill('Mounted roster appearance draft'); await secondDisplay.fill('Independent second roster draft');
      await display.focus(); await display.evaluate(node => node.setSelectionRange(3, 11));
      const mounted = await display.elementHandle(); await page.emulateMedia({ colorScheme: other });
      assert.equal(await display.evaluate((node, saved) => node === saved, mounted), true);
      assert.deepEqual(await display.evaluate(node => [node.value, node.selectionStart, node.selectionEnd, document.activeElement === node]),
        ['Mounted roster appearance draft', 3, 11, true]);
      assert.equal(await secondDisplay.inputValue(), 'Independent second roster draft');
      await check(`roster-live-${other}`, { surfaces: ['.settings-staff-list', '.settings-staff-editor'],
        controls: [`[data-staff-id="${staffIds[0]}"] input`], text: ['.settings-staff-unsaved:not([hidden])'] }, true, other);
      await page.emulateMedia({ colorScheme: scheme });
      await page.locator('#settings-nav-workflow').click();
      await page.locator('#suggestion-limit-message').fill('Global appearance draft with two roster blockers');
      await page.locator('#settings-save').click();
      await page.waitForFunction(() => document.querySelectorAll('#settings-draft-blockers button').length === 2);
      await check('settings-draft-blockers', { surfaces: ['#settings-draft-blockers'],
        text: ['#settings-message', '#settings-draft-blockers button'] });
      await decideConfirmation(page, () => page.locator('#operations-view-tab').click(), async decision => {
        await check('settings-departure-canceled', { surfaces: ['#confirmation-dialog .dialog-shell'],
          text: ['#confirmation-message'], actions: ['#confirmation-dialog .danger-button'] });
        await decision.dismiss();
      });
      await page.locator('#settings-nav-staff').click();
      assert.equal(await display.inputValue(), 'Mounted roster appearance draft');
      assert.equal(await secondDisplay.inputValue(), 'Independent second roster draft');
      await decideConfirmation(page, () => page.locator('#operations-view-tab').click(), decision => decision.accept());
      await page.locator('#operations-view').waitFor();
      await tab('settings', '#settings-view'); await page.locator('#settings-nav-staff').click(); await settingsReady();
      assert.equal(await display.inputValue(), savedName); assert.equal(await secondDisplay.inputValue(), savedSecondName);

      // Every Settings section shares the same shell; exercise disabled system
      // fields in library context and the final compact/picker/template controls.
      for (const section of width === 1280 ? ['start', 'polaris', 'smtp', 'workflow', 'patron', 'templates'] : ['workflow', 'templates']) {
        await page.locator(`#settings-nav-${section}`).click();
        const disabledSamples = section === 'start' ? {
          disabledControls: ['#ui-system-not-enabled-msg', '#ui-misconfigured-msg'],
          help: ['#ui-system-not-enabled-help', '#ui-misconfigured-help'], disabledActions: ['#manage-library-participation']
        } : section === 'polaris' ? {
          disabledControls: [], help: ['#settings-polaris .settings-scope-explanation', '#polaris-api-key-status'],
          disabledActions: ['#btn-test-polaris', '#btn-sync-organizations']
        } : { disabledControls: [], help: [], disabledActions: [] };
        await check(`settings-${section}`, { surfaces: [`#settings-${section} .settings-fieldset, #settings-${section} .settings-template-card`],
          text: [`#settings-${section} .settings-panel-heading p`, ...disabledSamples.help], controls: ['#settings-scope'],
          disabledControls: disabledSamples.disabledControls, disabledActions: disabledSamples.disabledActions }, disabledSamples.help.length > 0);
      }
      await page.locator('.settings-template-toggle').first().click();
      await page.locator('.template-placeholder-trigger').first().click();
      await page.locator('.template-placeholder-picker:popover-open').waitFor();
      await check('template-placeholder', { surfaces: ['.settings-template-card', '.template-placeholder-picker:popover-open'],
        text: ['.template-placeholder-picker:popover-open button'] });
      await page.keyboard.press('Escape');
      await page.locator('#settings-nav-workflow').click();
      const note = page.locator('#suggestion-limit-message');
      const original = await note.inputValue(); await note.fill('Retained settings appearance draft'); await note.focus();
      await note.evaluate(node => node.setSelectionRange(7, 15));
      await page.locator('#suggestion-limit-placeholder-trigger').click();
      await page.locator('#suggestion-limit-placeholder-picker:popover-open').waitFor();
      await page.emulateMedia({ colorScheme: other });
      assert.equal(await note.inputValue(), 'Retained settings appearance draft');
      assert.deepEqual(await note.evaluate(node => [node.selectionStart, node.selectionEnd]), [7, 15]);
      await check(`settings-popover-live-${other}`, { surfaces: ['#suggestion-limit-placeholder-picker', '.settings-save-bar'],
        text: ['#suggestion-limit-placeholder-picker button'], controls: ['#suggestion-limit-message'] }, true, other);
      await page.emulateMedia({ colorScheme: scheme }); await page.keyboard.press('Escape');
      await decideConfirmation(page, () => page.locator('#settings-discard').click(), decision => decision.accept()); await settingsReady();
      assert.equal(await note.inputValue(), original);
      if (width === 390) {
        await page.setViewportSize({ width, height: 420 });
        await page.locator('#suggestion-limit-message').fill('Short height appearance draft');
        await page.locator('#settings-save').focus();
        const footer = await page.locator('.settings-save-bar').boundingBox();
        assert.ok(footer && footer.y >= 0 && footer.y + footer.height <= 421, 'Short-height Save/Discard footer fits the viewport.');
        await check('short-height-footer', { surfaces: ['.settings-save-bar'], controls: ['#suggestion-limit-message'] });
        await decideConfirmation(page, () => page.locator('#settings-discard').click(), decision => decision.accept()); await settingsReady();
        await page.setViewportSize({ width, height: 900 });
      }
      await page.locator('#settings-nav-staff').click();
      await page.emulateMedia({ forcedColors: 'active' });
      const nativePair = await page.evaluate(() => {
        const probe = document.createElement('span');
        probe.style.color = 'ButtonText'; probe.style.background = 'ButtonFace'; probe.hidden = true;
        document.body.append(probe);
        const native = getComputedStyle(probe), selected = getComputedStyle(document.querySelector('#settings-nav-staff'));
        const result = { nativeText: native.color, nativeFace: native.backgroundColor,
          selectedFill: selected.webkitTextFillColor, selectedFace: selected.backgroundColor,
          actionFill: getComputedStyle(document.querySelector('#staff-add-toggle')).webkitTextFillColor };
        probe.remove(); return result;
      });
      // Native buttons paint a ButtonFace text backplate in forced colors.
      // HighlightText can contrast with the authored fill while disappearing
      // on that actual backplate; retain the readable native pair explicitly.
      assert.equal(nativePair.selectedFill, nativePair.nativeText, 'Selected Settings label contrasts with its native text backplate.');
      assert.equal(nativePair.selectedFace, nativePair.nativeFace);
      assert.equal(nativePair.actionFill, nativePair.nativeText, 'Filled action uses resolved system text, including its text fill.');
      report.appearance.push({ name: `${prefix}-forced-native-pairs`, scheme, nativePair });
      await display.focus(); await page.keyboard.press('Tab');
      assert.equal(await page.locator(':focus-visible').count(), 1);
      assert.ok(await page.locator(':focus-visible').evaluate(node => parseFloat(getComputedStyle(node).outlineWidth) >= 2));
      await scan(`${prefix}-forced-colors-roster`);
      await page.emulateMedia({ forcedColors: 'none' });
    }
  }
  assert.equal(report.writes.length, writesBefore, 'Appearance, navigation, search and discarded drafts make no writes.');
  await page.setViewportSize({ width: 1280, height: 900 });
  await goto(`stage=settings&settingsScope=${library}#settings-staff`); await settingsReady();
  await page.locator('#staff-show-inactive').check();
  for (const id of staffIds) {
    if (await row(id).locator('.settings-staff-summary').getAttribute('aria-expanded') !== 'true') await row(id).locator('.settings-staff-summary').click();
  }
  await verifyAppearanceNativeZoom({ baseURL, library, staffIds, headers, artifacts, report });
  assert.equal(report.writes.length, writesBefore, 'Native zoom and preference changes make no writes.');
}

async function verifyAppearanceNativeZoom({ baseURL, library, staffIds, headers, artifacts, report }) {
  const extension = path.join(__dirname, 'native-zoom-extension');
  const axeSource = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  for (const scheme of ['dark', 'light']) {
    const context = await chromium.launchPersistentContext(path.join(artifacts, `appearance-zoom-profile-${scheme}`), {
      channel: 'chromium', headless: true, viewport: null, colorScheme: scheme, extraHTTPHeaders: headers,
      args: [`--disable-extensions-except=${extension}`, `--load-extension=${extension}`, '--window-size=1280,900']
    });
    try {
      await context.route('**/*', route => {
        if (new URL(route.request().url()).origin === new URL(baseURL).origin) return route.continue();
        report.external.push(route.request().url()); return route.abort();
      });
      context.on('request', request => {
        const pathname = new URL(request.url()).pathname;
        if (!['GET', 'HEAD'].includes(request.method()) && !(pathname === '/api/asap/staff/search' && request.method() === 'POST')) {
          report.writes.push({ method: request.method(), path: pathname });
        }
      });
      const page = context.pages()[0] || await context.newPage();
      page.on('pageerror', error => report.errors.push(error.message)); rejectNativeDialogs(page, report.errors);
      const check = async (name, samples, visible) => {
        await nativeZoom(context, page, 2);
        await visible.scrollIntoViewIfNeeded();
        const label = `appearance-native-200-${scheme}-${name}`;
        await assertStaffAppearance(page, scheme, label, samples, report.appearance);
        await page.evaluate(axeSource);
        const state = await page.evaluate(async () => ({
          violations: (await axe.run()).violations.filter(item => ['serious', 'critical'].includes(item.impact)),
          overflow: document.documentElement.scrollWidth > innerWidth,
          missingImages: [...document.images].filter(node => node.checkVisibility() && (!node.complete || !node.naturalWidth)).map(node => node.id),
          viewport: { width: innerWidth, height: innerHeight, devicePixelRatio, scale: visualViewport.scale }
        }));
        await visible.scrollIntoViewIfNeeded();
        const bounds = await visible.boundingBox();
        assert.ok(bounds && bounds.x >= -1 && bounds.y >= -1 && bounds.x + bounds.width <= state.viewport.width + 1 &&
          bounds.y + bounds.height <= state.viewport.height + 1, `${label}: active control fits native viewport`);
        assert.deepEqual(state.violations, [], `${label}: axe`); assert.equal(state.overflow, false, `${label}: overflow`);
        assert.deepEqual(state.missingImages, [], `${label}: missing images`);
        const cdp = await context.newCDPSession(page);
        try {
          const metrics = await cdp.send('Page.getLayoutMetrics');
          assert.equal(metrics.visualViewport.zoom, 2);
          const capture = await cdp.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
          await fs.writeFile(path.join(artifacts, `${label}.png`), Buffer.from(capture.data, 'base64'));
          report.states.push({ name: label, actualBrowserZoom: 2, bounds, metrics, ...state });
        } finally { await cdp.detach(); }
      };
      await page.goto(`${baseURL}/staff/?stage=settings&settingsScope=${library}#settings-staff`, { waitUntil: 'networkidle' });
      await page.waitForFunction(() => document.querySelector('#settings-version').value && !document.querySelector('#settings-form').inert &&
        document.querySelector('#settings-form').getAttribute('aria-busy') !== 'true' && !document.querySelector('#staff-refresh').disabled);
      await page.locator('#staff-show-inactive').check();
      for (const id of staffIds) await page.locator(`[data-staff-id="${id}"] .settings-staff-summary`).click();
      const display = page.locator(`[data-staff-id="${staffIds[0]}"] input`).first();
      await display.focus();
      await check('expanded-roster', { surfaces: ['.settings-staff-list', '.settings-staff-editor'], controls: [`[data-staff-id="${staffIds[0]}"] input`] }, display);
      await page.locator('.view-tab[data-view="additional-copies"]').click();
      await page.locator('#additional-copy-view').waitFor();
      await page.locator('#additional-copy-library-scope').selectOption(library);
      const copy = page.locator('#additional-copy-grid .additional-copy-open').first(); await copy.waitFor();
      await page.waitForFunction(() => !document.querySelector('#refresh-additional-copies').disabled && document.querySelector('#additional-copy-grid td'));
      await check('copy-cards', { surfaces: ['#additional-copy-grid td'], controls: ['#additional-copy-search'], text: ['.additional-copy-title'] }, copy);
      await copy.click(); await page.locator('#request-dialog[open]').waitFor();
      await check('copy-detail', { surfaces: ['#request-dialog .dialog-shell', '#request-dialog .dialog-header'],
        text: ['#request-dialog .notes-history'] }, page.locator('#close-request'));
      await page.keyboard.press('Escape');
      await page.goto(`${baseURL}/staff/?stage=analytics&scope=${library}`, { waitUntil: 'networkidle' });
      await page.waitForFunction(() => document.querySelector('.analytics-card') && document.querySelector('.analytics-results').getAttribute('aria-busy') !== 'true');
      await page.locator('#analytics-date-range').selectOption('custom');
      await check('custom-dates', { surfaces: ['.analytics-card'], controls: ['#analytics-start-date', '#analytics-end-date'] }, page.locator('#analytics-start-date'));
    } finally { await context.close(); }
  }
}

module.exports = { assertStaffAppearance, verifyStaffAppearance };
