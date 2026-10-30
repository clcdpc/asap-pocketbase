'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { requireFixture } = require('../helpers/environment.cjs');
const { checkpoint, nativeZoom } = require('../helpers/accessibility.cjs');
const { detail, json, openFixture, actionButton, unchanged, submit } = require('../helpers/assertions.cjs');

const labels = ['Claim', 'Purchase', 'Assign', 'Already own', 'Close silently', 'Reject'];
const dialog = page => page.locator('#request-dialog[open]');
const status = page => page.locator('#request-dialog-status');
const fixture = async (t, key) => requireFixture((await t.verify()).evidence, key);
const evidence = (t, name, data) => fs.writeFile(path.join(t.directory, t.id + '-' + name + '.json'), JSON.stringify(data, null, 2));

async function toolbarGeometry(page, columns) {
  const bar = dialog(page).locator('.action-bar');
  await bar.scrollIntoViewIfNeeded();
  const geometry = await bar.evaluate(node => {
    const rect = n => { const r = n.getBoundingClientRect(); return { x: r.x, y: r.y, width: r.width, height: r.height, right: r.right, bottom: r.bottom }; };
    const frame = node.closest('dialog'), middle = node.closest('.request-middle');
    return { bar: rect(node), frame: rect(frame), scrollWidth: node.scrollWidth, clientWidth: node.clientWidth,
      middle: { ...rect(middle), scrollWidth: middle.scrollWidth, clientWidth: middle.clientWidth },
      footer: rect(frame.querySelector('.request-save-footer')), viewport: { width: innerWidth, height: innerHeight },
      buttons: [...node.children].map(n => {
        const range = document.createRange(); range.selectNodeContents(n);
        return { label: n.textContent.trim(), ...rect(n), text: [...range.getClientRects()].map(r => ({ x: r.x, y: r.y, right: r.right, bottom: r.bottom })) };
      }) };
  });
  assert.deepEqual(geometry.buttons.map(b => b.label), labels, 'All six direct actions retain DOM/keyboard order');
  assert.equal(await dialog(page).locator('.request-more, summary').count(), 0, 'No modal overflow menu');
  const rows = new Map();
  for (const b of geometry.buttons) {
    const y = Math.round(b.y); rows.set(y, (rows.get(y) || 0) + 1);
    assert.ok(b.height >= 44, 'Action has a 44px touch target');
    assert.ok(b.x >= geometry.bar.x - 1 && b.right <= geometry.bar.right + 1, 'Action fits its container');
    for (const text of b.text) assert.ok(text.x >= b.x - 1 && text.right <= b.right + 1 && text.y >= b.y - 1 && text.bottom <= b.bottom + 1, 'Complete action label fits');
  }
  assert.deepEqual([...rows.values()], Array(6 / columns).fill(columns), 'Expected responsive rows');
  assert.ok(geometry.scrollWidth <= geometry.clientWidth + 1 && geometry.middle.scrollWidth <= geometry.middle.clientWidth + 1, 'No internal horizontal overflow');
  assert.ok(geometry.footer.y >= geometry.frame.y && geometry.footer.bottom <= geometry.frame.bottom + 1, 'Save footer stays in the frame');
  if (columns === 6) assert.ok(geometry.buttons[5].right >= geometry.bar.right - 1, 'Reject is separated at the right');
  return geometry;
}

async function keyboardActions(page) {
  const controls = dialog(page).locator('.action-bar > button');
  await controls.first().focus();
  for (let i = 0; i < labels.length; i++) {
    assert.equal(await controls.nth(i).evaluate(n => n === document.activeElement), true, 'Logical keyboard order');
    assert.equal(await controls.nth(i).evaluate(n => {
      const s = getComputedStyle(n); return s.outlineStyle !== 'none' && parseFloat(s.outlineWidth) > 0 || s.boxShadow !== 'none';
    }), true, 'Visible keyboard focus');
    if (i < labels.length - 1) await page.keyboard.press('Tab');
  }
}

