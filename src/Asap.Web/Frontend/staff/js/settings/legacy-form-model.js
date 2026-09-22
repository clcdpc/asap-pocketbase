function array(value) {
  return Array.isArray(value) ? value : [];
}

function stringId(value) {
  return value === undefined || value === null ? '' : String(value);
}

function fieldRule(format, name, defaultMode, defaultLabel) {
  const nested = format?.[name] || {};
  const capitalized = name.charAt(0).toUpperCase() + name.slice(1);
  return {
    mode: nested.mode || format?.[`${name}Mode`] || defaultMode,
    label: nested.label || format?.[`${name}Label`] || defaultLabel || capitalized
  };
}

function buildFormatRule(format) {
  return {
    messageBehavior: format.messageBehavior || 'none',
    message: format.message || '',
    fields: {
      title: fieldRule(format, 'title', 'required', 'Title'),
      author: fieldRule(format, 'author', 'optional', 'Author'),
      identifier: fieldRule(format, 'identifier', 'optional', 'Identifier number'),
      publication: fieldRule(format, 'publication', 'optional', 'Publication Timing')
    },
    customFields: format.customFields || {}
  };
}

function mergeFormats(storedFormats, effectiveFormats) {
  const effectiveById = new Map(effectiveFormats.map(item => [stringId(item?.id), item]));
  const effectiveByCode = new Map(effectiveFormats.map(item => [item?.code, item]));
  const source = storedFormats.length ? storedFormats : effectiveFormats;
  return source
    .filter(item => item && item.code)
    .map(item => {
      const effective = effectiveById.get(stringId(item.id)) || effectiveByCode.get(item.code) || {};
      return { ...item, ...effective, ownerOrganizationId: item.ownerOrganizationId ?? effective.ownerOrganizationId };
    })
    .sort((left, right) => Number(left.sortOrder || 0) - Number(right.sortOrder || 0));
}

function mergeProviders(storedProviders, effectiveProviders) {
  const effectiveById = new Map(effectiveProviders.map(item => [stringId(item?.id), item]));
  const effectiveByKey = new Map(effectiveProviders.map(item => [item?.key, item]));
  const source = storedProviders.length ? storedProviders : effectiveProviders;
  return source
    .filter(item => item && item.key)
    .map((item, index) => {
      const effective = effectiveById.get(stringId(item.id)) || effectiveByKey.get(item.key) || {};
      return {
        ...item,
        ...effective,
        sortOrder: effective.sortOrder ?? item.sortOrder ?? ((index + 1) * 10)
      };
    })
    .sort((left, right) => Number(left.sortOrder || 0) - Number(right.sortOrder || 0));
}

function creatorValues(values) {
  return array(values)
    .map(item => typeof item === 'string' ? item : item?.value)
    .filter(value => value !== undefined && value !== null)
    .map(String);
}

function patronCodeValues(values) {
  return array(values).map(stringId).filter(Boolean);
}

function scopedSnapshot(systemSnapshot, librarySnapshot, isSystem, fallback) {
  if (isSystem && Array.isArray(systemSnapshot?.values)) return systemSnapshot.values;
  if (!isSystem && librarySnapshot?.exists && Array.isArray(librarySnapshot.values) && librarySnapshot.values.length) {
    return librarySnapshot.values;
  }
  if (!isSystem && Array.isArray(systemSnapshot?.values)) return systemSnapshot.values;
  return fallback;
}

function mapAutoClaimRules(rules, formats) {
  const codeById = new Map(formats.map(format => [stringId(format.id), format.code]));
  return array(rules).map(rule => ({
    ...rule,
    materialFormatId: stringId(rule.materialFormatId || rule.formatId),
    staffUserId: stringId(rule.staffUserId || rule.staffId),
    format: rule.format || codeById.get(stringId(rule.materialFormatId || rule.formatId)) || ''
  })).filter(rule => rule.format && rule.materialFormatId);
}

function mapAutoClaimStaff(staff, contextOrgId) {
  return array(staff).map(item => ({
    ...item,
    id: stringId(item.id),
    displayName: item.displayName || item.label || item.userPrincipalName || item.username || 'Staff',
    libraryOrgId: stringId(item.libraryOrgId || item.organizationId || contextOrgId)
  }));
}

