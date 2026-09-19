export function renderRejectionTemplateSelector(actionStr, ctx) {
  const rejectionContainer = ctx.rejectionContainer;
  const select = ctx.rejectionTemplate;
  const availability = ctx.rejectionAvailability;
  if (!rejectionContainer || !select) return;

  if (actionStr !== 'reject') {
    rejectionContainer.classList.add('hidden');
    select.value = '';
    if (availability) {
      availability.textContent = '';
      availability.classList.add('hidden');
    }
    return;
  }

  rejectionContainer.classList.remove('hidden');
  select.innerHTML = '<option value="">Default rejection template (recommended)</option>';
  select.value = '';

  const sortedTemplates = [...ctx.currentRejectionTemplates].sort((a, b) => {
    const nameA = (a.name || a.subject || '').toLowerCase();
    const nameB = (b.name || b.subject || '').toLowerCase();
    return nameA.localeCompare(nameB);
  });

  sortedTemplates.forEach(t => {
    const opt = document.createElement('option');
    opt.value = t.id;
    opt.textContent = t.name || t.subject || 'Rejection template';
    select.appendChild(opt);
  });

  renderRejectionTemplateAvailability(sortedTemplates.length);
}

function renderRejectionTemplateAvailability(otherTemplateCount) {
  const availability = document.getElementById('edit-rejection-template-availability');
  if (!availability) return;

  if (otherTemplateCount < 1) {
    availability.textContent = '';
    availability.classList.add('hidden');
    return;
  }

  availability.textContent = otherTemplateCount === 1
    ? '1 other template available'
    : `${otherTemplateCount} other templates available`;
  availability.classList.remove('hidden');
}
