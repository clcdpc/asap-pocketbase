import { bindAuthEvents, captureAuthOperation, restoreSession } from './auth.js';
import { bindSubmitEvents } from './submit.js';
import { bindFormEvents, applyUiConfig, updateFormatUI } from './form-ui.js';
import { applyLoadedUiText, captureUiConfigVersion, loadInitialConfig } from './config.js';
import { initEmbedMode, postEmbedResize } from './embed.js';

export async function initPatronApp() {
  initEmbedMode();
  bindAuthEvents();
  bindSubmitEvents();
  bindFormEvents();

  updateFormatUI();
  const startupAuthOperation = captureAuthOperation();
  const startupUiConfigVersion = captureUiConfigVersion();
  const initialConfig = await loadInitialConfig();
  if (initialConfig && captureUiConfigVersion() === startupUiConfigVersion) {
    applyLoadedUiText(initialConfig);
    applyUiConfig();
    updateFormatUI();
  }
  await restoreSession(startupAuthOperation);
  postEmbedResize();
}