const scenarios = [
  { id: 'd1-toolbar-layout', requirements: ['#388: six direct actions, order, container geometry, keyboard, tabs, desktop/mobile/native 200%'], writes: false,
    run: async t => {
      const f = await fixture(t, 'draft'); await openFixture(t.page, t.origin, f);
      const before = await detail(t.context, t.origin, f);
      for (const [mode, width, height, columns] of [
        ['wide', 1280, 900, 6], ['medium', 740, 900, 3], ['narrow', 390, 844, 2], ['compact', 360, 800, 2],
        ['small', 320, 700, 1], ['short', 1280, 480, 6], ['zoom', 1280, 900, 3]
      ]) {
        await t.page.setViewportSize({ width, height });
        if (mode === 'zoom') await nativeZoom(t.context, t.page, 2);
        const geometry = await toolbarGeometry(t.page, columns);
        await keyboardActions(t.page);
        if (mode === 'zoom') {
          const cdp = await t.context.newCDPSession(t.page);
          try { geometry.cdp = await cdp.send('Page.getLayoutMetrics'); assert.equal(geometry.cdp.visualViewport.zoom, 2); }
          finally { await cdp.detach(); }
        }
        await evidence(t, mode + '-geometry', geometry);
        await checkpoint(t.page, t.directory, t.id + '-' + mode, { visible: [status(t.page), dialog(t.page).locator('.action-bar > button'), dialog(t.page).locator('.request-save-footer')] });
        if (mode === 'zoom') await nativeZoom(t.context, t.page, 1);
      }
      await t.page.setViewportSize({ width: 1280, height: 900 });
      for (const name of ['Activity', 'Related requests', 'Details']) {
        await dialog(t.page).getByRole('tab', { name, exact: true }).click();
        await toolbarGeometry(t.page, 6);
      }
      assert.equal(t.mutations.length, 0); await unchanged(t.context, t.origin, f, before);
    } },
  { id: 'd1-action-focus', requirements: ['#388: Purchase/Reject/Assign review, Escape/Cancel and initiating-button focus; no native confirmation'], writes: false,
    run: async t => {
      const f = await fixture(t, 'draft'); await openFixture(t.page, t.origin, f);
      const before = await detail(t.context, t.origin, f);
      for (const label of ['Purchase', 'Reject', 'Assign']) {
        const opener = await actionButton(t.page, label); await opener.click();
        const panel = dialog(t.page).locator(label === 'Assign' ? '.inline-form' : '.action-choice');
        await panel.waitFor();
        await panel.locator('button[type=submit]:not([disabled])').waitFor();
        if (label === 'Assign') await panel.getByRole('button', { name: 'Cancel', exact: true }).click();
        else await t.page.keyboard.press('Escape');
        await panel.waitFor({ state: 'hidden' });
        assert.equal(await opener.evaluate(n => n === document.activeElement), true, label + ' restores focus');
      }
      await (await actionButton(t.page, 'Close silently')).click();
      const confirmation = t.page.locator('#confirmation-dialog[open]'); await confirmation.waitFor();
      assert.equal(t.mutations.length, 0, 'Consent precedes any request write');
      await t.page.keyboard.press('Escape'); await confirmation.waitFor({ state: 'hidden' });
      assert.equal(await (await actionButton(t.page, 'Close silently')).evaluate(n => n === document.activeElement), true);
      await unchanged(t.context, t.origin, f, before);
      await checkpoint(t.page, t.directory, t.id, { visible: [dialog(t.page).locator('.action-bar')] });
    } },
  { id: 'd1-research-patron', requirements: ['#387: one compact research row, configured URLs/encoding/attributes and live drafts', '#408: one plus beside linked name, distinct patron fields'], writes: false,
    run: async t => {
      const f = await fixture(t, 'draft'); await openFixture(t.page, t.origin, f);
      const before = await detail(t.context, t.origin, f);
      const config = await json(t.context, t.origin, '/api/asap/staff/research-configuration?requestId=' + f.id);
      const row = dialog(t.page).locator('.polaris-edit-tools');
      const enabled = config.externalSearchProviders.filter(p => p.isEnabled !== false && p.label).sort((a,b) => a.sortOrder - b.sortOrder);
      assert.ok(enabled.length >= 3, 'Existing deterministic provider configuration is present');
      for (const p of enabled) await row.getByRole('link', { name: p.label, exact: true }).waitFor();
      assert.equal(await dialog(t.page).locator('.research-section').count(), 0);
      assert.equal(await dialog(t.page).getByRole('heading', { name: 'Catalog and research links' }).count(), 0);
      assert.equal(await row.evaluate(n => n.previousElementSibling.textContent.includes('BIB ID') && n.nextElementSibling.textContent.includes('Publication timing')), true);
      const title = dialog(t.page).getByLabel('Title', { exact: true });
      await title.fill('D1 café & research / title');
      await dialog(t.page).getByLabel('Identifier', { exact: true }).fill('D1/a&b');
      await dialog(t.page).getByLabel('BIB ID', { exact: true }).fill('941003');
      const urls = [];
      for (const p of enabled) {
        const a = row.getByRole('link', { name: p.label, exact: true });
        const expected = p.urlTemplate.replaceAll('{{title}}', encodeURIComponent('D1 café & research / title')).replaceAll('{{identifier}}', encodeURIComponent('D1/a&b'));
        assert.equal(await a.getAttribute('href'), new URL(expected).href);
        assert.equal(await a.getAttribute('target'), '_blank'); assert.equal(await a.getAttribute('rel'), 'noopener noreferrer');
        assert.equal(await dialog(t.page).getByRole('link', { name: p.label, exact: true }).count(), 1);
        urls.push({ label: p.label, url: await a.getAttribute('href') });
      }
      const patron = dialog(t.page).locator('.patron-context .detail-grid > div').filter({ has: t.page.locator('dt', { hasText: /^Patron$/ }) });
      const plus = patron.getByRole('button', { name: 'New suggestion for patron ' + before.barcode + ' from request ' + before.id, exact: true });
      assert.equal(await plus.count(), 1); assert.equal(await dialog(t.page).locator('.grid-new-suggestion').count(), 1);
      assert.equal(await plus.evaluate(n => n.parentElement.classList.contains('patron-name-actions') && n.previousElementSibling.textContent.trim().length > 0 && n.tagName === 'BUTTON'), true);
      assert.deepEqual(await dialog(t.page).locator('.patron-context dt').allTextContents(), ['Patron', 'Barcode', 'Email', 'Recorded ASAP pickup']);
      for (const mode of ['desktop', 'mobile']) {
        await t.page.setViewportSize(mode === 'mobile' ? { width: 390, height: 844 } : { width: 1280, height: 900 });
        await row.scrollIntoViewIfNeeded(); await checkpoint(t.page, t.directory, t.id + '-research-' + mode, { visible: [row] });
        await plus.scrollIntoViewIfNeeded(); await checkpoint(t.page, t.directory, t.id + '-patron-' + mode, { visible: [patron] });
      }
      await evidence(t, 'urls', urls);
      await unchanged(t.context, t.origin, f, before); assert.equal(t.mutations.length, 0);
    } },
  { id: 'd1-closed-locks', requirements: ['#400: fixed header Closed/reason, truthful stage/history copy, disabled versus editable fields, replacement and native zoom'], writes: false,
    run: async t => {
      for (const key of ['d1-closed', 'd1-protected']) {
        const f = await fixture(t, key); await openFixture(t.page, t.origin, f);
        const request = await detail(t.context, t.origin, f);
        assert.equal(request.status, 'closed');
        assert.equal(request.capabilities.canEditIdentifier, false); assert.equal(request.capabilities.canChangeBib, false);
        await status(t.page).filter({ hasText: /^Closed · / }).waitFor();
        const hint = dialog(t.page).locator('.identifier-lock-help');
        if (key === 'd1-closed') {
          assert.match(await hint.innerText(), /closed.*Identifier and BIB ID.*Reopen/);
          assert.doesNotMatch(await hint.innerText(), /previous|placement history/);
          assert.equal(await status(t.page).innerText(), 'Closed · Rejected');
          assert.equal(await (await actionButton(t.page, 'Reopen')).isEnabled(), true);
        } else {
          assert.match(await hint.innerText(), /previous hold placement.*can't be reopened/);
          assert.equal(await dialog(t.page).getByRole('button', { name: 'Reopen', exact: true }).count(), 0);
        }
        const fields = {};
        for (const label of ['Title', 'Author', 'Identifier', 'BIB ID']) {
          const input = dialog(t.page).getByLabel(label, { exact: true });
          fields[label] = await input.evaluate(n => ({ disabled: n.disabled, background: getComputedStyle(n).backgroundColor,
            described: n.getAttribute('aria-describedby') && document.getElementById(n.getAttribute('aria-describedby')).textContent }));
          assert.equal(fields[label].disabled, ['Identifier', 'BIB ID'].includes(label));
          if (fields[label].disabled) { assert.equal(fields[label].described, 'Locked'); assert.notEqual(fields[label].background, fields.Title.background); }
        }
        assert.equal(await dialog(t.page).locator('.detail-meta .status-badge').count(), 0);
        await evidence(t, key + '-fields', fields);
        for (const mode of ['desktop', 'mobile', 'zoom']) {
          await t.page.setViewportSize(mode === 'mobile' ? { width: 390, height: 844 } : { width: 1280, height: 900 });
          if (mode === 'zoom') await nativeZoom(t.context, t.page, 2);
          await hint.scrollIntoViewIfNeeded(); await checkpoint(t.page, t.directory, t.id + '-' + key + '-' + mode, { visible: [status(t.page), hint] });
          const input = dialog(t.page).getByLabel('Identifier', { exact: true });
          await input.scrollIntoViewIfNeeded(); await checkpoint(t.page, t.directory, t.id + '-' + key + '-fields-' + mode, { visible: [status(t.page), input] });
          if (mode === 'zoom') await nativeZoom(t.context, t.page, 1);
        }
      }
      await openFixture(t.page, t.origin, await fixture(t, 'draft'));
      assert.equal(await status(t.page).innerText(), 'Suggestion');
      await openFixture(t.page, t.origin, await fixture(t, 'copy'));
      assert.equal(await status(t.page).isVisible(), false, 'Additional-copy dialog has no stale title-request badge');
    } },
  { id: 'd1-activity', requirements: ['#402: newest timestamp, lossless bigint ties, every event once, original server chronology'], writes: false,
    run: async t => {
      const f = await fixture(t, 'd1-history'); await openFixture(t.page, t.origin, f);
      const before = await detail(t.context, t.origin, f);
      assert.equal(before.activity.length, 3);
      const expected = [...before.activity].sort((a,b) => Date.parse(b.created) - Date.parse(a.created) || (BigInt(a.id) > BigInt(b.id) ? -1 : 1));
      assert.ok(BigInt(expected[0].id) > BigInt(Number.MAX_SAFE_INTEGER));
      await dialog(t.page).getByRole('tab', { name: 'Activity', exact: true }).click();
      const list = dialog(t.page).locator('.request-activity ol');
      assert.deepEqual(await list.locator('li').evaluateAll(nodes => nodes.map(n => n.dataset.eventId)), expected.map(e => e.id));
      assert.equal(await list.getAttribute('reversed'), null);
      assert.equal(before.activity[0].message, 'D1 created', 'Authoritative DTO stays chronological');
      await evidence(t, 'order', { server: before.activity.map(e => ({ id: e.id, created: e.created })), displayed: expected.map(e => e.id) });
      await checkpoint(t.page, t.directory, t.id, { visible: [list] });
      await unchanged(t.context, t.origin, f, before);
    } },
  { id: 'd1-close-reopen-refresh', requirements: ['#400: Closed then Reopen immediately refreshes header/field capabilities', '#402: mutation history stays newest first'], writes: true,
    run: async t => {
      const f = await fixture(t, 'close-reopen'); t.allowFixture(f); await openFixture(t.page, t.origin, f);
      for (const [label, approve, state] of [['Close silently', 'Close silently', 'closed'], ['Reopen', 'Reopen request', 'suggestion']]) {
        await (await actionButton(t.page, label)).click();
        const confirm = t.page.locator('#confirmation-dialog[open]'); await confirm.waitFor();
        const response = await submit(t.page, confirm.getByRole('button', { name: approve, exact: true }), f, 200);
        assert.equal(response.committed, true); assert.equal(response.finalStatus, state);
        await status(t.page).filter({ hasText: state === 'closed' ? /^Closed · Closed silently$/ : /^Suggestion$/ }).waitFor();
        assert.equal(await dialog(t.page).getByLabel('Identifier', { exact: true }).isDisabled(), state === 'closed');
        const current = await detail(t.context, t.origin, f);
        await dialog(t.page).getByRole('tab', { name: 'Activity', exact: true }).click();
        const ids = await dialog(t.page).locator('.request-activity li').evaluateAll(nodes => nodes.map(n => n.dataset.eventId));
        assert.deepEqual(ids, [...current.activity].reverse().map(e => e.id));
        await checkpoint(t.page, t.directory, t.id + '-' + state, { visible: [status(t.page)] });
      }
      assert.equal(t.mutations.length, 2);
    } }
];

module.exports = { id: 'd1', requires: ['wslc-review-0.4.0', 'sql-server-2022', 'authorized-super-admin',
  'review-library-2', 'deterministic-providers', 'chromium-native-zoom'], mutations: true, scenarios,
  human: [['human-screen-reader', '#398: status description, Locked hints, action order and focus with NVDA/VoiceOver'],
    ['human-visual', '#398: desktop/mobile/200% visual acceptance, long content and forced colors']],
  uncovered: [['d1-remaining-provider-states', '#388; #400: role variants, incomplete hold/pickup and retained reopened protection',
    'Focused controller and retained SQL/browser tests cover these states; this disposable suite uses the authorized synthetic super-admin and adds only closed/protected/history fixtures.'],
    ['d1-patron-quick-create', '#408: quick-create completion/focus and live patron LEAP configuration',
      'Existing real SQL quick-create and LEAP browser journeys remain required; this read-only suite checks location/eligibility and never submits patron lookups or external navigation.']] };

module.exports.toolbarGeometry = toolbarGeometry;
