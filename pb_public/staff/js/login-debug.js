import { pb } from './state.js';
import { authorizedJson } from './http.js';

const form = document.getElementById('diagnostic-form');
const run = document.getElementById('run');
const copy = document.getElementById('copy');
const report = document.getElementById('report');
const status = document.getElementById('session-status');
const password = document.getElementById('password');
let busy = false;

function updateSession() {
  const isAdmin = pb.authStore.isValid && pb.authStore.model?.role === 'super_admin';
  run.disabled = busy || !isAdmin;
  status.textContent = isAdmin ? 'Super-admin session active.' : 'Sign in to ASAP as a super admin in this browser, then return to this page.';
}
pb.authStore.onChange(updateSession);
updateSession();

form.addEventListener('submit', async event => {
  event.preventDefault();
  if (run.disabled) return;
  busy = true;
  updateSession();
  copy.disabled = true;
  report.textContent = 'Testing...';
  const body = { username: document.getElementById('username').value, password: password.value };
  password.value = '';
  try {
    const result = await authorizedJson('/api/asap/staff/login-diagnostics', { method: 'POST', body, cache: 'no-store' });
    report.textContent = JSON.stringify(result, null, 2);
    copy.disabled = false;
  } catch (err) {
    report.textContent = JSON.stringify({ status: err.status || 0, message: 'Diagnostic request failed. Verify your super-admin session and that the diagnostic endpoint is deployed.' }, null, 2);
    copy.disabled = false;
  } finally {
    body.password = '';
    busy = false;
    updateSession();
  }
});

copy.addEventListener('click', async () => {
  try {
    await navigator.clipboard.writeText(report.textContent);
    status.textContent = 'Report copied.';
  } catch {
    status.textContent = 'Clipboard unavailable. Select the report text to copy it.';
  }
});
