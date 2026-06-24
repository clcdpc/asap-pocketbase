import { renderWorkflowTags } from '../grid.js';

export function renderEditClaimState(row, ctx) {
  const container = ctx.claimState;
  if (!container) return;
  container.replaceChildren();
  const label = document.createElement('span');
  label.className = 'edit-status-group-label';
  label.textContent = 'Claim:';
  container.appendChild(label);
  const valueWrap = document.createElement('span');
  valueWrap.className = 'edit-status-group-value';
  const currentStaffId = String((ctx.pb.authStore.model && ctx.pb.authStore.model.id) || '').trim();
  const claimantId = String(row.claimedByStaffUserId || '').trim();
  const badge = document.createElement('span');
  badge.className = 'claim-badge';
  if (!claimantId) {
    badge.classList.add('claim-badge--unclaimed');
    badge.textContent = 'Unclaimed';
  } else if (currentStaffId && claimantId === currentStaffId) {
    badge.classList.add('claim-badge--mine');
    badge.textContent = 'Mine';
  } else {
    const name = row.claimedByDisplayName || 'Staff';
    badge.classList.add('claim-badge--claimed');
    badge.textContent = `Claimed by ${name}`;
  }
  valueWrap.appendChild(badge);
  if (claimantId) {
    const source = document.createElement('span');
    source.className = 'text-muted';
    source.textContent = row.claimType === 'automatic_format_rule' ? '(auto)' : '(manual)';
    valueWrap.appendChild(source);
  }
  container.appendChild(valueWrap);
}

export function renderEditWorkflowTags(tags, row, ctx) {
  const container = ctx.workflowTags;
  if (!container) return;
  container.replaceChildren();
  const label = document.createElement('span');
  label.className = 'edit-status-group-label';
  label.textContent = 'Flags:';
  container.appendChild(label);
  const valueWrap = document.createElement('span');
  valueWrap.className = 'edit-status-group-value';
  const tagHtml = renderWorkflowTags(tags, row);
  if (tagHtml.includes('No workflow flags')) {
    const none = document.createElement('span');
    none.className = 'text-muted';
    none.textContent = 'None';
    valueWrap.appendChild(none);
  } else {
    const temp = document.createElement('div');
    temp.innerHTML = tagHtml;
    while (temp.firstChild) {
      valueWrap.appendChild(temp.firstChild);
    }
  }
  container.appendChild(valueWrap);
}

export function reactiveCleanupWorkflowFlags(rowId, ctx) {
  const row = ctx.currentSuggestions.find(r => r.id === rowId) || ctx.allSuggestions.find(r => r.id === rowId);
  if (!row || !row.workflowTags) return;

  const staleFlags = ['Hold failed', '! Hold failed', 'No holdable items'];
  const originalTags = Array.isArray(row.workflowTags) ? row.workflowTags : (String(row.workflowTags || '').split(',').map(t => t.trim()).filter(Boolean));
  
  const nextTags = originalTags.filter(t => !staleFlags.includes(t));
  
  if (nextTags.length !== originalTags.length) {
    row.workflowTags = nextTags;
    renderEditWorkflowTags(nextTags, row, ctx);
    console.log(`Cleaned up stale workflow flags for row ${rowId}`);
  }
}
