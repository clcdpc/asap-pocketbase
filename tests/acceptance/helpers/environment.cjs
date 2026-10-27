'use strict';
const fs = require('node:fs/promises');
const path = require('node:path');
const https = require('node:https');
const { Blocked } = require('./reporting.cjs');
const shaPattern = /^[a-f0-9]{40}$/;
const localDocker = value => typeof value === 'string' && (/^unix:\/\//.test(value) && value.startsWith('unix:///') ||
  value.startsWith('npipe:////./pipe/'));
const must = (condition, message) => { if (!condition) throw new Blocked(message); };

async function readState(root, expectedCommit) {
  let state;
  try { state = JSON.parse((await fs.readFile(path.join(root, '.review', 'current.json'), 'utf8')).replace(/^\uFEFF/, '')); }
  catch { throw new Blocked('Review state is missing or malformed. Start the managed review environment.'); }
  return validateState(state, expectedCommit, root);
}
function validateState(state, expectedCommit, root, container = false) {
  must(shaPattern.test(expectedCommit || ''), 'ExpectedCommit must be a full lowercase application SHA.');
  must(state && typeof state === 'object' && !Array.isArray(state), 'Review state must be an object.');
  must(state.LifecycleState === 'Ready', 'Review lifecycle must be Ready.');
  must(state.Sha === expectedCommit, 'Recorded application SHA does not match ExpectedCommit.');
  const short = expectedCommit.slice(0, 12);
  const project = state.Kind === 'Acceptance' && /^[a-f0-9]{24}$/.test(state.RunId || '') ?
    'asap-acceptance-' + state.RunId : 'asap-review-' + short;
  must(state.ShortSha === short && state.ProjectName === project, 'Review project identity is invalid.');
  must(Number.isInteger(state.Port) && state.Port > 0 && state.Port <= 65535, 'Review HTTPS port is invalid.');
  must(state.ToolingVersion === '0.3.0', 'Review harness 0.3.0 is required; restart with this branch tooling.');
  must(shaPattern.test(state.ToolingSha || ''), 'Review tooling SHA is required.');
  must(typeof state.StatePath === 'string' && (container ? state.StatePath === '/tmp/asap-review' :
    path.resolve(state.StatePath) === path.join(root, '.review', ...(state.Kind === 'Acceptance' ? ['acceptance', state.RunId] : [short]))),
    'Review state directory is outside the managed review root.');
  // No arbitrary URL option: the launcher binds exactly this origin to loopback.
  return { state, origin: 'https://localhost:' + state.Port };
}
function service(containers, name) {
  const matches = containers.filter(c => c.Config?.Labels?.['com.docker.compose.service'] === name);
  must(matches.length === 1, 'Expected exactly one managed ' + name + ' container.');
  return matches[0];
}
function validateContainers(state, containers) {
  const allowed = ['web', 'sql', 'db-init', 'acceptance'];
  must(containers.length >= 3 && containers.length <= 4 && containers.every(c => allowed.includes(
    c.Config?.Labels?.['com.docker.compose.service'])), 'Managed review containers are missing or ambiguous.');
  const web = service(containers, 'web'), sql = service(containers, 'sql'), init = service(containers, 'db-init');
  for (const c of containers) {
    must(c.Config.Labels['com.docker.compose.project'] === state.ProjectName, 'Container project mismatch.');
    must(c.HostConfig?.NetworkMode === state.ProjectName + '_review' && c.HostConfig?.Privileged === false, 'Unsafe review container isolation.');
    must(Object.keys(c.NetworkSettings?.Networks || {}).join() === state.ProjectName + '_review',
      'Unexpected review container network.');
  }
  must(web.State?.Running === true && sql.State?.Running === true && init.State?.Status === 'exited' && init.State.ExitCode === 0,
    'Review runtime is stopped or initialization failed.');
  for (const c of [web, init]) {
    must(c.Config.Labels['org.opencontainers.image.revision'] === state.Sha, 'Running image application SHA mismatch.');
    must(c.Config.Labels['org.opencontainers.image.version'] === state.ToolingVersion, 'Running image tooling version mismatch.');
    must(c.Config.Labels['org.asap.review.tooling-revision'] === state.ToolingSha, 'Running image tooling SHA mismatch.');
  }
  if (containers.length === 4) {
    const acceptance = service(containers, 'acceptance');
    must(acceptance.State.Running === true && acceptance.Config.Labels['org.opencontainers.image.revision'] === state.ToolingSha &&
      acceptance.Config.Labels['org.opencontainers.image.version'] === state.ToolingVersion, 'Acceptance image identity mismatch.');
    const ports = acceptance.NetworkSettings.Ports;
    must(Object.keys(ports || {}).length === 1 && ports['6080/tcp']?.length === 1 &&
      ports['6080/tcp'][0].HostIp === '127.0.0.1' && ports['6080/tcp'][0].HostPort === String(state.ViewerPort),
      'Acceptance viewer must be published only on IPv4 loopback.');
    const allowedMounts = ['/review/tls-public', '/run/review/control', '/run/review/viewer', '/review/evidence/token', '/artifacts', '/tmp'];
    must((acceptance.Mounts || []).every(m => allowedMounts.includes(m.Destination) &&
      (['/artifacts', '/tmp'].includes(m.Destination) || m.RW === false)), 'Unsafe acceptance mounts.');
  }
  const bindings = web.NetworkSettings.Ports;
  must(Object.keys(bindings || {}).length === 1 && bindings['8443/tcp']?.length === 1 &&
    bindings['8443/tcp'][0].HostIp === '127.0.0.1' &&
    bindings['8443/tcp'][0].HostPort === String(state.Port), 'Review HTTPS must be published only on IPv4 loopback.');
  must(Object.values(sql.NetworkSettings.Ports || {}).every(v => v === null || v.length === 0) &&
    Object.values(sql.HostConfig.PortBindings || {}).every(v => v === null || v.length === 0),
    'Review SQL must not publish a host port.');
  const env = Object.fromEntries((web.Config.Env || []).map(v => { const split = v.indexOf('='); return [v.slice(0, split), v.slice(split + 1)]; }));
  must(env.ASPNETCORE_ENVIRONMENT === 'Testing' && env.Testing__UseDeterministicPatronProvider === 'true' &&
    env.ASPNETCORE_HOSTINGSTARTUPASSEMBLIES === 'Asap.ReviewProviders', 'Deterministic review providers are required.');
  must(!Object.keys(env).some(k => /^(ConnectionStrings__|EmailSafety__|Asap__|Testing__Staff|Authentication__|DOTNET_ADDITIONAL_DEPS$)/i.test(k)),
    'Unexpected runtime configuration override.');
  for (const [container, destination, suffix, readonly] of [
    [web, '/review/config', 'runtime-config', true], [init, '/review/config', 'runtime-config', false],
    [sql, '/var/opt/mssql', 'sql-data', false]
  ]) {
    const mounts = container.Mounts?.filter(m => m.Destination === destination) || [];
    must(mounts.length === 1 && mounts[0].Type === 'volume' &&
      mounts[0].Name === state.ProjectName + '_' + suffix && mounts[0].RW === !readonly,
      'Review database/config volume identity mismatch.');
  }
  must(web.Path === '/review/web-entrypoint.sh' && web.Config.WorkingDir === '/app', 'Unexpected review web entry point.');
  return { webId: web.Id, sqlId: sql.Id, initId: init.Id, imageId: web.Image, webHostname: web.Config.Hostname,
    toolingCommit: state.ToolingSha };
}
function validateEvidence(evidence) {
  must(evidence.contract === 1 && evidence.database === 'AsapReview' && evidence.login === 'asap_review_runtime' &&
    evidence.sqlMajorVersion === 16 && evidence.sqlEdition === 'Developer' && evidence.environment === 'Review' &&
    evidence.deterministicProviders === true && evidence.deliveryMode === 'capture' &&
    Array.isArray(evidence.titles) && Array.isArray(evidence.copies) && Array.isArray(evidence.outbox),
    'Runtime/database/provider evidence is incomplete.');
}
async function health(origin) {
  await new Promise((resolve, reject) => {
    const request = https.get(origin + '/health/ready', { timeout: 5000 }, response => {
      let data = '';
      response.on('data', chunk => { data += chunk; if (data.length > 4096) request.destroy(); });
      response.on('end', () => {
        try { must(response.statusCode === 200 && JSON.parse(data).status === 'healthy', 'Review health is not ready.'); resolve(); }
        catch { reject(new Blocked('Review health is not ready.')); }
      });
    });
    request.on('timeout', () => request.destroy());
    request.on('error', () => reject(new Blocked('Trusted local HTTPS health check failed. Check review TLS/readiness.')));
  });
}
async function verifyEnvironment(root, expectedCommit, dependencies = {}) {
  const readProof = dependencies.readProof || (async () => {
    try { return JSON.parse(await fs.readFile('/run/review/control/proof.json', 'utf8')); }
    catch { throw new Blocked('Live host isolation evidence is unavailable. Keep the PowerShell launcher running.'); }
  });
  const proof = await readProof();
  validateProof(proof);
  const { state, origin } = validateState(proof.state, expectedCommit, root, true);
  const identity = validateContainers(state, proof.containers);
  const current = await (dependencies.runtimeEvidence || runtimeEvidence)(origin);
  must(current.identity?.applicationCommit === expectedCommit && current.identity.toolingCommit === state.ToolingSha &&
    current.identity.toolingVersion === state.ToolingVersion && current.project === state.ProjectName &&
    current.webHostname === identity.webHostname, 'Live application/tooling/project identity mismatch.');
  validateEvidence(current.evidence);
  await (dependencies.health || health)(origin);
  // The host can revoke evidence while a SQL/health read is in flight.
  const latest = await readProof();
  validateProof(latest);
  must(JSON.stringify(validateContainers(latest.state, latest.containers)) === JSON.stringify(identity) &&
    latest.state.Sha === state.Sha && latest.state.ProjectName === state.ProjectName, 'Review runtime changed during verification.');
  return { origin, state, identity, evidence: current.evidence };
}
function validateProof(proof) {
  const age = Date.now() - Date.parse(proof?.observedUtc);
  must(proof?.schemaVersion === 1 && proof.acceptanceRunning === true && Number.isFinite(age) && age >= -2000 && age < 15000,
    'Host isolation evidence is stale or incomplete; mutations are blocked.');
  must(localDocker(proof.dockerEndpoint), 'Remote Docker contexts are not supported.');
  must(proof.containers?.length === 4, 'Managed review containers are missing or ambiguous.');
}
async function runtimeEvidence(origin) {
  let token;
  try { token = await fs.readFile('/review/evidence/token', 'utf8'); }
  catch { throw new Blocked('Review evidence credential is unavailable.'); }
  return new Promise((resolve, reject) => {
    const fail = () => reject(new Blocked('Protected read-only runtime/SQL evidence is unavailable.'));
    const request = https.get(origin + '/_review/evidence', {
      headers: { 'X-Asap-Review-Proof': token }, timeout: 12000
    }, response => {
      let data = '';
      response.on('error', fail);
      response.on('data', chunk => { data += chunk; if (data.length > 1024 * 1024) request.destroy(); });
      response.on('end', () => {
        try { must(response.statusCode === 200, 'Protected runtime evidence failed.'); resolve(JSON.parse(data)); }
        catch { fail(); }
      });
    });
    request.on('timeout', () => request.destroy());
    request.on('error', fail);
  });
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
module.exports = { must, readState, validateState, validateContainers, validateEvidence, validateProof, verifyEnvironment, requireFixture };
