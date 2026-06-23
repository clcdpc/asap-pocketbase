const assert = require('assert');
const fs = require('fs');
const path = require('path');

const root = path.join(__dirname, '..');

function loadSimpleEsModule(modulePath, scope = {}) {
  const source = fs.readFileSync(modulePath, 'utf8');
  const exportNames = [];
  let transformed = source
    .replace(/^import .*$/gm, '')
    .replace(/export class (\w+)/g, (_, name) => {
      exportNames.push(name);
      return `class ${name}`;
    })
    .replace(/export async function (\w+)/g, (_, name) => {
      exportNames.push(name);
      return `async function ${name}`;
    })
    .replace(/export function (\w+)/g, (_, name) => {
      exportNames.push(name);
      return `function ${name}`;
    })
    .replace(/export const (\w+)/g, (_, name) => {
      exportNames.push(name);
      return `const ${name}`;
    })
    .replace(/export let (\w+)/g, (_, name) => {
      exportNames.push(name);
      return `let ${name}`;
    });

  transformed += `\nreturn { ${exportNames.join(', ')} };`;
  return new Function(...Object.keys(scope), transformed)(...Object.values(scope));
}

async function main() {
  console.log('Running frontend request architecture tests...');

  const httpModulePath = path.join(root, 'pb_public/shared/http.js');
  const loadModulePath = path.join(root, 'pb_public/shared/latest-load.js');
  const patronApiPath = path.join(root, 'pb_public/patron/js/api.js');
  const analyticsPath = path.join(root, 'pb_public/staff/js/analytics.js');
  const actionsPath = path.join(root, 'pb_public/staff/js/actions.js');
  const modalsPath = path.join(root, 'pb_public/staff/js/modals.js');
  const settingsPolarisPath = path.join(root, 'pb_public/staff/js/settings-polaris.js');
  const agentsPath = path.join(root, 'AGENTS.md');

  assert.ok(fs.existsSync(httpModulePath), 'shared http helper should exist');
  assert.ok(fs.existsSync(loadModulePath), 'latest-load helper should exist');

  {
    const calls = [];
    const { requestJson, HttpError, isAbortError } = loadSimpleEsModule(httpModulePath, {
      fetch: async (path, options) => {
        calls.push({ path, options });
        return {
          ok: true,
          status: 200,
          json: async () => ({ ok: true, echoedBody: options.body })
        };
      }
    });

    const result = await requestJson('/api/example', {
      method: 'POST',
      body: { hello: 'world' },
      headers: { 'X-Test': 'yes' },
      cache: 'no-store'
    });

    assert.deepStrictEqual(result, { ok: true, echoedBody: JSON.stringify({ hello: 'world' }) });
    assert.strictEqual(calls.length, 1);
    assert.strictEqual(calls[0].path, '/api/example');
    assert.strictEqual(calls[0].options.method, 'POST');
    assert.strictEqual(calls[0].options.cache, 'no-store');
    assert.strictEqual(calls[0].options.headers['Content-Type'], 'application/json');
    assert.strictEqual(calls[0].options.headers['X-Test'], 'yes');
    assert.strictEqual(calls[0].options.body, JSON.stringify({ hello: 'world' }));
    assert.strictEqual(typeof HttpError, 'function');
    assert.strictEqual(typeof isAbortError, 'function');
  }

  {
    const { requestJson, HttpError } = loadSimpleEsModule(httpModulePath, {
      fetch: async () => ({
        ok: false,
        status: 409,
        json: async () => ({ message: 'Conflict', detail: 'duplicate' })
      })
    });

    let caught = null;
    try {
      await requestJson('/api/conflict');
    } catch (err) {
      caught = err;
    }

    assert.ok(caught instanceof HttpError, 'requestJson should reject with HttpError');
    assert.strictEqual(caught.status, 409);
    assert.strictEqual(caught.message, 'Conflict');
    assert.deepStrictEqual(caught.response, { message: 'Conflict', detail: 'duplicate' });
  }

  {
    const abortError = new Error('The operation was aborted.');
    abortError.name = 'AbortError';
    const { requestJson, isAbortError } = loadSimpleEsModule(httpModulePath, {
      fetch: async () => {
        throw abortError;
      }
    });

    let caught = null;
    try {
      await requestJson('/api/abort');
    } catch (err) {
      caught = err;
    }

    assert.strictEqual(caught, abortError, 'abort errors should be rethrown directly');
    assert.strictEqual(isAbortError(caught), true);
    assert.strictEqual(isAbortError(new Error('different')), false);
  }

  {
    const { createLatestLoad } = loadSimpleEsModule(loadModulePath, {
      AbortController
    });

    const latest = createLatestLoad();
    const first = latest.begin('grid');
    const second = latest.begin('grid');
    const other = latest.begin('settings');

    assert.strictEqual(first.signal.aborted, true, 'new load should abort previous load in same slot');
    assert.strictEqual(second.signal.aborted, false, 'current load should remain active');
    assert.strictEqual(other.signal.aborted, false, 'other slot should remain independent');
    assert.strictEqual(first.isCurrent(), false, 'first load should become stale');
    assert.strictEqual(second.isCurrent(), true, 'second load should be current');
    assert.strictEqual(other.isCurrent(), true, 'other slot should be current');

    latest.finish('grid', second.token);
    assert.strictEqual(second.isCurrent(), false, 'finished load should no longer be current');
    assert.strictEqual(other.isCurrent(), true, 'finishing one slot should not affect others');
  }

  {
    const patronApiSource = fs.readFileSync(patronApiPath, 'utf8');
    assert.match(patronApiSource, /from ['"]\.\.\/\.\.\/shared\/http\.js['"]/);

    const authState = {
      token: 'patron-token',
      clearedTo: null,
      clear(value) {
        this.clearedTo = value;
        this.token = value;
      }
    };

    const patronApi = loadSimpleEsModule(patronApiPath, {
      authToken: authState.token,
      setAuthToken: value => authState.clear(value),
      requestJson: async () => {
        const err = new Error('expired');
        err.status = 401;
        err.response = { message: 'expired' };
        throw err;
      },
      window: { location: { origin: 'http://127.0.0.1:8090' } }
    });

    let caught = null;
    try {
      await patronApi.request('/api/asap/patron/suggestions');
    } catch (err) {
      caught = err;
    }

    assert.ok(caught instanceof patronApi.SessionExpiredError, 'patron 401 should become SessionExpiredError');
    assert.strictEqual(authState.clearedTo, '', 'patron 401 should clear auth token');
  }

  {
    const analyticsSource = fs.readFileSync(analyticsPath, 'utf8');
    assert.match(analyticsSource, /import\s+\{\s*authorizedJson\s*\}\s+from\s+['"]\.\/http\.js['"]/);
    assert.ok(!/async function authorizedJson\(/.test(analyticsSource), 'analytics should not define a duplicate authorizedJson helper');
  }

  {
    const actionsSource = fs.readFileSync(actionsPath, 'utf8');
    const modalsSource = fs.readFileSync(modalsPath, 'utf8');
    const settingsPolarisSource = fs.readFileSync(settingsPolarisPath, 'utf8');

    assert.ok(actionsSource.includes('refreshCurrentStaffView'), 'actions should refresh through the named helper');
    assert.ok(!actionsSource.includes('loadTab(currentStatus)'), 'actions should not reload with direct loadTab calls');
    assert.ok(modalsSource.includes('refreshCurrentStaffView'), 'modals should refresh through the named helper');
    assert.ok(!modalsSource.includes('loadTab(currentStatus)'), 'modals should not reload with direct loadTab calls');
    assert.ok(settingsPolarisSource.includes('refreshCurrentStaffView'), 'settings-polaris should refresh through the named helper');
  }

  {
    const agentsSource = fs.readFileSync(agentsPath, 'utf8');
    assert.ok(
      agentsSource.includes('### Frontend request and refresh architecture'),
      'AGENTS.md should document the request and refresh architecture rules'
    );
    assert.ok(
      agentsSource.includes('protect screen-level staff loads with abort-plus-stale-result guards'),
      'AGENTS.md should describe staff load race protection'
    );
  }

  console.log('frontend_request_architecture.test.js passed.');
}

main().catch(err => {
  console.error(err);
  process.exit(1);
});
