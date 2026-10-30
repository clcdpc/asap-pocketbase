'use strict';
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const { createReport, writeReport, result } = require('/runner/tests/acceptance/helpers/reporting.cjs');

async function blocked(detail) {
  let report;
  try { report = JSON.parse(await fs.readFile('/artifacts/results.json', 'utf8')); }
  catch {
    const identity = JSON.parse(await fs.readFile('/review/identity.json', 'utf8'));
    const tooling = JSON.parse(await fs.readFile('/runner/tooling.json', 'utf8'));
    report = (await createReport('/runner', process.env.REVIEW_SUITE || 'unselected',
      identity.applicationCommit, identity.toolingCommit, tooling.dirty)).report;
  }
  report.environment = { status: 'BLOCKED', detail };
  report.scenarios = report.scenarios.filter(s => s.id !== 'runtime-startup');
  report.scenarios.push(result({ id: 'runtime-startup', requirements: ['Disposable acceptance runtime'] }, 'BLOCKED', detail));
  report.actualExitCode = 2;
  await writeReport('/artifacts', report);
}
async function prepare(argv) {
  const suiteIndex = argv.indexOf('--suite');
  process.env.REVIEW_SUITE = suiteIndex >= 0 ? argv[suiteIndex + 1] : 'unselected';
  for (const directory of ['config', 'input', 'dpkeys', 'auth', 'evidence']) {
    await fs.mkdir('/review/' + directory, { recursive: true });
  }
  const secret = () => crypto.randomBytes(32).toString('hex');
  await fs.writeFile('/review/runtime.env', 'REVIEW_SA_PASSWORD=' + secret() + 'aA1!\n');
  await fs.writeFile('/review/auth/credential', secret());
  await fs.writeFile('/review/evidence/token', secret());
  await fs.writeFile('/review/input/review.json', JSON.stringify({ Seed: 'Review', Entra: {
    ClientId: crypto.randomUUID(), AllowedTenantIds: [crypto.randomUUID()],
    InitialSuperAdminEmail: 'reviewer@example.org', InitialSuperAdminDisplayName: 'ASAP Reviewer'
  } }));
  await blocked('Acceptance startup has not completed; browser scenarios have not run.');
}
async function diagnostics(code) {
  const secrets = [];
  for (const file of ['/review/runtime.env', '/review/auth/credential', '/review/evidence/token',
    '/review/tls/password.txt', '/review/config/dataprotection-password.txt']) {
    try {
      const value = (await fs.readFile(file, 'utf8')).trim();
      secrets.push(value.includes('=') ? value.slice(value.indexOf('=') + 1) : value);
    } catch { /* Startup may not have created every file. */ }
  }
  try {
    const config = JSON.parse(await fs.readFile('/review/config/application.json', 'utf8'));
    const connection = config.ConnectionStrings.AsapDatabase;
    secrets.push(connection, connection.match(/Password=([^;]+)/i)?.[1]);
  } catch { /* Configuration failures still export the startup log. */ }
  const directory = '/artifacts/diagnostics';
  await fs.mkdir(directory, { recursive: true });
  for (const filename of await fs.readdir('/review/logs')) {
    if (!/^[a-zA-Z0-9._-]+\.log$/.test(filename)) continue;
    let text = await fs.readFile(path.join('/review/logs', filename), 'utf8');
    for (const secret of secrets.filter(Boolean)) text = text.split(secret).join('[redacted]');
    text = text.replace(/((?:password|pwd|cookie|set-cookie|x-asap-review-auth|x-asap-review-proof)\s*[:=])[^\r\n]*/gi, '$1 [redacted]');
    await fs.writeFile(path.join(directory, filename), text);
  }
  const report = JSON.parse(await fs.readFile('/artifacts/results.json', 'utf8'));
  report.actualExitCode = Number(code);
  report.diagnostics = 'diagnostics/';
  await writeReport('/artifacts', report);
}
async function main() {
  const [command, ...args] = process.argv.slice(2);
  if (command === 'prepare') return prepare(args);
  if (command === 'blocked') return blocked(args[0]);
  if (command === 'diagnostics') return diagnostics(args[0]);
  if (command === 'health') return require('/runner/tests/acceptance/helpers/environment.cjs').health('https://localhost:8080');
  throw new Error('Unknown runtime command');
}
main().catch(() => { process.exitCode = 2; });
