function escapeHtml(str) {
  return String(str || "")
    .replace(/&/g, "&amp;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;");
}

function clean(value) {
  return String(value || "").trim();
}

function getRealValue(combinedStr) {
  if (!combinedStr) return combinedStr;
  const parenIndex = combinedStr.indexOf(" (");
  if (parenIndex > 0) {
    return combinedStr.substring(0, parenIndex).trim();
  }
  return combinedStr.trim();
}

function replacePlaceholders(template, data, escape) {
  if (!template) return "";
  if (!clean(data && data.author)) {
    template = template.replace(/\s+by\s+{{author}}/gi, "");
  }
  return template.replace(/{{(\w+)}}/g, (match, key) => {
    const val = data[key] !== undefined ? data[key] : match;
    return escape ? escapeHtml(val) : val;
  });
}

function isValidEmail(value) {
  return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(String(value || "").trim());
}

function formatDateTime(value) {
  let date = value ? new Date(value) : new Date();
  if (isNaN(date.getTime())) {
    date = new Date();
  }
  return date.toLocaleString("en-US", {
    year: "numeric",
    month: "long",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit",
    timeZoneName: "short"
  });
}

function formatLabel(value, configuredLabels) {
  const defaultLabels = {
    book: "Book",
    ebook: "eBook",
    audiobook_cd: "Audiobook (Physical CD)",
    eaudiobook: "eAudiobook",
    dvd: "DVD",
    music_cd: "Music CD",
  };
  const labels = configuredLabels || {};
  return labels[value] || defaultLabels[value] || clean(value);
}

function reminderValue(value) {
  const text = clean(value);
  return text || "Not provided";
}

function reminderLine(label, value) {
  return label + ": " + reminderValue(value);
}

function htmlReminderLine(label, value) {
  return "<p><strong>" + escapeHtml(label) + ":</strong> " + escapeHtml(reminderValue(value)) + "</p>";
}

module.exports = {
  escapeHtml, clean, getRealValue,
  replacePlaceholders, isValidEmail, formatDateTime, formatLabel,
  reminderValue, reminderLine, htmlReminderLine
};
