export async function deleteSettingsFormatsSequentially(formats, deleteFormat, onDeleted, canContinue = () => true) {
  for (const format of formats) {
    if (!canContinue()) {
      return;
    }
    await deleteFormat(format);
    onDeleted(format);
  }
}
