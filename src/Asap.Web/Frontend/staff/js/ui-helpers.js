export function escapeAttr(value) {
  return String(value || '')
    .replace(/&/g, '&amp;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
}

export function escapeHtml(value) {
  return escapeAttr(value);
}

export function h(tag, attrs, children) {
  attrs = attrs || {};
  children = children || [];

  const el = document.createElement(tag);
  
  Object.keys(attrs).forEach(function (key) {
    var val = attrs[key];
    if (key === "class" || key === "className") {
      el.className = val;
    } else if (key === "textContent" || key === "text") {
      el.textContent = val;
    } else if (key.startsWith("on") && typeof val === "function") {
      el.addEventListener(key.slice(2).toLowerCase(), val);
    } else {
      el.setAttribute(key, val);
    }
  });

  if (!Array.isArray(children)) {
    children = [children];
  }
  
  children.forEach(function (child) {
    if (!child) return;
    if (typeof child === "string" || typeof child === "number") {
      el.appendChild(document.createTextNode(child));
    } else if (child instanceof HTMLElement) {
      el.appendChild(child);
    }
  });

  return el;
}

export function install() {
  window.escapeAttr = escapeAttr;
  window.escapeHtml = escapeHtml;
  window.h = h;
}
