import * as state from './state.js';
import { requestedStatusFromUrl } from './api.js';
import { initStaffApp } from './settings.js';
import { install as installUiHelpers } from './ui-helpers.js';
import './actions.js';
import './modals.js';
import './edit-pickup.js';
import './patron.js';

try {
  localStorage.removeItem("pbSettings");
} catch (err) {}

installUiHelpers();

state.setCurrentStatus(requestedStatusFromUrl() || 'suggestion');
initStaffApp();


import { openProfileDialog, setFieldChecked, setFieldValue } from './api.js';
import { closeOpenDialogs } from './dialogs.js';
import { updateRejectionTemplate, removeRejectionTemplate } from './settings-templates.js';

window.closeOpenDialogs = closeOpenDialogs;
window.setFieldChecked = setFieldChecked;
window.setFieldValue = setFieldValue;
window.pb = state.pb;
