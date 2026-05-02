import { env, exposeLegacyGlobals } from './state.js';
import { install as installCore } from './api.js';
import { install as installGrid } from './grid.js';
import { install as installModals } from './modals.js';
import { install as installSettingsUi } from './settings-ui.js';
import { install as installSettingsPolaris } from './settings-polaris.js';
import { install as installSettingsLabels } from './settings-labels.js';
import { install as installSettingsFormats } from './settings-formats.js';
import { install as installSettingsTemplates } from './settings-templates.js';
import { install as installSettingsUsers } from './settings-users.js';
import { install as installSettings } from './settings.js';
import { install as installActions } from './actions.js';
import { install as installPatron } from './patron.js';
import { install as installUiHelpers } from './ui-helpers.js';

try {
  localStorage.removeItem("pbSettings");
} catch (err) {}

installUiHelpers(env);
installCore(env);
installGrid(env);
installModals(env);
installActions(env);
installPatron(env);
installSettingsUi(env);
installSettingsPolaris(env);
installSettingsLabels(env);
installSettingsFormats(env);
installSettingsTemplates(env);
installSettingsUsers(env);
installSettings(env);

env.currentStatus = env.requestedStatusFromUrl() || 'suggestion';
exposeLegacyGlobals();
env.initStaffApp();
