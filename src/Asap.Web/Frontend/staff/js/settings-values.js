export function clone(value) {
  return value === undefined ? undefined : JSON.parse(JSON.stringify(value));
}

export function object(value) {
  return value && typeof value === 'object' && !Array.isArray(value) ? value : {};
}

export function property(value, key) {
  const source = object(value);
  if (Object.prototype.hasOwnProperty.call(source, key)) return source[key];
  const pascal = key.charAt(0).toUpperCase() + key.slice(1);
  return source[pascal];
}

export function meaningful(value) {
  return value !== null && value !== undefined &&
    (typeof value !== 'string' || value.trim() !== '');
}

export function clean(value) {
  if (value === null || value === undefined) return null;
  const result = String(value).trim();
  return result || null;
}
