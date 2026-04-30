const assert = require('assert');

global.__hooks = __dirname + '/../pb_hooks';

const routes = require('../pb_hooks/lib/routes.js');

function runTests() {
  const uiText = {
    alreadySubmittedMessage: 'Previous: {{duplicate_title}} by {{duplicate_author}} on {{duplicate_date}}. Status: {{duplicate_status}}. Match: {{duplicate_match_type}}. Format: {{duplicate_format}}.',
    duplicateStatusLabels: {
      suggestion: 'Received',
      rejected: 'Not selected for purchase',
      closed: 'Completed'
    },
    formatLabels: {
      book: 'Book'
    }
  };

  const rejectedMessage = routes.renderDuplicateMessage(uiText, {
    created: '2026-04-29 14:30:00.000Z',
    status: 'closed',
    closeReason: 'rejected',
    title: '<Bad Title>',
    author: 'A & B',
    format: 'book',
    matchType: 'identifier'
  });

  assert.ok(rejectedMessage.includes('&lt;Bad Title&gt;'));
  assert.ok(rejectedMessage.includes('A &amp; B'));
  assert.ok(rejectedMessage.includes('April 29, 2026'));
  assert.ok(rejectedMessage.includes('Status: Not selected for purchase.'));
  assert.ok(rejectedMessage.includes('Match: identifier number.'));
  assert.ok(rejectedMessage.includes('Format: Book.'));

  const openMessage = routes.renderDuplicateMessage(uiText, {
    created: '2026-04-28T10:00:00.000Z',
    status: 'suggestion',
    title: 'Clean Title',
    author: '',
    format: 'unknown_format',
    matchType: 'title_format'
  });

  assert.ok(openMessage.includes('Status: Received.'));
  assert.ok(openMessage.includes('Match: title and format.'));
  assert.ok(openMessage.includes('Format: unknown_format.'));

  console.log('duplicate_message tests passed.');
}

runTests();
