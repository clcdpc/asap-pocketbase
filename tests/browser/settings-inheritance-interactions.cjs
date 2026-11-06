'use strict';
const assert = require('node:assert/strict');
const { decideConfirmation } = require('./confirmation.cjs');

// Runs inside the production GET/editor/POST journey. Only the temporary long
// System references are saved; the tested Library edits must all be discarded.
exports.verifyInheritanceInteractions = async (page, args, report, ready, scan) => {
  const read = scope => page.evaluate(async scope => {
    const response = await fetch(`/api/asap/staff/settings?orgId=${encodeURIComponent(scope)}`);
    if (!response.ok) throw new Error(`Inheritance witness GET ${response.status}`);
    return response.json();
  }, scope);
  const post = async values => {
    const current = await read('system');
    const result = await page.evaluate(async ({ values, version }) => {
      const session = await (await fetch('/api/asap/staff/session')).json();
      const response = await fetch('/api/asap/staff/settings', { method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-ASAP-Antiforgery': session.antiforgeryToken },
        body: JSON.stringify({ orgId: 'system', version, ...values }) });
      return { status: response.status, body: await response.text() };
    }, { values, version: current.version });
    assert.equal(result.status, 200, result.body);
  };
  const reload = async () => { await page.reload({ waitUntil: 'networkidle' }); await ready(args.scopeId); };
  const discard = async () => {
    await decideConfirmation(page, () => page.locator('#settings-discard').click(), dialog => dialog.accept());
    await page.waitForFunction(() => document.getElementById('settings-save-title').textContent === 'No changes' &&
      !document.getElementById('settings-form').inert);
  };
  const original = await read('system');
  const originalPrompt = original.stored.configuredSystem.patron.loginPrompt;
  const originalTemplate = original.stored.templates.find(row => row.organizationId === '1' && row.templateKey === 'suggestion_submitted');
  const longPrompt = 'System login reference. '.repeat(12);
  const longBody = 'System submission reference.\n'.repeat(12);
  const witness = report.inheritanceInteractionWitness = { comparisons: [], pointerEvents: [], libraryPosts: [] };
  const requests = request => {
    if (request.method() === 'POST' && new URL(request.url()).pathname === '/api/asap/staff/settings' &&
        request.postDataJSON()?.orgId === args.scopeId) witness.libraryPosts.push(request.postDataJSON());
  };
  page.on('request', requests);
  try {
    await post({ patron: { loginPrompt: longPrompt }, templates: [{ templateKey: 'suggestion_submitted', body: longBody }] });
    await reload();
    const accepted = await read(args.scopeId);
    await page.locator('#settings-nav-patron').click();
    assert.equal(await page.locator('#settings-discard').isHidden(), true);
    await page.locator('#patron-login-prompt').focus();
    assert.equal(await page.locator('#settings-discard').isHidden(), true, 'Focus alone creates no draft.');
    const scalar = page.locator('[data-setting-section="patron"][data-setting-key="loginPrompt"]');
    await page.locator('#patron-login-prompt').fill('Library prompt');
    const cases = [
      { name: 'Patron login prompt', field: scalar, input: page.locator('#patron-login-prompt'), system: longPrompt },
      { name: 'Submission confirmation Body', field: page.locator('[data-template-key="suggestion_submitted"] [data-template-status="body"]'),
        input: page.locator('#email-submit-body'), system: longBody }
    ];
    for (const item of cases) {
      if (item.name.startsWith('Submission')) {
        await page.locator('#settings-nav-templates').click();
        const card = page.locator('[data-template-key="suggestion_submitted"]');
        if (await card.locator('[data-template-toggle]').getAttribute('aria-expanded') !== 'true') await card.locator('[data-template-toggle]').click();
        await item.input.fill('Library body');
      }
      const comparison = item.field.locator('details.settings-system-comparison');
      await comparison.locator('summary').press('Enter');
      assert.equal(await comparison.evaluate(node => node.open), true);
      await item.input.focus(); await item.input.press('End'); await item.input.pressSequentially('x');
      witness.comparisons.push({ name: item.name, open: await comparison.evaluate(node => node.open),
        contentVisible: await comparison.locator('p').isVisible(), content: await comparison.locator('p').textContent(),
        expected: item.system, focused: await item.input.evaluate(node => document.activeElement === node),
        value: await item.input.inputValue(), status: await item.field.locator('.settings-inheritance-status').textContent() });
      await scan(`inheritance-${item.name.startsWith('Patron') ? 'scalar' : 'template'}-comparison`);
    }
    await discard();
    await page.setViewportSize({ width: 390, height: 844 });
    await page.locator('#settings-nav-patron').click();
    const title = page.locator('#patron-page-title');
    const acceptedTitle = await title.inputValue();
    await title.fill('Other guarded Library draft');
    await page.locator('#settings-nav-workflow').click();
    const provider = accepted.effective.externalSearchProviders.find(item => item.key === `audit_provider_inherited_${args.formatCode}`);
    assert.ok(provider);
    assert.equal(accepted.stored.libraryOverride.providers.some(item => item.id === provider.id), false);
    const row = page.locator(`#external-search-provider-editor [data-provider-id="${provider.id}"]`);
    const label = row.locator('input[type="text"]');
    await label.fill('Temporary provider draft');
    const reset = row.locator('[data-provider-status="label"] .settings-use-system');
    await reset.focus(); await reset.press('Enter');
    assert.equal(await label.inputValue(), provider.label);
    assert.equal(await label.evaluate(node => document.activeElement === node), true, 'Keyboard reset restores the editable field focus.');
    assert.match(await row.locator('[data-provider-status="label"]').textContent(), /Using system value.*Unsaved/);
    await scan('inheritance-mobile-provider-reset');
    await page.evaluate(() => {
      window.inheritancePointerEvents = [];
      for (const type of ['pointerdown', 'focusin', 'pointerup', 'click']) {
        document.addEventListener(type, event => {
          const button = document.getElementById('settings-discard'), bounds = button.getBoundingClientRect();
          window.inheritancePointerEvents.push({ type, target: event.target.id || event.target.className,
            y: bounds.y, height: bounds.height, active: document.activeElement?.id });
        }, true);
      }
    });
    await page.locator('#settings-discard').click();
    const dialog = page.locator('#confirmation-dialog[open]');
    witness.firstDiscardConfirmed = await dialog.waitFor({ state: 'visible', timeout: 2000 }).then(() => true, () => false);
    witness.pointerEvents = await page.evaluate(() => window.inheritancePointerEvents);
    if (witness.firstDiscardConfirmed) {
      await dialog.locator('.form-actions button').first().click();
      assert.equal(await label.inputValue(), provider.label);
      assert.equal(await page.locator('#settings-save').isEnabled(), true);
      await page.locator('#settings-nav-patron').click();
      assert.equal(await title.inputValue(), 'Other guarded Library draft');
      await page.locator('#settings-nav-workflow').click();
      // Repeat the exact field-reset/focus transition before the accepting click.
      await label.fill('Second temporary provider draft');
      await row.locator('[data-provider-status="label"] .settings-use-system').press('Enter');
      assert.equal(await label.evaluate(node => document.activeElement === node), true);
      await discard();
      assert.equal(await label.inputValue(), provider.label);
      assert.match(await row.locator('[data-provider-status="label"]').textContent(), /^Using system value$/);
      await page.locator('#settings-nav-patron').click();
      assert.equal(await title.inputValue(), acceptedTitle);
      assert.equal(await page.locator('#settings-discard').isHidden(), true);
      witness.cancelPreservedAndAcceptDiscardedBoth = true;
      await scan('inheritance-mobile-discard-accepted');
    }
    assert.deepEqual((await read(args.scopeId)).stored.libraryOverride, accepted.stored.libraryOverride,
      'Comparison editing and Discard leave the exact accepted SQL state unchanged.');
  } finally {
    page.off('request', requests);
    await post({ patron: { loginPrompt: originalPrompt }, templates: [{ templateKey: 'suggestion_submitted', body: originalTemplate.body }] });
    await page.setViewportSize({ width: 1280, height: 900 });
    await reload();
  }
  assert.deepEqual(witness.libraryPosts, [], 'No tested Library draft may send an unintended POST.');
  assert.equal(witness.firstDiscardConfirmed, true, JSON.stringify(witness.pointerEvents));
  assert.equal(witness.cancelPreservedAndAcceptDiscardedBoth, true);
  for (const item of witness.comparisons) {
    assert.equal(item.open, true, `${item.name}: typing keeps the expanded reference open.`);
    assert.equal(item.contentVisible, true); assert.equal(item.content, item.expected); assert.equal(item.focused, true);
    assert.match(item.status, /Library setting.*Unsaved/);
  }
};
