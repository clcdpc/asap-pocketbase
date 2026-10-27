'use strict';
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');

const statuses = ['PASS', 'FAIL', 'BLOCKED', 'NOT RUN', 'HUMAN REVIEW REQUIRED'];
class Blocked extends Error {}

// Never serialize arbitrary exceptions, API responses, browser events or state files.
// Error messages can contain SQL connection strings, cookies, OIDC URLs or tokens.
function safeError(error) {
  if (error instanceof Blocked) return error.message;
  if (error?.code === 'ERR_ASSERTION') return 'Assertion failed; see the scenario requirement and sanitized failure trace.';
  if (error?.name === 'TimeoutError') return 'Browser checkpoint timed out.';
  return 'Browser or runner operation failed; inspect the local environment and scenario trace.';
}
function assertionDetails(error) {
  if (error?.code !== 'ERR_ASSERTION') return undefined;
  // Only caller-written assertion labels and small scalar diagnostics are safe.
  const label = typeof error.message === 'string' ? error.message.split('\n')[0] : '';
  const scalar = value => typeof value === 'number' || typeof value === 'boolean' || value === null ? value :
    typeof value === 'string' && /^(suggestion|outstanding_purchase|pending_hold|hold_placed|closed|open|purchase|reject|manual|queued|purchased_no_hold|rejected|file|PASS|FAIL|BLOCKED|NOT RUN|stale_version)$/.test(value) ? value : '(omitted)';
  return { operator: error.operator, label: /^[a-zA-Z0-9 /,;().?-]{1,180}$/.test(label) &&
    !/token|cookie|password|credential|secret|bearer/i.test(label) ? label : 'Assertion failed',
    expected: scalar(error.expected), actual: scalar(error.actual),
    source: String(error.stack || '').split('\n').find(line => /[\\/]tests[\\/]acceptance[\\/].*:\d+:\d+/.test(line))?.trim().replace(/^at /, '') };
}
function result(scenario, status, detail = '') {
  if (!statuses.includes(status)) throw new Error('Invalid result status');
  return { id: scenario.id, requirements: scenario.requirements, category: scenario.category || 'functional',
    status, detail, executed: status === 'PASS' || status === 'FAIL' };
}
function exitCode(report) {
  if (report.scenarios.some(s => s.status === 'FAIL') || report.environment.status === 'FAIL') return 1;
  if (report.environment.status !== 'PASS' || report.authentication.status === 'BLOCKED' ||
      report.scenarios.some(s => s.status === 'BLOCKED')) return 2;
  return 0;
}
function functionalStatus(report) {
  const rows = report.scenarios.filter(s => s.category === 'functional');
  if (rows.some(s => s.status === 'FAIL')) return 'FAIL';
  if (rows.some(s => s.status === 'BLOCKED')) return 'BLOCKED';
  if (report.requiredScenarioIds?.some(id => !rows.some(s => s.id === id))) return 'INCOMPLETE';
  if (!rows.length || rows.some(s => s.status !== 'PASS')) return 'INCOMPLETE';
  return 'PASS';
}
async function createReport(root, suite, expectedCommit, toolingCommit, toolingDirty) {
  const startedUtc = new Date().toISOString();
  const directory = process.env.ASAP_ACCEPTANCE_CONTAINER === '1' ? '/artifacts' :
    path.join(root, '.artifacts', 'acceptance', startedUtc.replace(/[:.]/g, '-') + '-' + crypto.randomUUID());
  await fs.mkdir(directory, { recursive: true });
  return { directory, report: { schemaVersion: 1, suite, startedUtc,
    expectedApplicationCommit: expectedCommit, applicationCommit: null, toolingCommit, toolingDirty,
    environment: { status: 'NOT RUN' }, authentication: { status: 'NOT RUN' }, scenarios: [] } };
}
async function writeReport(directory, report) {
  report.finishedUtc = new Date().toISOString();
  report.automatedFunctionalAcceptance = functionalStatus(report);
  report.overallAcceptance = 'INCOMPLETE'; // Human acceptance is never inferred from automation.
  report.screenshots = (await fs.readdir(directory)).filter(name => /^[a-z0-9-]+\.png$/.test(name)).sort();
  const line = value => String(value ?? '').replace(/[|\r\n]/g, ' ');
  const rows = report.scenarios.map(s => `| ${line(s.id)} | ${line(s.requirements.join(', '))} | ${s.status} | ${line(s.detail)} |`);
  const markdown = [
    `# ASAP acceptance: ${line(report.suite)}`, '',
    `Application: \`${line(report.applicationCommit || 'not verified')}\``,
    `Requested application: \`${line(report.expectedApplicationCommit)}\``,
    `Tooling: \`${line(report.toolingCommit)}\`${report.toolingDirty ? ' (working tree changes present)' : ''}`,
    `Started (UTC): ${report.startedUtc}`, '',
    `Environment/integration validation: **${report.environment.status}** — ${line(report.environment.detail)}`,
    `Real Entra sign-in: **${report.authentication.status}**`,
    `Automated functional checks: **${report.automatedFunctionalAcceptance}**`,
    'Human visual review: **REQUIRED** (#398)',
    'Screen-reader review: **REQUIRED** (#398)',
    'Overall acceptance / issue readiness: **INCOMPLETE**', '',
    '| Scenario | Issue / requirement | Result | Evidence / reason |',
    '| --- | --- | --- | --- |', ...rows, '',
    'Screenshots: ' + (report.screenshots.map(name => `[${name}](${name})`).join(', ') || 'none'), '',
    'Exit 0 means the selected automated mode finished successfully; it never closes acceptance.',
    'Inspect artifacts before sharing. No issue comments, labels, approvals or closures were changed.', ''
  ].join('\n');
  await fs.writeFile(path.join(directory, 'results.json'), JSON.stringify(report, null, 2) + '\n');
  await fs.writeFile(path.join(directory, 'report.md'), markdown);
}
module.exports = { Blocked, statuses, safeError, assertionDetails, result, exitCode, createReport, writeReport };
