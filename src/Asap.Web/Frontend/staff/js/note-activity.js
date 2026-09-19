const RANGE_NOTE_REGEX = /^(\d{1,2}\/\d{1,2}\/\d{4}) to (\d{1,2}\/\d{1,2}\/\d{4}) \(Count: (\d+)\) (.*)$/;
const SINGLE_DATE_NOTE_REGEX = /^(\d{1,2}\/\d{1,2}\/\d{4}) (.*)$/;
const USER_SUFFIX_REGEX = /\s+by\s+([^.\n]+)\.?$/i;
const INITIAL_VISIBLE_EVENT_COUNT = 12;

function createElement(tagName, className, text) {
  const element = document.createElement(tagName);
  if (className) element.className = className;
  if (text !== undefined) element.textContent = text;
  return element;
}

function normalizeMessage(message) {
  return String(message || '').replace(/^\*\*\*ALERT\*\*\*\s+/, '').trim();
}

function eventTypeForMessage(message) {
  const normalized = normalizeMessage(message).toLowerCase();
  if (normalized.startsWith('skip:')) return 'Skip';
  if (normalized.startsWith('moved from') || normalized.startsWith('moved to')) return 'Status';
  if (normalized.startsWith('identifier number verification')) return 'Polaris';
  if (normalized.startsWith('created on behalf')) return 'Created';
  return 'Note';
}

function displayMessageForType(message, type) {
  if (type === 'Skip') return normalizeMessage(message).replace(/^SKIP:\s*/i, '').trim();
  return normalizeMessage(message);
}

function splitUserFromMessage(message) {
  const normalized = normalizeMessage(message);
  const match = normalized.match(USER_SUFFIX_REGEX);
  if (!match) return { message: normalized, user: '' };
  return {
    message: normalized.slice(0, match.index).trim().replace(/\s*\.$/, ''),
    user: match[1].trim()
  };
}

export function parseNoteLine(line) {
  const raw = String(line || '').trim();
  if (!raw) return null;

  let match = raw.match(RANGE_NOTE_REGEX);
  if (match) {
    const message = normalizeMessage(match[4]);
    const type = eventTypeForMessage(message);
    const split = splitUserFromMessage(displayMessageForType(message, type));
    return {
      startDate: match[1],
      endDate: match[2],
      groupDate: match[2],
      count: Math.max(parseInt(match[3], 10) || 1, 1),
      type,
      message: split.message,
      user: split.user,
      raw
    };
  }

  match = raw.match(SINGLE_DATE_NOTE_REGEX);
  if (match) {
    const message = normalizeMessage(match[2]);
    const type = eventTypeForMessage(message);
    const split = splitUserFromMessage(displayMessageForType(message, type));
    return {
      startDate: match[1],
      endDate: match[1],
      groupDate: match[1],
      count: 1,
      type,
      message: split.message,
      user: split.user,
      raw
    };
  }

  return {
    startDate: '',
    endDate: '',
    groupDate: 'Undated',
    count: 1,
    type: 'Note',
    message: raw,
    user: '',
    raw
  };
}

export function parseNoteActivity(notes) {
  return String(notes || '')
    .split(/\r?\n/)
    .map(parseNoteLine)
    .filter(Boolean);
}

export function groupNoteActivity(events) {
  const groups = [];
  const byDate = new Map();
  events.forEach(event => {
    const key = event.groupDate || 'Undated';
    if (!byDate.has(key)) {
      const group = { date: key, events: [] };
      byDate.set(key, group);
      groups.push(group);
    }
    byDate.get(key).events.push(event);
  });
  return groups;
}

function metaPartsForEvent(event) {
  const parts = [];
  if (event.count > 1) parts.push(`Count ${event.count}`);
  if (event.startDate && event.endDate && event.startDate !== event.endDate) {
    parts.push(`${event.startDate} to ${event.endDate}`);
  }
  if (event.user) parts.push(event.user);
  if (!event.user && event.type !== 'Note') parts.push('System');
  return parts;
}

function renderEvent(event) {
  const item = createElement('li', 'note-activity-item');
  const badge = createElement('span', `note-activity-badge note-activity-badge--${event.type.toLowerCase()}`, event.type);
  const body = createElement('div', 'note-activity-body');
  const message = createElement('div', 'note-activity-message', event.message || event.raw);
  const metaParts = metaPartsForEvent(event);

  body.append(message);
  if (metaParts.length) {
    body.append(createElement('div', 'note-activity-meta', metaParts.join(' | ')));
  }
  item.append(badge, body);
  return item;
}

function renderGroups(groups, limit) {
  const fragment = document.createDocumentFragment();
  let renderedCount = 0;

  groups.forEach(group => {
    const visibleEvents = [];
    group.events.forEach(event => {
      if (limit === null || renderedCount < limit) {
        visibleEvents.push(event);
        renderedCount++;
      }
    });
    if (!visibleEvents.length) return;

    const section = createElement('section', 'note-activity-group');
    section.append(createElement('h6', 'note-activity-date', group.date));
    const list = createElement('ol', 'note-activity-list');
    visibleEvents.forEach(event => list.append(renderEvent(event)));
    section.append(list);
    fragment.append(section);
  });

  return fragment;
}

export function renderNoteActivity(notes) {
  const container = createElement('div', 'note-activity');
  const events = parseNoteActivity(notes);
  if (!events.length) {
    container.append(createElement('p', 'note-activity-empty', 'No notes yet.'));
    return container;
  }

  const groups = groupNoteActivity(events);
  const body = createElement('div', 'note-activity-groups');
  body.append(renderGroups(groups, INITIAL_VISIBLE_EVENT_COUNT));
  container.append(body);

  if (events.length > INITIAL_VISIBLE_EVENT_COUNT) {
    const button = createElement('button', 'btn btn-link btn-sm note-activity-show-more', `Show ${events.length - INITIAL_VISIBLE_EVENT_COUNT} older entries`);
    button.type = 'button';
    button.addEventListener('click', () => {
      body.replaceChildren(renderGroups(groups, null));
      button.remove();
    });
    container.append(button);
  }

  return container;
}
