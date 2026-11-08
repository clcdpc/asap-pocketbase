import { property } from './settings-values.js';

export function isLibraryOrganization(organization) {
  return Number(organization?.id) > 1 && Number(organization?.organizationCodeId) === 2;
}

export function validateOrganizationCatalog(response, scope, settingsData, superAdmin) {
  const values = response?.data ?? response;
  if (!Array.isArray(values) || values.length === 0) {
    throw new Error('The organization list is incomplete. Reload settings before saving.');
  }
  const ids = new Set();
  const rows = [];
  for (const value of values) {
    if (!value || typeof value !== 'object' || Array.isArray(value) ||
        !Number.isSafeInteger(value.id) || value.id < 1 || ids.has(value.id) ||
        typeof value.name !== 'string' || !value.name.trim() ||
        !Object.prototype.hasOwnProperty.call(value, 'organizationCodeId') ||
        value.organizationCodeId !== null && !Number.isSafeInteger(value.organizationCodeId) ||
        !Object.prototype.hasOwnProperty.call(value, 'parentOrganizationId') ||
        value.parentOrganizationId !== null && !Number.isSafeInteger(value.parentOrganizationId) ||
        typeof value.isActive !== 'boolean' || typeof value.version !== 'string' || !value.version.trim()) {
      throw new Error('The organization list is malformed or incomplete. Reload settings before saving.');
    }
    ids.add(value.id);
    rows.push(value);
  }

  if (superAdmin) {
    if (!ids.has(1)) {
      throw new Error('The organization list is missing the system organization. Reload settings before saving.');
    }
    const systemSettings = property(property(settingsData, 'stored'), 'systemSettings');
    const expectedEnabled = property(systemSettings, 'enabledLibraryOrgIds');
    const expectedLibraries = property(systemSettings, 'libraryOrgIds');
    if (!Array.isArray(expectedEnabled) || expectedEnabled.some(id => !Number.isSafeInteger(id) || id <= 1) ||
        new Set(expectedEnabled).size !== expectedEnabled.length ||
        !Array.isArray(expectedLibraries) || expectedLibraries.some(id => !Number.isSafeInteger(id) || id <= 1) ||
        new Set(expectedLibraries).size !== expectedLibraries.length) {
      throw new Error('The saved participation snapshot is incomplete. Reload settings before saving.');
    }
    const libraryRows = rows.filter(isLibraryOrganization).map(item => item.id).sort((first, second) => first - second);
    const expectedLibraryRows = [...expectedLibraries].sort((first, second) => first - second);
    if (JSON.stringify(libraryRows) !== JSON.stringify(expectedLibraryRows)) {
      throw new Error('The organization list is missing library rows. Reload settings before saving.');
    }
    const activeRows = rows.filter(item => isLibraryOrganization(item) && item.isActive)
      .map(item => item.id).sort((first, second) => first - second);
    const expectedRows = [...expectedEnabled].sort((first, second) => first - second);
    if (JSON.stringify(activeRows) !== JSON.stringify(expectedRows)) {
      throw new Error('The organization list is missing active libraries. Reload settings before saving.');
    }
  } else if (scope !== 'system' && !ids.has(Number(scope))) {
    throw new Error('The organization list is missing the selected library. Reload settings before saving.');
  }

  return rows;
}
