'use strict';

exports.waitForConfirmation = async page => {
  const root = page.locator('#confirmation-dialog[open]');
  await root.waitFor();
  const message = await root.locator('#confirmation-message').textContent();
  return {
    message: () => message,
    accept: () => root.locator('.form-actions button').last().click(),
    dismiss: () => root.locator('.form-actions button').first().click()
  };
};

const decisions = new WeakMap();
exports.answerNextConfirmation = (page, respond) => {
  const origin = new Error('Confirmation requested here').stack;
  const decision = (decisions.get(page) || Promise.resolve()).then(() => exports.waitForConfirmation(page)).then(respond)
    .catch(error => { throw new Error(`${error.message}\n${origin}`, { cause: error }); });
  decisions.set(page, decision);
  return decision;
};

exports.decideConfirmation = async (page, action, respond) => {
  const decision = exports.answerNextConfirmation(page, respond);
  await action();
  await decision;
};

exports.rejectNativeDialogs = (page, errors) => {
  page.on('dialog', async dialog => {
    if (dialog.type() === 'beforeunload') { await dialog.accept(); return; }
    errors.push(`Unexpected native ${dialog.type()}: ${dialog.message()}`);
    await dialog.dismiss();
  });
};
