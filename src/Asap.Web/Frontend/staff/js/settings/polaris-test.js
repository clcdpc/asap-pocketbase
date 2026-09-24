export async function saveThenTestPolaris(save, test) {
  const saved = await save();
  if (!saved) {
    return { saved: false, tested: false };
  }

  const result = await test();
  return { saved: true, tested: true, result };
}
