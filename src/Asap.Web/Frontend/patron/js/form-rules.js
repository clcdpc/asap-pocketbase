export function normalizeMode(value, fallback) {
  return ['required', 'optional', 'hidden'].includes(value) ? value : fallback || 'optional';
}

export function normalizeMessageBehavior(value, fallback) {
  return ['none', 'message', 'ebookMessage', 'eaudiobookMessage'].includes(value) ? value : fallback || 'none';
}

export function normalizeFormatRule(formatKey, rawRule, defaultRule, bookDefaultRule, fieldKeys) {
  const normalized = {
    messageBehavior: 'none',
    fields: {},
    customFields: {}
  };
  const incomingRule = rawRule && typeof rawRule === 'object' ? rawRule : {};
  const fallbackRule = defaultRule || bookDefaultRule || { messageBehavior: 'none', fields: {} };

  normalized.messageBehavior = normalizeMessageBehavior(incomingRule.messageBehavior, fallbackRule.messageBehavior);
  normalized.message = String(incomingRule.message || fallbackRule.message || '').trim();

  const incomingFields = incomingRule.fields || {};
  fieldKeys.forEach(field => {
    const fallbackField = (fallbackRule.fields && fallbackRule.fields[field]) || (bookDefaultRule.fields && bookDefaultRule.fields[field]) || {};
    const incomingField = incomingFields[field] || {};
    let mode = normalizeMode(incomingField.mode, fallbackField.mode);
    if (field === 'title') mode = 'required';
    normalized.fields[field] = {
      mode,
      label: String(incomingField.label || fallbackField.label || field).trim() || fallbackField.label || field
    };
  });

  if (incomingRule.customFields && typeof incomingRule.customFields === 'object') {
    Object.keys(incomingRule.customFields).forEach(key => {
      const incomingCustom = incomingRule.customFields[key] || {};
      normalized.customFields[key] = {
        mode: normalizeMode(incomingCustom.mode, 'hidden')
      };
    });
  }

  return normalized;
}

export function normalizeFormatRules(rawRules, defaultRules, formatKeys, fieldKeys) {
  const normalized = structuredClone(defaultRules);
  const incoming = rawRules && typeof rawRules === 'object' ? rawRules : {};
  const allKeys = new Set([...formatKeys, ...Object.keys(incoming)]);
  const bookDefaultRule = defaultRules.book;

  allKeys.forEach(format => {
    normalized[format] = normalizeFormatRule(
      format,
      incoming[format],
      normalized[format],
      bookDefaultRule,
      fieldKeys
    );
  });

  return normalized;
}

export function getFieldRule(formatRules, formatKey, fieldKey, defaultRules) {
  const formatRule = (formatRules && (formatRules[formatKey] || formatRules.book)) || {};
  const defaultField = defaultRules.book.fields[fieldKey];
  return (formatRule.fields && formatRule.fields[fieldKey]) || defaultField;
}

export function isPhysicalSubmissionAllowed(formatRules, formatKey) {
  const rule = (formatRules && (formatRules[formatKey] || formatRules.book)) || {};
  return !rule.messageBehavior || rule.messageBehavior === 'none';
}
