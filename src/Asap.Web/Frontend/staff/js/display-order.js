const displayLabelCollator = new Intl.Collator(undefined, {
  sensitivity: 'base',
  numeric: true
});

function normalizedLabel(value) {
  return value === null || value === undefined ? '' : String(value).trim();
}

function firstLabel(...values) {
  for (const value of values) {
    const label = normalizedLabel(value);
    if (label) return label;
  }
  return '';
}

export function compareDisplayLabels(first, second) {
  return displayLabelCollator.compare(normalizedLabel(first), normalizedLabel(second));
}

export function sortByDisplayLabel(items, getLabel = item => item, getKey = item => item?.id ?? '') {
  return (Array.isArray(items) ? items : [])
    .map((item, index) => ({ item, index }))
    .sort((first, second) => {
      const labelComparison = compareDisplayLabels(getLabel(first.item), getLabel(second.item));
      if (labelComparison !== 0) return labelComparison;

      const keyComparison = compareDisplayLabels(getKey(first.item), getKey(second.item));
      return keyComparison || first.index - second.index;
    })
    .map(entry => entry.item);
}

export function organizationDisplayName(organization) {
  const name = firstLabel(organization?.name, organization?.displayName);
  if (name) return name;

  const id = organization?.id ?? organization?.orgId ?? organization?.organizationId;
  if (String(id) === '1') return 'System level';
  if (id !== null && id !== undefined && String(id).trim() !== '') return `Library ${id}`;
  return 'Unnamed organization';
}

export function staffDisplayName(staff) {
  const name = firstLabel(
    staff?.displayName,
    staff?.label,
    staff?.name,
    staff?.userPrincipalName
  );
  if (name) return name;

  const id = staff?.id ?? staff?.staffId;
  return `Staff ${id === null || id === undefined || String(id).trim() === '' ? '?' : id}`;
}

export function pickupBranchDisplayName(branch) {
  const name = firstLabel(branch?.name, branch?.displayName, branch?.label);
  if (name) return name;

  const id = branch?.id ?? branch?.branchId;
  return `Branch ${id === null || id === undefined || String(id).trim() === '' ? '?' : id}`;
}
