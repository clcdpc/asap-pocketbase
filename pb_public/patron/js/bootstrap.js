import { bindAuthEvents } from './auth.js';
import { bindSubmitEvents } from './submit.js';
import { bindFormEvents, applyUiConfig, updateFormatUI } from './form-ui.js';
import { loadInitialConfig } from './config.js';
import { initEmbedMode, postEmbedResize } from './embed.js';

export async function initPatronApp() {
  initEmbedMode();
  bindAuthEvents();
  bindSubmitEvents();
  bindFormEvents();

  updateFormatUI();
  await loadInitialConfig();
  applyUiConfig();
  updateFormatUI();
  postEmbedResize();
}