export function buildLegacySettingsFormModel(settings, contextOrgId = 'system') {
  settings = settings || {};
  const stored = settings.stored || {};
  const effective = settings.effective || {};
  const isSystem = contextOrgId === 'system';
  const configuredSystem = stored.configuredSystem || {};
  const libraryOverride = stored.libraryOverride || {};

  const workflow = {
    ...(isSystem
      ? (stored.workflow || configuredSystem.workflow || settings.workflow || {})
      : (effective.workflow || settings.workflow || stored.workflow || {}))
  };

  const creatorSource = scopedSnapshot(
    configuredSystem.commonCreators,
    libraryOverride.commonCreators,
    isSystem,
    stored.commonCreators ?? effective.commonCreators);
  const creators = creatorValues(creatorSource);
  if (Array.isArray(creatorSource)) {
    workflow.commonAuthorsList = creators.join('\n');
  }
  const patronCodeSource = scopedSnapshot(
    configuredSystem.allowedPatronCodeIds,
    libraryOverride.allowedPatronCodeIds,
    isSystem,
    stored.allowedPatronCodeIds ?? effective.allowedPatronCodeIds);
  const patronCodeIds = patronCodeValues(patronCodeSource);
  if (Array.isArray(patronCodeSource)) {
    workflow.allowedPatronCodeIds = patronCodeIds.join(',');
  }

  const storedProviders = array(stored.providers);
  const effectiveProviders = array(effective.externalSearchProviders);
  const providers = mergeProviders(storedProviders, effectiveProviders);
  providers.forEach(provider => {
    const match = /^external_search_([1-4])$/.exec(provider.key);
    if (!match) return;
    const index = match[1];
    workflow[`externalSearch${index}Enabled`] = !!provider.isEnabled;
    workflow[`externalSearch${index}Label`] = provider.label ?? '';
    workflow[`externalSearch${index}UrlTemplate`] = provider.urlTemplate ?? '';
  });

  const storedFormats = array(stored.formats);
  const effectiveFormats = array(effective.formats);
  const formats = mergeFormats(storedFormats, effectiveFormats);
  const formatLabels = {};
  const formatRules = {};
  formats.forEach(format => {
    formatLabels[format.code] = format.label || format.code;
    formatRules[format.code] = buildFormatRule(format);
  });

  const authoritativeCustomFields = Array.isArray(stored.customFields)
    ? stored.customFields
    : (Array.isArray(effective.customFields) ? effective.customFields : null);
  const publicationOptions = scopedSnapshot(
    configuredSystem.publicationOptions,
    libraryOverride.publicationOptions,
    isSystem,
    settings.ui_text?.publicationOptions ?? effective.publicationOptions ?? stored.publicationOptions);
  const branding = isSystem ? configuredSystem.branding : libraryOverride.branding;
  const uiText = {
    ...(effective || {}),
    ...(settings.ui_text || {}),
    logoAlt: settings.ui_text?.logoAlt ?? branding?.altText ?? effective.logoAltText ?? '',
    brandingInherited: !isSystem && !branding?.version,
    publicationOptions,
    formatLabels,
    formatOrder: formats.map(format => format.code),
    availableFormats: formats.filter(format => format.isEnabled).map(format => format.code),
    formatRules,
    additionalFieldDefinitions: authoritativeCustomFields || []
  };

  const autoClaimRules = mapAutoClaimRules(stored.autoClaimRules ?? settings.formatClaimRules, formats);
  const autoClaimStaff = mapAutoClaimStaff(settings.autoClaimStaff, contextOrgId);

  return {
    contextOrgId,
    isSystem,
    isOverride: !!settings.isOverride,
    systemSettings: stored.systemSettings || settings.systemSettings || settings,
    polaris: stored.polaris || settings.polaris || {},
    emails: settings.emails || {},
    workflow,
    uiText,
    providers,
    formats,
    customFields: authoritativeCustomFields || [],
    autoClaimRules,
    autoClaimStaff,
    commonCreatorStateTrusted: Array.isArray(creatorSource),
    patronCodeStateTrusted: Array.isArray(patronCodeSource),
    publicationOptionStateTrusted: Array.isArray(publicationOptions),
    providerStateTrusted: Array.isArray(stored.providers) || Array.isArray(effective.externalSearchProviders),
    formatStateTrusted: Array.isArray(stored.formats) || Array.isArray(effective.formats),
    customFieldStateTrusted: isSystem || authoritativeCustomFields !== null,
    autoClaimStateTrusted: isSystem || (Array.isArray(stored.autoClaimRules ?? settings.formatClaimRules) && Array.isArray(settings.autoClaimStaff)),
    source: settings
  };
}
