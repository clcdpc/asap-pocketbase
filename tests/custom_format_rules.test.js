const assert = require('assert');
const { normalizeFormatRules, sanitizePatronSuggestion, MESSAGE_BEHAVIORS } = require('../lib/format_rules.js');

console.log('Running tests for custom format rules...\n');

let passed = 0;
let failed = 0;

function test(name, fn) {
  try {
    fn();
    console.log(`✅ ${name}`);
    passed++;
  } catch (err) {
    console.error(`❌ ${name}`);
    console.error(`   ${err.message}`);
    failed++;
  }
}

// ──────────────────────────────────────────────
// normalizeFormatRules: custom format support
// ──────────────────────────────────────────────

test('normalizeFormatRules includes custom format keys from incoming rules', () => {
  const rules = normalizeFormatRules({
    videogame: {
      messageBehavior: 'none',
      fields: {
        title: { mode: 'required', label: 'Game Title' },
        author: { mode: 'required', label: 'Developer' },
        identifier: { mode: 'hidden', label: 'SKU' },
        agegroup: { mode: 'required', label: 'Rating' },
        publication: { mode: 'optional', label: 'Release window' }
      }
    }
  });
  assert.ok(rules.videogame, 'videogame key should exist in normalized rules');
  assert.ok(rules.book, 'default book key should still exist');
});

test('custom format labels are preserved through normalization', () => {
  const rules = normalizeFormatRules({
    videogame: {
      messageBehavior: 'none',
      fields: {
        title: { mode: 'required', label: 'Game Title' },
        author: { mode: 'required', label: 'Developer' },
        identifier: { mode: 'hidden', label: 'SKU' },
        agegroup: { mode: 'required', label: 'Rating' },
        publication: { mode: 'optional', label: 'Release window' }
      }
    }
  });
  assert.strictEqual(rules.videogame.fields.title.label, 'Game Title');
  assert.strictEqual(rules.videogame.fields.author.label, 'Developer');
  assert.strictEqual(rules.videogame.fields.identifier.label, 'SKU');
  assert.strictEqual(rules.videogame.fields.agegroup.label, 'Rating');
  assert.strictEqual(rules.videogame.fields.publication.label, 'Release window');
});

test('custom format field modes are preserved through normalization', () => {
  const rules = normalizeFormatRules({
    videogame: {
      messageBehavior: 'none',
      fields: {
        title: { mode: 'required', label: 'Game Title' },
        author: { mode: 'optional', label: 'Developer' },
        identifier: { mode: 'hidden', label: 'SKU' },
        agegroup: { mode: 'required', label: 'Rating' },
        publication: { mode: 'optional', label: 'Release window' }
      }
    }
  });
  assert.strictEqual(rules.videogame.fields.title.mode, 'required');
  assert.strictEqual(rules.videogame.fields.author.mode, 'optional');
  assert.strictEqual(rules.videogame.fields.identifier.mode, 'hidden');
  assert.strictEqual(rules.videogame.fields.agegroup.mode, 'required');
  assert.strictEqual(rules.videogame.fields.publication.mode, 'optional');
});

test('custom format messageBehavior is preserved', () => {
  const rules = normalizeFormatRules({
    vinyl_record: {
      messageBehavior: 'ebookMessage',
      fields: {
        title: { mode: 'required', label: 'Album' }
      }
    }
  });
  assert.strictEqual(rules.vinyl_record.messageBehavior, MESSAGE_BEHAVIORS.EBOOK);
});

test('custom format title mode is always forced to required', () => {
  const rules = normalizeFormatRules({
    videogame: {
      messageBehavior: 'none',
      fields: {
        title: { mode: 'optional', label: 'Game Title' }
      }
    }
  });
  assert.strictEqual(rules.videogame.fields.title.mode, 'required',
    'title mode must always be required regardless of input');
});

test('custom format with no fields gets sensible defaults', () => {
  const rules = normalizeFormatRules({
    magazine: { messageBehavior: 'none' }
  });
  assert.ok(rules.magazine, 'magazine key should exist');
  assert.ok(rules.magazine.fields, 'fields should exist');
  assert.ok(rules.magazine.fields.title, 'title field should exist');
  assert.strictEqual(rules.magazine.fields.title.mode, 'required');
});

