'use strict';

const standardTemplateKeys = [
  'suggestion_submitted', 'purchase_approved', 'already_owned', 'rejected', 'hold_placed'
];

function copy(value) {
  return value === undefined ? undefined : structuredClone(value);
}

function projectTemplateRows(authoritative) {
  if (Array.isArray(authoritative.templateEditor)) {
    return authoritative.templateEditor.map(row => ({
      ...copy(row), name: row.displayName ?? '', overridden: !!row.hasOverride,
      hadOverride: !!row.hasOverride, subject: row.subject ?? '', body: row.body ?? ''
    }));
  }

  const scope = String(authoritative.orgId ?? 'system');
  const all = authoritative.stored?.templates ?? [];
  const sources = all.filter(row => String(row.organizationId) === '1');
  const local = all.filter(row => String(row.organizationId) === scope);
  return sources.concat(local.filter(row => row.isCustom)).map(source => {
    const override = scope === 'system' || String(source.organizationId) !== '1'
      ? null
      : local.find(row => !row.isCustom && String(row.sourceTemplateId) === String(source.id));
    const current = override ?? source;
    const displayName = override?.displayName || source.displayName || source.templateKey;
    return {
      referenceId: String(source.id), id: String(current.id), templateKey: source.templateKey,
      sourceTemplateId: override?.sourceTemplateId ??
        (String(source.organizationId) === '1' ? String(source.id) : null),
      organizationId: String(current.organizationId), isCustom: !!source.isCustom,
      displayName, name: displayName, subject: override?.subject || source.subject || '',
      body: override?.body || source.body || '', enabled: !!source.enabled && override?.enabled !== false,
      hasOverride: !!override, canReset: !!override, overridden: !!override, hadOverride: !!override,
      subjectInherited: !override?.subject, bodyInherited: !override?.body,
      nameInherited: !override?.displayName, sortOrder: current.sortOrder,
      sourceVersion: source.version, version: current.version
    };
  });
}

function projectLegacySettingsFixture(authoritative) {
  if (!authoritative?.stored) {
    return copy(authoritative);
  }

  const scope = String(authoritative.orgId ?? 'system');
  const stored = authoritative.stored;
  const formats = copy(stored.formats ?? []);
  const providers = copy(stored.providers ?? []);
  const workflow = copy(authoritative.effective?.workflow ?? stored.workflow ?? authoritative.workflow ?? {});
  workflow.commonAuthorsList = (authoritative.effective?.commonCreators ?? []).join('\n');
  workflow.allowedPatronCodeIds = (authoritative.effective?.allowedPatronCodeIds ?? []).join(',');
  for (const provider of providers) {
    const match = /^external_search_([1-4])$/.exec(provider.key ?? '');
    if (!match) continue;
    workflow[`externalSearch${match[1]}Enabled`] = !!provider.isEnabled;
    workflow[`externalSearch${match[1]}Label`] = provider.label ?? '';
    workflow[`externalSearch${match[1]}UrlTemplate`] = provider.urlTemplate ?? '';
  }

  const rulesByFormat = new Map();
  for (const row of stored.customFieldRules ?? []) {
    if (!rulesByFormat.has(row.formatCode)) rulesByFormat.set(row.formatCode, {});
    rulesByFormat.get(row.formatCode)[row.fieldKey] = {
      mode: row.mode, labelOverride: row.labelOverride
    };
  }
  const labels = {};
  const rules = {};
  for (const format of formats) {
    labels[format.code] = format.label ?? format.code;
    const effective = authoritative.effective?.formats?.find(row => row.code === format.code);
    rules[format.code] = {
      messageBehavior: format.messageBehavior ?? 'none', message: format.message ?? '',
      fields: Object.fromEntries(['title', 'author', 'identifier', 'publication'].map(name => [name, {
        mode: format[`${name}Mode`] ?? (name === 'title' ? 'required' : 'optional'),
        label: format[`${name}Label`] ?? ({ title: 'Title', author: 'Author',
          identifier: 'Identifier number', publication: 'Publication Timing' })[name]
      }])),
      customFields: copy(rulesByFormat.get(format.code) ?? effective?.customFields ?? {})
    };
  }

  const branding = stored.branding ?? {};
  const systemPublication = stored.configuredSystem?.publicationOptions?.values ?? [];
  const libraryPublication = stored.libraryOverride?.publicationOptions?.values ?? [];
  const publicationOptions = authoritative.publicationOptionsEditor ??
    (scope !== 'system' && stored.libraryOverride?.publicationOptions?.exists
      ? libraryPublication : systemPublication);
  const uiText = {
    ...copy(authoritative.ui_text ?? {}), formatLabels: labels,
    formatOrder: formats.map(row => row.code),
    availableFormats: formats.filter(row => row.isEnabled).map(row => row.code),
    formatRules: rules, additionalFieldDefinitions: copy(stored.customFields ?? []),
    publicationOptions: copy(publicationOptions),
    logoAlt: branding.altText ?? authoritative.effective?.logoAltText ?? '',
    logoUrl: copy(authoritative.effective?.logoUrl ?? null),
    brandingInherited: scope !== 'system' && !branding.version,
    patronSettingsInherited: scope !== 'system' && !stored.libraryOverride?.patron
  };
  if (scope === 'system') {
    uiText.systemNotEnabledMessage = stored.systemSettings?.systemNotEnabledMessage ?? null;
    uiText.misconfiguredMessage = stored.systemSettings?.misconfiguredMessage ?? null;
  }

  const templates = projectTemplateRows(authoritative);
  const emails = copy(authoritative.emails ?? {});
  for (const key of standardTemplateKeys) {
    const row = templates.find(item => item.templateKey === key);
    if (row) emails[key] = { subject: row.subject, body: row.body };
  }
  const formatById = new Map(formats.map(row => [String(row.id), row.code]));
  const autoClaimRules = (stored.autoClaimRules ?? []).filter(row => row.active === true || row.isActive === true).map(row => ({
    format: formatById.get(String(row.materialFormatId)),
    staffUserId: String(row.staffUserId), materialFormatId: String(row.materialFormatId)
  }));

  return {
    orgId: scope, version: authoritative.version, isOverride: !!authoritative.isOverride,
    workflow, uiText, emails,
    systemSettings: { ...copy(stored.systemSettings ?? {}),
      enabledLibraryOrgIds: copy(authoritative.enabledLibraryOrgIds ?? []) },
    polaris: copy(stored.polaris ?? {}), providers, formats, templates,
    autoClaimRules, autoClaimStaff: (authoritative.autoClaimStaff ?? []).map(row => ({
      id: String(row.id), displayName: row.displayName ?? row.label,
      libraryOrgId: row.libraryOrgId ?? scope
    }))
  };
}

module.exports = { projectLegacySettingsFixture };
