'use strict';
const assert = require('node:assert/strict');
const { decideConfirmation } = require('./confirmation.cjs');

async function geometry(card, stacked) {
  const result = await card.evaluate(node => {
    const rect = element => {
      const value = element.getBoundingClientRect();
      return { left: value.left, right: value.right, top: value.top, bottom: value.bottom, width: value.width };
    };
    const editor = node.querySelector('.settings-template-fields');
    const preview = node.querySelector('.settings-template-preview');
    const admin = node.querySelector('.settings-template-admin');
    return { card: rect(node), editor: rect(editor), preview: rect(preview), admin: rect(admin), viewport: innerWidth,
      devicePixelRatio, columns: getComputedStyle(node.querySelector('.settings-template-layout')).gridTemplateColumns,
      controls: [...editor.querySelectorAll('input, textarea, .template-placeholder-trigger')].map(rect),
      adminControls: [...admin.querySelectorAll('input, button')].map(rect) };
  });
  const diagnostic = JSON.stringify(result);
  for (const control of result.controls) {
    assert.ok(control.width > 0 && control.left >= result.editor.left - 1 && control.right <= result.editor.right + 1,
      `Each editor control fits its own column: ${JSON.stringify(result)}`);
    assert.ok(control.left >= 0 && control.right <= result.viewport + 1, `No child control is clipped by the viewport: ${diagnostic}`);
  }
  for (const control of result.adminControls) {
    assert.ok(control.width > 0 && control.left >= result.admin.left - 1 && control.right <= result.admin.right + 1 &&
      control.left >= 0 && control.right <= result.viewport + 1, `Administrative controls fit the card and viewport: ${diagnostic}`);
  }
  assert.ok(result.preview.left >= result.card.left && result.preview.right <= result.card.right + 1, diagnostic);
  if (stacked) assert.ok(result.preview.top >= result.editor.bottom, `Narrow layouts put the preview below its editor: ${diagnostic}`);
  else {
    assert.ok(result.preview.left >= result.editor.right, `Desktop preview sits beside the editor: ${diagnostic}`);
    const share = result.editor.width / (result.editor.width + result.preview.width);
    assert.ok(share >= .60 && share <= .65, `The desktop editor uses 60–65% of the two columns (${share}): ${diagnostic}`);
  }
  return result;
}