test('default formats are not affected by custom format additions', () => {
  const rules = normalizeFormatRules({
    videogame: {
      messageBehavior: 'none',
      fields: {
        title: { mode: 'required', label: 'Game Title' },
        author: { mode: 'required', label: 'Developer' }
      }
    }
  });
  // Default book labels should be unchanged
  assert.strictEqual(rules.book.fields.title.label, 'Title');
  assert.strictEqual(rules.book.fields.author.label, 'Author');
  assert.strictEqual(rules.dvd.fields.author.label, 'Director/Actors/Producer');
});

// ──────────────────────────────────────────────
// sanitizePatronSuggestion: custom format support
// ──────────────────────────────────────────────

test('sanitizePatronSuggestion accepts custom format and applies its rules', () => {
  const uiText = {
    formatRules: {
      videogame: {
        messageBehavior: 'none',
        fields: {
          title: { mode: 'required', label: 'Game Title' },
          author: { mode: 'optional', label: 'Developer' },
          identifier: { mode: 'hidden', label: 'SKU' },
          agegroup: { mode: 'required', label: 'Rating' },
          publication: { mode: 'optional', label: 'Release window' }
        }
      }
    }
  };
  const data = {
    format: 'videogame',
    title: 'Elden Ring',
    author: 'FromSoftware',
    identifier: 'ABC123',
    agegroup: 'Adult',
    publication: 'Already published'
  };
  const result = sanitizePatronSuggestion(data, uiText);
  assert.strictEqual(result.format, 'videogame', 'format should stay as videogame, not fall back to book');
  assert.strictEqual(result.identifier, '', 'identifier should be cleared because mode is hidden');
});

test('sanitizePatronSuggestion rejects custom format when required field is missing', () => {
  const uiText = {
    formatRules: {
      videogame: {
        messageBehavior: 'none',
        fields: {
          title: { mode: 'required', label: 'Game Title' },
          author: { mode: 'required', label: 'Developer' },
          identifier: { mode: 'optional', label: 'SKU' },
          agegroup: { mode: 'required', label: 'Rating' },
          publication: { mode: 'required', label: 'Release window' }
        }
      }
    }
  };
  const data = {
    format: 'videogame',
    title: 'Elden Ring',
    author: '',  // required but empty
    agegroup: 'Adult',
    publication: 'Already published'
  };
  try {
    sanitizePatronSuggestion(data, uiText);
    assert.fail('Should have thrown an error for missing required Developer field');
  } catch (err) {
    assert.ok(err.message.includes('Developer'), `Error should mention "Developer" label, got: ${err.message}`);
  }
});

test('sanitizePatronSuggestion rejects custom format with message-only behavior', () => {
  const uiText = {
    formatRules: {
      streaming: {
        messageBehavior: 'ebookMessage',
        fields: {
          title: { mode: 'required', label: 'Title' }
        }
      }
    }
  };
  const data = {
    format: 'streaming',
    title: 'Some Movie'
  };
  try {
    sanitizePatronSuggestion(data, uiText);
    assert.fail('Should have thrown an error for message-only format');
  } catch (err) {
    assert.ok(err.message.includes('informational'), `Error should mention informational, got: ${err.message}`);
  }
});

test('sanitizePatronSuggestion uses custom labels in error messages', () => {
  const uiText = {
    formatRules: {
      videogame: {
        messageBehavior: 'none',
        fields: {
          title: { mode: 'required', label: 'Game Title' },
          author: { mode: 'required', label: 'Developer' },
          identifier: { mode: 'optional', label: 'SKU' },
          agegroup: { mode: 'required', label: 'ESRB Rating' },
          publication: { mode: 'required', label: 'Release window' }
        }
      }
    }
  };
  const data = {
    format: 'videogame',
    title: 'Elden Ring',
    author: 'FromSoftware',
    agegroup: '',  // required but empty
    publication: 'Already published'
  };
  try {
    sanitizePatronSuggestion(data, uiText);
    assert.fail('Should have thrown an error for missing ESRB Rating');
  } catch (err) {
    assert.ok(err.message.includes('ESRB Rating'),
      `Error should use custom label "ESRB Rating", got: ${err.message}`);
  }
});

// ──────────────────────────────────────────────
console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);
if (failed > 0) process.exit(1);
