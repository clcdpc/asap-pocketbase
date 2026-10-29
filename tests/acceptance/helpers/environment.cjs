'use strict';
const fs = require('node:fs/promises');
const https = require('node:https');
const { Blocked } = require('./reporting.cjs');
const must = (condition, message) => { if (!condition) throw new Blocked(message); };

function validateEvidence(evidence) {
  must(evidence?.contract === 1 && evidence.database === 'AsapReview' && evidence.login === 'asap_review_runtime' &&
    evidence.sqlMajorVersion === 16 && evidence.sqlEdition === 'Developer' && evidence.environment === 'Review' &&
    evidence.deterministicProviders === true && evidence.deliveryMode === 'capture' &&
    Array.isArray(evidence.titles) && Array.isArray(evidence.copies) && Array.isArray(evidence.outbox),
    'Runtime/database/provider evidence is incomplete.');
  must(evidence.titles.some(t => t.key === 'acceptance-pr410-draft') &&
    evidence.titles.some(t => t.key === 'acceptance-pr410-close-reopen') &&
    evidence.copies.some(t => t.key === 'acceptance-pr410-copy'), 'Expected review fixtures are missing.');
}
async function readJson(url, headers = {}) {
  return new Promise((resolve, reject) => {
    const fail = () => reject(new Blocked('Trusted local HTTPS runtime/readiness check failed. See web diagnostics.'));
    const request = https.get(url, { headers, timeout: 15000 }, response => {
      let data = '';
      response.on('error', fail);
      response.on('data', chunk => { data += chunk; if (data.length > 1024 * 1024) request.destroy(); });
      response.on('end', () => {
        try { must(response.statusCode === 200, 'Review runtime is unavailable.'); resolve(JSON.parse(data)); }
        catch { fail(); }
      });
    });
    request.on('timeout', () => request.destroy());
    request.on('error', fail);
  });
}
async function health(origin) {
  must((await readJson(origin + '/health/ready')).status === 'healthy', 'Review health is not ready.');
}
async function runtimeEvidence(origin) {
  let token;
  try { token = await fs.readFile('/review/evidence/token', 'utf8'); }
  catch { throw new Blocked('Review evidence credential is unavailable.'); }
  return readJson(origin + '/_review/evidence', { 'X-Asap-Review-Proof': token });
}
async function verifyEnvironment(_root, expectedCommit, dependencies = {}) {
  must(/^[a-f0-9]{40}$/.test(expectedCommit || ''), 'ExpectedCommit must be a full lowercase application SHA.');
  const origin = 'https://localhost:8080';
  const current = await (dependencies.runtimeEvidence || runtimeEvidence)(origin);
  const identity = current.identity;
  must(identity?.applicationCommit === expectedCommit && /^[a-f0-9]{40}$/.test(identity.toolingCommit || '') &&
    identity.toolingVersion === '0.4.0', 'Live application/tooling identity mismatch.');
  must(current.aspnetEnvironment === 'Testing' && current.authenticationMode === 'Local',
    'Testing and local review authentication are required.');
  validateEvidence(current.evidence);
  await (dependencies.health || health)(origin);
  return { origin, identity, evidence: current.evidence, authenticationMode: current.authenticationMode };
}
function requireFixture(evidence, key, expected = {}) {
  const list = key === 'copy' ? evidence.copies : evidence.titles;
  const matches = list.filter(f => f.key === 'acceptance-pr410-' + key);
  must(matches.length === 1, 'Fixture ' + key + ' is missing or ambiguous; use a fresh review database.');
  const f = matches[0];
  must(/^[1-9][0-9]*$/.test(f.id), 'Fixture has no authoritative request identity.');
  const pristine = key === 'copy' ? f.pristine && f.status === 'open' :
    f.events === 0 && f.pristineNotes && f.identityBound === true && f.status === 'suggestion';
  must(pristine && Object.entries(expected).every(([k,v]) => f[k] === v),
    'Fixture ' + key + ' is consumed or has changed; this scenario requires fresh review data.');
  return f;
}
module.exports = { must, validateEvidence, verifyEnvironment, requireFixture, health };
