const formatCodes = ['book', 'audiobook_cd', 'dvd', 'music_cd', 'ebook', 'eaudiobook'];

function patronUiText(prefix, overrides = {}) {
  const { customField, ...payloadOverrides } = overrides;
  const formatRules = Object.fromEntries(formatCodes.map(code => [code, {
    messageBehavior: 'none',
    message: null,
    fields: {
      title: { mode: 'required', label: `${prefix} Title` },
      author: { mode: 'required', label: `${prefix} Author` },
      identifier: { mode: 'optional', label: `${prefix} Identifier` },
      publication: { mode: 'optional', label: `${prefix} Publication` }
    },
    customFields: customField && code === 'book' ? {
      [customField.key]: {
        mode: customField.mode || 'optional',
        label: customField.ruleLabel === undefined ? customField.label : customField.ruleLabel
      }
    } : {}
  }]));

  const payload = {
    successTitle: `${prefix} Submitted`,
    successMessage: `${prefix} submission succeeded.`,
    alreadySubmittedMessage: `${prefix} suggestion already submitted.`,
    pageTitle: `${prefix} Material Suggestion`,
    barcodeLabel: `${prefix} Card`,
    pinLabel: `${prefix} PIN`,
    loginPrompt: `${prefix} login prompt.`,
    loginNote: `${prefix} login note.`,
    suggestionFormNote: `${prefix} suggestion note.`,
    noEmailMessage: `${prefix} no email message.`,
    publicationOptions: [`${prefix} publication`],
    formatRules,
    formatLabels: Object.fromEntries(formatCodes.map(code => [code, `${prefix} ${code}`])),
    availableFormats: ['book', 'ebook'],
    additionalFieldDefinitions: customField ? [{
      id: customField.key,
      key: customField.key,
      type: customField.type || 'text',
      label: customField.label,
      helpText: customField.helpText || null,
      enabled: true,
      options: []
    }] : [],
    commonAuthorsEnabled: false,
    commonAuthorsList: '',
    commonAuthorsLabel: `${prefix} Common Authors`,
    commonAuthorsHelp: `${prefix} common authors help.`,
    commonAuthorsMessage: `${prefix} common authors message.`,
    duplicateStatusLabels: { suggestion: 'Received' },
    ebookMessage: `${prefix} ebook message.`,
    eaudiobookMessage: `${prefix} eaudiobook message.`,
    misconfiguredMessage: `${prefix} misconfigured message.`,
    externalSearch1Enabled: false,
    externalSearch1Label: '',
    externalSearch1UrlTemplate: '',
    externalSearch2Enabled: false,
    externalSearch2Label: '',
    externalSearch2UrlTemplate: '',
    externalSearch3Enabled: false,
    externalSearch3Label: '',
    externalSearch3UrlTemplate: '',
    externalSearch4Enabled: false,
    externalSearch4Label: '',
    externalSearch4UrlTemplate: '',
    allowPatronAutoholdOptOut: false,
    logoUrl: '/jpl.png',
    logoAlt: `${prefix} logo`,
    systemNotEnabled: false,
    systemNotEnabledMessage: `${prefix} system unavailable message.`,
    library: `${prefix} Library`
  };

  return { ...payload, ...payloadOverrides };
}

function patronSession(barcode, token, overrides = {}) {
  const effectiveLibraryOrgId = Object.prototype.hasOwnProperty.call(overrides, 'effectiveLibraryOrgId')
    ? overrides.effectiveLibraryOrgId
    : 2;
  const uiText = Object.prototype.hasOwnProperty.call(overrides, 'ui_text')
    ? overrides.ui_text
    : patronUiText(barcode, Object.prototype.hasOwnProperty.call(overrides, 'effectiveLibraryOrgName')
      ? { library: overrides.effectiveLibraryOrgName }
      : {});
  const currentLibraryOrgId = Number.isInteger(effectiveLibraryOrgId) ? effectiveLibraryOrgId : 2;
  const effectiveLibraryOrgName = Object.prototype.hasOwnProperty.call(overrides, 'effectiveLibraryOrgName')
    ? overrides.effectiveLibraryOrgName
    : (typeof uiText?.library === 'string' ? uiText.library : 'Effective Library');

  return {
    token,
    barcode,
    email: `${barcode}@example.org`,
    preferredPickupBranchId: 101,
    selectedPickupBranchId: 101,
    pickupBranches: [{ id: 101, label: 'Main Library' }],
    currentPreferredPickupBranchId: 101,
    pickupBranchWarning: '',
    record: { email: `${barcode}@example.org`, libraryOrgId: currentLibraryOrgId },
    experienceLibraryOrgId: currentLibraryOrgId,
    experienceLibraryOrgName: effectiveLibraryOrgName,
    patronHomeLibraryOrgId: currentLibraryOrgId,
    patronHomeLibraryOrgName: effectiveLibraryOrgName,
    effectiveLibraryOrgId,
    effectiveLibraryOrgName,
    crossLibraryLogin: false,
    ui_text: uiText,
    ...overrides
  };
}

module.exports = { patronSession, patronUiText };
