import { pb } from './state.js';
import { workflowStatusLabel } from './modals.js';

function getStorageKey() {
  const userId = pb.authStore.model?.id;
  if (!userId) return null;
  return `asap.recentSuggestions.${userId}`;
}

export function rememberRecentSuggestion(row) {
  if (!row || !row.id) return;
  
  // We're focusing on title_requests for the first version as per the plan
  if (row.type === 'additional_copy') return; 

  const storageKey = getStorageKey();
  if (!storageKey) return;

  const suggestion = {
    id: row.id,
    type: row.type || 'title_request',
    title: row.title || 'Unknown Title',
    author: row.author || '',
    status: row.status,
    accessedAt: new Date().toISOString()
  };

  let recent = getRecentSuggestions();
  
  // Deduplicate
  recent = recent.filter(r => r.id !== suggestion.id);
  
  // Add to front
  recent.unshift(suggestion);
  
  // Trim to 5
  recent = recent.slice(0, 5);
  
  try {
    localStorage.setItem(storageKey, JSON.stringify(recent));
  } catch (e) {
    console.warn("Failed to save recent suggestions to localStorage", e);
  }
}

export function getRecentSuggestions() {
  const storageKey = getStorageKey();
  if (!storageKey) return [];
  
  try {
    const data = localStorage.getItem(storageKey);
    return data ? JSON.parse(data) : [];
  } catch (e) {
    console.warn("Failed to load recent suggestions from localStorage", e);
    return [];
  }
}

export function clearRecentSuggestionsForCurrentUser() {
  const storageKey = getStorageKey();
  if (storageKey) {
    localStorage.removeItem(storageKey);
  }
}

export function initRecentSuggestionsDropdown() {
  const btn = document.getElementById('recentSuggestionsBtn');
  const menu = document.getElementById('recent-suggestions-menu');
  if (!btn || !menu) return;

  btn.addEventListener('click', (e) => {
    e.preventDefault();
    e.stopPropagation();
    const isShowing = menu.classList.contains('show');
    
    // Close other dropdowns if we had any, but we just toggle this one
    if (isShowing) {
      menu.classList.remove('show');
      btn.setAttribute('aria-expanded', 'false');
    } else {
      menu.classList.add('show');
      btn.setAttribute('aria-expanded', 'true');
    }
  });

  // Close when clicking outside
  document.addEventListener('click', (e) => {
    if (menu.classList.contains('show') && !menu.contains(e.target) && !btn.contains(e.target)) {
      menu.classList.remove('show');
      btn.setAttribute('aria-expanded', 'false');
    }
  });
}

export function renderRecentSuggestionsSwitcher() {

  const menu = document.getElementById('recent-suggestions-menu');
  if (!menu) return;

  const recent = getRecentSuggestions();
  menu.replaceChildren();

  if (recent.length === 0) {
    const emptyItem = document.createElement('span');
    emptyItem.className = 'dropdown-item disabled text-muted small';
    emptyItem.textContent = 'No recent suggestions yet.';
    menu.appendChild(emptyItem);
    return;
  }

  recent.forEach(r => {
    const item = document.createElement('a');
    item.className = 'dropdown-item recent-suggestion-item border-bottom py-2';
    item.href = '#';
    
    // Status label formatting
    let statusDisplay = workflowStatusLabel(r.status) || r.status;
    
    item.innerHTML = `
      <div class="font-weight-bold text-truncate" title="${r.title}">${r.title}</div>
      <div class="small text-muted text-truncate">
        ${r.author ? `${r.author} &middot; ` : ''}${statusDisplay}
      </div>
    `;

    item.addEventListener('click', (e) => {
      e.preventDefault();
      
      // Close the dropdown menu
      menu.classList.remove('show');
      const btn = document.getElementById('recentSuggestionsBtn');
      if (btn) btn.setAttribute('aria-expanded', 'false');

      // Dispatch custom event to let grid.js handle the jump
      document.dispatchEvent(new CustomEvent('asap:recent-suggestion-selected', {
        detail: { id: r.id, status: r.status }
      }));
    });

    menu.appendChild(item);
  });
}
