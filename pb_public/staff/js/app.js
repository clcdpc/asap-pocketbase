import * as state from './state.js';
import { requestedStatusFromUrl } from './api.js';
import { initStaffApp } from './settings.js';
import { install as installUiHelpers } from './ui-helpers.js';

try {
  localStorage.removeItem("pbSettings");
} catch (err) {}

installUiHelpers();

state.setCurrentStatus(requestedStatusFromUrl() || 'suggestion');
initStaffApp();


import { openProfileDialog, closeOpenDialogs, setFieldChecked, setFieldValue } from './api.js';
import { updateRejectionTemplate, removeRejectionTemplate } from './settings-templates.js';

window.openProfileDialog = openProfileDialog;
window.closeOpenDialogs = closeOpenDialogs;
window.setFieldChecked = setFieldChecked;
window.setFieldValue = setFieldValue;
window.updateRejectionTemplate = updateRejectionTemplate;
window.removeRejectionTemplate = removeRejectionTemplate;
window.pb = state.pb;
