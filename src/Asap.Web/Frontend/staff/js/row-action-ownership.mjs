import { staffSession, staffAccessGeneration, currentSuggestions, allSuggestions,
  currentStatus, currentWorkflowOrgScopeId } from './state.js';
import { findWorkflowRow, requestIdentity, sameRequestIdentity } from './request-identity.mjs';

let generation = 0;

export function invalidateRowActionOwnership() {
  generation += 1;
  const assignmentDialog = document.getElementById('assign-dialog');
  if (assignmentDialog?.open) assignmentDialog.close();
}

export function beginRowAction(row) {
  invalidateRowActionOwnership();
  const token = generation;
  const accessGeneration = staffAccessGeneration;
  const actor = staffSession.staff;
  const status = currentStatus;
  const scope = currentWorkflowOrgScopeId;
  const identity = requestIdentity(row);
  const sessionCurrent = () => staffAccessGeneration === accessGeneration &&
    staffSession.authenticated && staffSession.accessAllowed &&
    staffSession.staff?.id === actor?.id && staffSession.staff?.role === actor?.role &&
    String(staffSession.staff?.organizationId) === String(actor?.organizationId);
  const ownsUi = () => {
    const current = findWorkflowRow(identity, currentSuggestions, allSuggestions);
    return token === generation && sessionCurrent() && currentStatus === status &&
      currentWorkflowOrgScopeId === scope && current && sameRequestIdentity(current, identity) &&
      current.version === row.version && current.status === row.status;
  };
  return { ownsUi, sessionCurrent };
}
