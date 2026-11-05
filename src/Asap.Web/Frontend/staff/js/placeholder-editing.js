// insertText uses the browser's editing transaction, so placeholder edits join
// ordinary typing in native Undo/Redo. setRangeText alone has no undo entry.
export function insertPlaceholderText(control, token, start, end, scrollPosition = control) {
  const document = control.ownerDocument, view = document.defaultView;
  const before = control.value;
  const expected = `${before.slice(0, start)}${token}${before.slice(end)}`;
  const { scrollTop, scrollLeft } = scrollPosition;
  const pageX = view.scrollX, pageY = view.scrollY;
  control.focus({ preventScroll: true });
  control.setSelectionRange(start, end);
  // A single-line editor may reset its horizontal view on blur. Begin its
  // native transaction in the remembered view.
  const restoreInputView = scrollPosition !== control && control.localName === 'input';
  if (restoreInputView) control.scrollLeft = scrollLeft;
  let inputObserved = false;
  const observeInput = () => { inputObserved = true; };
  control.addEventListener('input', observeInput);
  try {
    if (typeof document.execCommand === 'function') document.execCommand('insertText', false, token);
  } catch {
    // Non-native editors may only support the selection-based fallback below.
  } finally {
    control.removeEventListener('input', observeInput);
  }
  if (control.value !== expected) {
    control.value = before;
    control.setRangeText(token, start, end, 'end');
    control.dispatchEvent(new view.Event('input', { bubbles: true }));
  } else {
    if (!inputObserved) control.dispatchEvent(new view.Event('input', { bubbles: true }));
    control.setSelectionRange(start + token.length, start + token.length);
  }
  control.scrollTop = scrollTop;
  control.scrollLeft = scrollLeft;
  if (restoreInputView && control.clientWidth > 0) {
    // Restore the prior view with normal browser clamping. Programmatic native
    // insertion does not always reveal an extended end caret, so move only
    // enough horizontally to keep that caret inside this single-line editor.
    const style = view.getComputedStyle(control), measure = document.createElement('canvas').getContext('2d');
    measure.font = `${style.fontStyle} ${style.fontWeight} ${style.fontSize} ${style.fontFamily}`;
    const caret = measure.measureText(control.value.slice(0, start + token.length)).width;
    const width = control.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight);
    if (caret < control.scrollLeft) control.scrollLeft = caret;
    else if (caret > control.scrollLeft + width) control.scrollLeft = caret - width;
  }
  if (view.scrollX !== pageX || view.scrollY !== pageY) view.scrollTo({ left: pageX, top: pageY, behavior: 'instant' });
}
