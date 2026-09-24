const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');

(async () => {
  const source = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend', 'staff', 'js', 'settings', 'delete-formats.js');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-settings-format-delete-'));
  try {
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');
    fs.copyFileSync(source, path.join(temporary, 'delete-formats.js'));
    const { deleteSettingsFormatsSequentially } = await import(pathToFileURL(path.join(temporary, 'delete-formats.js')).href);
    const first = { id: '9007199254740993', version: 'first-version' };
    const second = { id: '9007199254740994', version: 'second-version' };
    const pending = [first, second];
    let remaining = [...pending];
    const attempts = [];

    await assert.rejects(deleteSettingsFormatsSequentially(pending, async format => {
      attempts.push(format.id);
      if (format.id === second.id) throw new Error('format_referenced');
    }, deleted => {
      remaining = remaining.filter(format => format.id !== deleted.id);
    }), /format_referenced/);

    assert.deepEqual(attempts, [first.id, second.id]);
    assert.deepEqual(remaining, [second], 'A successful delete must leave the retry queue before a later delete fails.');

    await deleteSettingsFormatsSequentially(remaining, async format => {
      attempts.push(format.id);
    }, deleted => {
      remaining = remaining.filter(format => format.id !== deleted.id);
    });
    assert.deepEqual(attempts, [first.id, second.id, second.id], 'A retry must not send a deleted bigint ID again.');
    assert.deepEqual(remaining, []);

    let contextIsCurrent = true;
    const oldContextAttempts = [];
    const oldContextPending = [first, second];
    await deleteSettingsFormatsSequentially(oldContextPending, async format => {
      oldContextAttempts.push(format.id);
      contextIsCurrent = false;
    }, deleted => {
      const index = oldContextPending.findIndex(format => format.id === deleted.id);
      if (index >= 0) oldContextPending.splice(index, 1);
    }, () => contextIsCurrent);
    assert.deepEqual(oldContextAttempts, [first.id], 'A library switch during one delete must stop queued deletes from the prior context.');
    assert.deepEqual(oldContextPending, [second], 'A queued delete from a superseded context must remain pending for an explicit review.');
    console.log('Custom format deletion removes successful IDs from the retry queue immediately');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