async function verifyEmailTemplateCards({ page, scan, report }) {
  const settleLayout = () => page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  await page.locator('#settings-nav-templates').click();
  const cards = page.locator('.settings-template-card');
  const count = await cards.count();
  assert.ok(count >= 5);
  assert.equal(await cards.locator('[data-template-toggle][aria-expanded="false"]').count(), count);
  const mutations = [];
  const trackMutation = request => { if (request.method() !== 'GET') mutations.push(request.url()); };
  page.on('request', trackMutation);
  const receipt = { contexts: [], nativeHistory: false, collapsedValidation: false, stableRows: false };
  try {
    const card = page.locator('[data-template-key="suggestion_submitted"]');
    const toggle = card.locator('[data-template-toggle]');
    await toggle.focus(); await toggle.press('Enter');
    const secondToggle = page.locator('[data-template-key="purchase_approved"] [data-template-toggle]');
    await secondToggle.press('Space');
    assert.equal(await toggle.getAttribute('aria-expanded'), 'true');
    assert.equal(await secondToggle.getAttribute('aria-expanded'), 'true');
    assert.equal(await page.locator('#settings-save').isDisabled(), true, 'Opening multiple cards is presentation only.');
    assert.deepEqual(mutations, []);
    const override = card.locator('.settings-template-override');
    const subject = page.locator('#email-submit-subject');
    const body = page.locator('#email-submit-body');
    const trigger = card.getByRole('button', { name: 'Insert placeholder into Submission confirmation Subject', exact: true });
    if (await override.count()) {
      assert.equal(await trigger.isDisabled(), true);
      assert.equal(await body.isDisabled(), true);
      await override.check();
    }
    const source = 'Prefix selected suffix';
    const inserted = 'Prefix {{title}} suffix';
    await subject.fill(source);
    await body.fill('<img src=x onerror=alert(1)>\nHello {{name}}: {{title}} {{unknown}}');
    assert.equal(await card.locator('.settings-template-preview-body').textContent(),
      '<img src=x onerror=alert(1)>\nHello Alex Reader: Example title {{unknown}}');
    assert.equal(await card.locator('.settings-template-preview img').count(), 0);
    await subject.evaluate(node => {
      node.setSelectionRange(7, 15);
      window.__emailTemplateInputTypes = [];
      node.addEventListener('input', event => window.__emailTemplateInputTypes.push(event.inputType));
    });
    await trigger.focus(); await trigger.press('Enter');
    const popup = page.locator('#email-submit-subject-placeholders');
    assert.deepEqual(await popup.locator('[data-placeholder-key]').evaluateAll(nodes => nodes.map(node => node.dataset.placeholderKey)),
      ['name', 'title', 'author', 'format', 'barcode', 'firstName', 'lastName']);
    await popup.locator('[data-placeholder-key="title"]').press('Enter');
    assert.equal(await subject.inputValue(), inserted);
    assert.equal(await subject.evaluate(node => node === document.activeElement), true);
    const adjacent = ' extra';
    const prefix = 'Prefix {{title}}';
    const suffix = ' suffix';
    const readAdjacent = async () => {
      const value = await subject.inputValue();
      assert.ok(value.startsWith(prefix) && value.endsWith(suffix), `Native history preserves the token and adjacent source: ${value}`);
      const remaining = value.slice(prefix.length, value.length - suffix.length);
      assert.ok(adjacent.startsWith(remaining), `Native history preserves an adjacent typing prefix: ${value}`);
      return remaining;
    };
    await page.keyboard.type(adjacent);
    let remaining = await readAdjacent();
    for (let step = 0; remaining.length; step++) {
      assert.ok(step < adjacent.length);
      await page.keyboard.press('Control+Z');
      const next = await readAdjacent();
      assert.ok(next.length < remaining.length); remaining = next;
    }
    await page.keyboard.press('Control+Z');
    assert.equal(await subject.inputValue(), source);
    assert.deepEqual(await subject.evaluate(node => [node.selectionStart, node.selectionEnd]), [7, 15]);
    await page.keyboard.press('Control+Shift+Z');
    assert.equal(await subject.inputValue(), inserted);
    remaining = await readAdjacent();
    for (let step = 0; remaining.length < adjacent.length; step++) {
      assert.ok(step < adjacent.length);
      await page.keyboard.press('Control+Shift+Z');
      const next = await readAdjacent();
      assert.ok(next.length > remaining.length); remaining = next;
    }
    assert.equal(await subject.inputValue(), `${prefix}${adjacent}${suffix}`);
    assert.deepEqual(await subject.evaluate(() => ['historyUndo', 'historyRedo'].map(type => window.__emailTemplateInputTypes.includes(type))), [true, true]);
    receipt.nativeHistory = true;
    await toggle.click();
    assert.equal(await subject.isVisible(), false);
    assert.match(await card.locator('.settings-template-subject-excerpt').textContent(), /{{title}} extra/);
    await toggle.click();
    assert.equal(await subject.inputValue(), `${prefix}${adjacent}${suffix}`);
    await trigger.scrollIntoViewIfNeeded(); await settleLayout(); await trigger.focus(); await trigger.press('Enter');
    const scrollBeforeDismissal = await page.evaluate(() => scrollY);
    await page.evaluate(() => window.scrollBy(0, scrollY > 0 ? -60 : 60));
    await page.waitForFunction(before => scrollY !== before &&
      document.querySelector('#email-submit-subject-placeholders')?.matches(':popover-open') === false, scrollBeforeDismissal);
    assert.equal(await trigger.evaluate(node => node === document.activeElement), true,
      'Real page scrolling dismisses the picker and restores its trigger focus.');
    assert.equal(await subject.inputValue(), `${prefix}${adjacent}${suffix}`);
    for (const [name, width, height, forced] of [
      ['email-template-desktop', 1280, 900, false], ['email-template-mobile', 390, 844, false],
      ['email-template-forced-colors', 1280, 900, true]
    ]) {
      await page.setViewportSize({ width, height });
      await page.emulateMedia({ forcedColors: forced ? 'active' : 'none' });
      await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      let layout;
      try { layout = await geometry(card, width === 390); }
      catch (error) { await scan(`${name}-geometry-failure`, true); throw error; }
      await trigger.scrollIntoViewIfNeeded(); await settleLayout(); await trigger.focus(); await trigger.press('Enter');
      assert.equal(await popup.evaluate(node => node.matches(':popover-open')), true);
      const bounds = await popup.boundingBox();
      assert.ok(bounds && bounds.x >= 0 && bounds.y >= 0 && bounds.x + bounds.width <= width && bounds.y + bounds.height <= height);
      await scan(name);
      assert.equal(await popup.evaluate(node => node.matches(':popover-open')), true, 'The viewport capture contains the open picker.');
      receipt.contexts.push({ name, layout, popup: bounds });
      await page.keyboard.press('Escape');
      assert.equal(await trigger.evaluate(node => node === document.activeElement), true);
    }
    await page.emulateMedia({ forcedColors: 'none' });
    if (await override.count()) {
      await override.uncheck();
      assert.equal(await subject.isDisabled(), true); assert.equal(await trigger.isDisabled(), true);
      assert.doesNotMatch(await card.locator('.settings-template-preview-subject').textContent(), / extra/);
      await override.check();
      assert.equal(await subject.inputValue(), `${prefix}${adjacent}${suffix}`);
    }
    await page.locator('#add-email-template').click();
    const added = page.locator('#email-templates-editor [data-template-id=""]').last();
    assert.equal(await added.locator('[data-template-toggle]').getAttribute('aria-expanded'), 'true');
    assert.equal(await added.locator('.template-display-name').evaluate(node => node === document.activeElement), true);
    await added.locator('.template-body').fill('Body with {{title}}');
    await added.locator('[data-template-toggle]').click();
    await page.locator('#settings-nav-workflow').click();
    await page.locator('#settings-save').click();
    assert.equal(await page.locator('#settings-templates').isVisible(), true);
    assert.equal(await added.locator('.template-subject').evaluate(node => node === document.activeElement), true);
    assert.equal(await added.locator('.template-subject').getAttribute('aria-invalid'), 'true');
    const templateError = await added.locator('[role="alert"]').textContent();
    assert.match(templateError, /Subject is required/);
    assert.equal(await page.locator('#settings-message').textContent(), templateError);
    assert.equal(await page.locator('#app-status').textContent(), templateError);
    await added.evaluate(node => node.scrollIntoView({ block: 'start' }));
    const invalidLayout = await geometry(added, false);
    await scan('email-template-invalid', true);
    receipt.contexts.push({ name: 'email-template-invalid', layout: invalidLayout });
    receipt.collapsedValidation = true;
    await added.locator('.template-subject').fill('Draft {{title}}');
    assert.equal(await added.locator('.template-subject').getAttribute('aria-invalid'), null);
    assert.equal(await added.locator('[role="alert"]').textContent(), '');
    assert.equal(await page.locator('#settings-message').textContent(), '', 'Correcting Subject clears its matching Settings validation notice.');
    assert.equal(await page.locator('#app-status').textContent(), '', 'Correcting Subject clears its matching shell validation notice.');
    for (const [name, width, height] of [['email-template-additional-desktop', 1280, 900], ['email-template-additional-mobile', 390, 844]]) {
      await page.setViewportSize({ width, height });
      await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      await added.evaluate(node => node.scrollIntoView({ block: 'start' }));
      let layout;
      try { layout = await geometry(added, width === 390); }
      catch (error) { await scan(`${name}-geometry-failure`, true); throw error; }
      await scan(name, true);
      receipt.contexts.push({ name, layout });
    }
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    const addedKey = await added.locator('.template-key').inputValue();
    const rowHandle = await added.elementHandle();
    await added.locator('.template-body').evaluate(node => node.setSelectionRange(0, 0));
    const addedTrigger = added.locator('.template-body').locator('xpath=..').locator('.template-placeholder-trigger');
    await addedTrigger.scrollIntoViewIfNeeded(); await settleLayout(); await addedTrigger.click();
    const addedPopupId = await addedTrigger.getAttribute('aria-controls');
    await page.waitForFunction(id => document.getElementById(id)?.matches(':popover-open') === true, addedPopupId);
    const retired = await page.locator('.template-placeholder-picker:popover-open [data-placeholder-key="title"]').elementHandle();
    // Use the stable UI ID after each move; array position is deliberately changing.
    const uiId = await rowHandle.getAttribute('data-template-ui-id');
    const moved = page.locator(`[data-template-ui-id="${uiId}"]`);
    while (await moved.locator('[aria-label="Move up"]').isEnabled()) {
      await moved.locator('[aria-label="Move up"]').click();
    }
    assert.equal(await moved.evaluate((node, original) => node === original, rowHandle), true);
    assert.equal(await moved.locator('.template-key').inputValue(), addedKey);
    assert.equal(await moved.locator('.template-body').inputValue(), 'Body with {{title}}');
    assert.equal(await moved.locator('[data-template-toggle]').evaluate(node => node === document.activeElement), true);
    await retired.evaluate(node => node.click());
    assert.equal(await moved.locator('.template-body').inputValue(), 'Body with {{title}}', 'Reordering retires the old picker target.');
    if (await moved.locator('[aria-label="Delete"]').count()) {
      await moved.locator('[aria-label="Delete"]').click();
      assert.equal(await page.evaluate(() => document.activeElement?.matches('[data-template-toggle], #add-email-template')), true);
    }
    receipt.stableRows = true;
    assert.deepEqual(mutations, [], 'Disclosure, editing, insertion, validation and row actions never autosave.');
  } finally {
    if (await page.locator('#settings-discard').isVisible()) {
      await decideConfirmation(page, () => page.locator('#settings-discard').click(), dialog => dialog.accept());
      await page.locator('#settings-discard').waitFor({ state: 'hidden' });
    }
    page.off('request', trackMutation);
    await page.emulateMedia({ forcedColors: 'none' });
    await page.setViewportSize({ width: 1280, height: 900 });
  }
  assert.equal(await cards.locator('[data-template-toggle][aria-expanded="false"]').count(), count);
  report.emailTemplateCards = receipt;
}

module.exports = { geometry, verifyEmailTemplateCards };
