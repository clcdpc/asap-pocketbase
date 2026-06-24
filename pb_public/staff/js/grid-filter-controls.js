import { tagCountsForRecords, getFlagDisplay } from './grid-policy.mjs';
import { escapeAttr } from './grid-utils.js';

export function hideTagFilter(ctx) {
  ctx.setActiveTagFilter('');
  if (ctx.tagFilterSelect) {
    ctx.tagFilterSelect.classList.add('hidden');
    ctx.tagFilterSelect.innerHTML = '';
  }
  if (ctx.additionalCopyStatusFilterSelect) {
    ctx.additionalCopyStatusFilterSelect.classList.add('hidden');
  }
}

export function hideClaimFilter(ctx) {
  if (ctx.claimFilterSelect) {
    ctx.claimFilterSelect.classList.add('hidden');
  }
}

export function updateTagFilter(records, ctx) {
  if (!ctx.tagFilterSelect || !ctx.staffGridFilterBar) return;
  const counts = tagCountsForRecords(records);
  if (!counts.length) {
    hideTagFilter(ctx);
    return;
  }

  const previous = ctx.activeTagFilter;
  ctx.tagFilterSelect.innerHTML = [
    '<option value="">All flags</option>',
    ...counts.map(([flag, count]) => {
      const display = getFlagDisplay(flag);
      return `<option value="${escapeAttr(flag)}">${escapeAttr(display.label)} (${count})</option>`;
    })
  ].join('');

  const stillExists = counts.some(([tag]) => tag === previous);
  ctx.tagFilterSelect.value = stillExists ? previous : '';
  ctx.setActiveTagFilter(ctx.tagFilterSelect.value);
  ctx.tagFilterSelect.classList.remove('hidden');
  ctx.staffGridFilterBar.classList.remove('hidden');
}

export function updateClaimFilter(ctx) {
  if (!ctx.claimFilterSelect || !ctx.staffGridFilterBar) return;
  ctx.claimFilterSelect.value = ctx.currentClaimFilter;
  ctx.claimFilterSelect.classList.remove('hidden');

  if (ctx.similarRequestFilterSelect) {
    ctx.similarRequestFilterSelect.value = ctx.currentSimilarRequestFilter;
    const showSimilar = ctx.currentStatus === 'suggestion';
    ctx.similarRequestFilterSelect.classList.toggle('hidden', !showSimilar);
  }

  if (ctx.closedTypeFilterSelect) {
    const isClosedTab = ctx.currentStatus === 'closed';
    ctx.closedTypeFilterSelect.classList.toggle('hidden', !isClosedTab);
    if (isClosedTab) {
      ctx.closedTypeFilterSelect.value = ctx.currentClosedTypeFilter;
    }
  }

  ctx.staffGridFilterBar.classList.remove('hidden');
}

export function toggleTagFilter(tagName, ctx, onRefresh) {
  const nextTag = ctx.activeTagFilter === tagName ? '' : tagName;
  ctx.setActiveTagFilter(nextTag);
  if (ctx.tagFilterSelect) {
    ctx.tagFilterSelect.value = nextTag;
  }
  return onRefresh();
}
