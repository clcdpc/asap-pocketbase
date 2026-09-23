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

function customFieldRule(value) {
  const rule = value && typeof value === 'object' ? value : {};
  return {
    mode: rule.mode || 'hidden',
    labelOverride: rule.labelOverride ?? rule.label ?? null
  };
}

function buildFormatRule(format) {
  const customFields = {};
  Object.entries(format.customFields || {}).forEach(([key, value]) => {
    customFields[key] = customFieldRule(value);
  });
  return {
    messageBehavior: format.messageBehavior || 'none',
    message: format.message || '',
    fields: {
      title: fieldRule(format, 'title', 'required', 'Title'),
      author: fieldRule(format, 'author', 'optional', 'Author'),
      identifier: fieldRule(format, 'identifier', 'optional', 'Identifier number'),
      publication: fieldRule(format, 'publication', 'optional', 'Publication Timing')
    },
    customFields
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
  return array(rules).filter(rule => rule && rule.active !== false && rule.isActive !== false).map(rule => ({
    ...rule,
    materialFormatId: stringId(rule.materialFormatId || rule.formatId),
    staffUserId: stringId(rule.staffUserId || rule.staffId),
    format: rule.format || codeById.get(stringId(rule.materialFormatId || rule.formatId)) || ''
  })).filter(rule => rule.format && rule.materialFormatId);
}

const STANDARD_TEMPLATE_KEYS = [
  'suggestion_submitted',
  'purchase_approved',
  'already_owned',
  'rejected',
  'hold_placed'
];

function templateRows(settings) {
  const stored = settings?.stored || {};
  if (Array.isArray(stored.templates)) return stored.templates;
  if (Array.isArray(settings?.emails?.templates)) return settings.emails.templates;
  return [
    ...(Array.isArray(stored.configuredSystem?.templates) ? stored.configuredSystem.templates : []),
    ...(Array.isArray(stored.libraryOverride?.templates) ? stored.libraryOverride.templates : [])
  ];
}

function normalizeTemplate(item, index) {
  const value = item && typeof item === 'object' ? item : {};
  return {
    ...value,
    id: stringId(value.id),
    version: stringId(value.version),
    organizationId: stringId(value.organizationId),
    sourceTemplateId: stringId(value.sourceTemplateId),
    templateKey: String(value.templateKey ?? value.key ?? `rejection:template_${index + 1}`),
    displayName: value.displayName ?? null,
    subject: value.subject ?? value.subjectTemplate ?? null,
    body: value.body ?? value.bodyTemplate ?? null,
    name: value.name ?? value.displayName ?? '',
    nameBaseline: value.nameBaseline ?? value.displayName ?? value.name ?? '',
    loadedSubject: value.loadedSubject ?? value.subject ?? value.subjectTemplate ?? null,
    loadedBody: value.loadedBody ?? value.body ?? value.bodyTemplate ?? null,
    loadedName: value.loadedName ?? value.displayName ?? value.name ?? '',
    enabled: value.enabled !== false && value.isHidden !== true,
    isCustom: value.isCustom === true || value.custom === true,
    sortOrder: value.sortOrder ?? (index + 1) * 10
  };
}

function mapTemplates(settings, isSystem, contextOrgId) {
  const stored = settings?.stored || {};
  const rows = templateRows(settings).map(normalizeTemplate);
  const configuredSystem = Array.isArray(stored.configuredSystem?.templates)
    ? stored.configuredSystem.templates.map(normalizeTemplate)
    : [];
  const systemRows = (configuredSystem.length ? configuredSystem : rows)
    .filter(item => item.organizationId === '1');
  const libraryId = stringId(settings?.orgId || contextOrgId);
  const libraryRows = rows.filter(item => item.organizationId === libraryId && libraryId && libraryId !== 'system');
  const sourceRows = isSystem ? systemRows : systemRows.map((system, index) => {
    const override = libraryRows.find(item => !item.isCustom && item.sourceTemplateId === system.id);
    const current = override || system;
    return {
      ...system,
      ...current,
      id: current.id || system.id,
      version: current.version || system.version,
      organizationId: current.organizationId || system.organizationId,
      sourceTemplateId: system.id,
      templateKey: system.templateKey,
      displayName: current.displayName ?? system.displayName,
      name: current.displayName ?? system.displayName ?? '',
      nameBaseline: current.displayName ?? system.displayName ?? '',
      loadedSubject: current.subject ?? system.subject,
      loadedBody: current.body ?? system.body,
      loadedName: current.displayName ?? system.displayName ?? '',
      subject: current.subject ?? system.subject,
      body: current.body ?? system.body,
      enabled: system.enabled && (!override || override.enabled),
      isCustom: false,
      overridden: !!override,
      hadOverride: !!override,
      subjectBaseline: system.subject,
      bodyBaseline: system.body,
      displayNameBaseline: system.displayName,
      enabledBaseline: system.enabled,
      rawSubject: override?.subject ?? null,
      rawBody: override?.body ?? null,
      rawDisplayName: override?.displayName ?? null,
      rawEnabled: override ? override.enabled : null,
      sourceIndex: index
    };
  });

  if (!isSystem) {
    sourceRows.push(...libraryRows.filter(item => item.isCustom).map(item => ({
      ...item,
      overridden: true,
      hadOverride: true,
      subjectBaseline: null,
      bodyBaseline: null,
      displayNameBaseline: null,
      enabledBaseline: item.enabled,
      rawSubject: item.subject,
      rawBody: item.body,
      rawDisplayName: item.displayName,
      rawEnabled: item.enabled
    })));
  }

  const byKey = new Map(sourceRows.map(item => [item.templateKey, item]));
  const legacy = {};
  STANDARD_TEMPLATE_KEYS.forEach(key => {
    const template = byKey.get(key);
    if (template) {
      legacy[key] = {
        ...template,
        subject: template.subject ?? '',
        body: template.body ?? ''
      };
    }
  });
  if (sourceRows.length > 0) {
    legacy.rejection_templates = sourceRows
      .filter(item => item.templateKey.startsWith('rejection:') && item.templateKey !== 'rejection:rejected')
      .map(item => ({ ...item, name: item.displayName || item.name || '' }));
  }

  return { rows: sourceRows, emails: legacy, trusted: rows.length > 0 || configuredSystem.length > 0 };
}

function mergeObjects(...values) {
  return values
    .filter(value => value && typeof value === 'object' && !Array.isArray(value))
    .reverse()
    .reduce((result, value) => Object.assign(result, value), {});
}

function baselineFormats(configuredSystem, effectiveFormats) {
  const configured = array(configuredSystem?.formats);
  return configured.length ? configured : array(effectiveFormats);
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
      ? { ...(settings.workflow || {}), ...(stored.workflow || {}), ...(configuredSystem.workflow || {}), ...(effective.workflow || {}) }
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
  const systemFormatValues = baselineFormats(configuredSystem, effectiveFormats);
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
  if (isSystem) {
    uiText.systemNotEnabledMessage = stored.systemSettings?.systemNotEnabledMessage ?? uiText.systemNotEnabledMessage;
    uiText.misconfiguredMessage = stored.systemSettings?.misconfiguredMessage ?? uiText.misconfiguredMessage;
  }

  const autoClaimRules = mapAutoClaimRules(stored.autoClaimRules ?? settings.formatClaimRules, formats);
  const autoClaimStaff = mapAutoClaimStaff(settings.autoClaimStaff, contextOrgId);
  const templateState = mapTemplates(settings, isSystem, contextOrgId);
  const systemWorkflow = mergeObjects(effective.workflow, settings.workflow, stored.workflow, configuredSystem.workflow);
  const systemPatron = mergeObjects(settings.ui_text, effective, stored.patron, configuredSystem.patron);
  const systemEmail = mergeObjects(settings.emails, effective.email, stored.email, configuredSystem.email);
  const systemSets = {
    commonCreators: scopedSnapshot(configuredSystem.commonCreators, null, true, stored.commonCreators ?? effective.commonCreators),
    allowedPatronCodeIds: scopedSnapshot(configuredSystem.allowedPatronCodeIds, null, true, stored.allowedPatronCodeIds ?? effective.allowedPatronCodeIds),
    publicationOptions: scopedSnapshot(configuredSystem.publicationOptions, null, true,
      settings.ui_text?.publicationOptions ?? effective.publicationOptions ?? stored.publicationOptions)
  };

  return {
    contextOrgId,
    isSystem,
    isOverride: !!settings.isOverride,
    systemSettings: stored.systemSettings || settings.systemSettings || settings,
    polaris: stored.polaris || settings.polaris || {},
    emails: { ...(settings.emails || {}), ...templateState.emails },
    templates: templateState.rows,
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
    templateStateTrusted: templateState.trusted,
    provenance: {
      authoritative: isSystem || Object.prototype.hasOwnProperty.call(stored, 'configuredSystem'),
      workflowOverride: isSystem ? {} : mergeObjects(libraryOverride.workflow),
      patronOverride: isSystem ? {} : mergeObjects(libraryOverride.patron),
      emailOverride: isSystem ? {} : mergeObjects(libraryOverride.email),
      systemWorkflow,
      systemPatron,
      systemEmail,
      systemSets,
      librarySets: isSystem ? {} : {
        commonCreators: libraryOverride.commonCreators,
        allowedPatronCodeIds: libraryOverride.allowedPatronCodeIds,
        publicationOptions: libraryOverride.publicationOptions
      },
      systemFormats: systemFormatValues,
      libraryBranding: isSystem ? null : libraryOverride.branding,
      systemBranding: configuredSystem.branding || settings.effective?.branding || {}
    },
    source: settings
  };
}
