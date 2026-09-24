import { staffSession } from './state.js';
import { loadStaffSession } from './http.js';

const form = document.getElementById('diagnostic-form');
const run = document.getElementById('run');
const copy = document.getElementById('copy');
const report = document.getElementById('report');
const status = document.getElementById('session-status');
let busy = false;

function updateSession() {
  const isAdmin = staffSession.authenticated && staffSession.accessAllowed && staffSession.staff?.role === 'super_admin';
  run.disabled = busy;
  status.textContent = isAdmin ? 'Super-admin session active.' : 'Sign in to ASAP as a super admin in this browser, then return to this page.';
}
loadStaffSession().then(updateSession).catch(updateSession);

form.addEventListener('submit', async event => {
  event.preventDefault();
  if (run.disabled) return;
  busy = true;
  updateSession();
  copy.disabled = true;
  report.textContent = 'Testing...';
  try {
    const result = await loadStaffSession();
    report.textContent = JSON.stringify(result, null, 2);
    copy.disabled = false;
  } catch (err) {
    report.textContent = JSON.stringify({ status: err.status || 0, message: 'Diagnostic request failed. Verify your super-admin session and that the diagnostic endpoint is deployed.' }, null, 2);
    copy.disabled = false;
  } finally {
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
