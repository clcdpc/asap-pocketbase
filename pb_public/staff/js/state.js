export const defaultPublicationOptions = ['Already published', 'Coming soon', 'Published a while back'];
export const defaultAgeGroups = ['Adult', 'Young Adult / Teen', 'Children'];

export const pb = new window.PocketBase(window.location.origin);
export const SETTINGS_RECORD_ID = 'settings0000001';
export const loginContainer = document.getElementById('login-container');
export const setupContainer = document.getElementById('setup-container');
export const appContainer = document.getElementById('app-container');
export const loginForm = document.getElementById('login-form');
export const setupForm = document.getElementById('setup-form');
export const logoutBtn = document.getElementById('logout-btn');
export const profileBtn = document.getElementById('profile-btn');
export const gridContainer = document.getElementById('grid-container');
export const staffGridFilterBar = document.getElementById('staff-grid-filter-bar');
export const tagFilterSelect = document.getElementById('tag-filter');
export const settingsContainer = document.getElementById('settings-container');
export const settingsForm = document.getElementById('settings-form');
export let grid = undefined;
export function setGrid(newGrid) { grid = newGrid; }

export const formatMap = { book: 'Book', ebook: 'eBook', audiobook_cd: 'Audiobook (Physical CD)', eaudiobook: 'eAudiobook', dvd: 'DVD', music_cd: 'Music CD' };
export let availableFormats = ['book', 'audiobook_cd', 'dvd', 'music_cd', 'ebook', 'eaudiobook'];
export function setAvailableFormats(formats) { availableFormats = formats; }
export const ageMap = { adult: 'Adult', teen: 'Teen', children: 'Children' };
export let currentRejectionTemplates = [];
export function setCurrentRejectionTemplates(templates) { currentRejectionTemplates = templates; }

export const closeReasonMap = { rejected: 'Rejected by staff', hold_completed: 'Hold placed / completed', duplicate_hold: 'Duplicate hold / request', manual: 'Manually closed' };
export const duplicateStatusLabelDefaults = { suggestion: 'Received', outstanding_purchase: 'Under review', pending_hold: 'Being prepared', hold_placed: 'Hold placed', closed: 'Completed', rejected: 'Not selected for purchase', hold_completed: 'Completed', hold_not_picked_up: 'Closed', duplicate_hold: 'Duplicate hold / request', manual: 'Closed', silent: 'Closed', 'Silently Closed': 'Closed' };
export const duplicateStatusLabelFields = [['suggestion','Received suggestion'],['outstanding_purchase','Pending purchase'],['pending_hold','Pending hold'],['hold_placed','Hold placed'],['closed','Closed'],['rejected','Rejected outcome'],['hold_completed','Fulfilled outcome'],['hold_not_picked_up','Hold not picked up'],['duplicate_hold','Duplicate hold / request'],['manual','Manual close'],['silent','Silent close']];
export const patronFormatKeys = ['book', 'audiobook_cd', 'dvd', 'music_cd', 'ebook', 'eaudiobook'];
export const patronFormatFields = [{ key: 'title', label: 'Title (original)', storage: 'title' },{ key: 'author', label: 'Author (original)', storage: 'author' },{ key: 'identifier', label: 'Identifier', storage: 'identifier' },{ key: 'agegroup', label: 'Age group', storage: 'agegroup' },{ key: 'publication', label: 'Publication timing', storage: 'publication' }];
export const defaultPatronFormatRules = {"book":{"messageBehavior":"none","fields":{"title":{"mode":"required","label":"Title"},"author":{"mode":"required","label":"Author"},"identifier":{"mode":"optional","label":"Identifier number"},"agegroup":{"mode":"required","label":"Age group"},"publication":{"mode":"required","label":"Publication timing"}}},"audiobook_cd":{"messageBehavior":"none","fields":{"title":{"mode":"required","label":"Title"},"author":{"mode":"required","label":"Author"},"identifier":{"mode":"optional","label":"Identifier number"},"agegroup":{"mode":"required","label":"Age group"},"publication":{"mode":"required","label":"Publication timing"}}},"dvd":{"messageBehavior":"none","fields":{"title":{"mode":"required","label":"Title"},"author":{"mode":"required","label":"Director/Actors/Producer"},"identifier":{"mode":"hidden","label":"UPC"},"agegroup":{"mode":"required","label":"Age group"},"publication":{"mode":"required","label":"Publication timing"}}},"music_cd":{"messageBehavior":"none","fields":{"title":{"mode":"required","label":"Title"},"author":{"mode":"required","label":"Artist"},"identifier":{"mode":"hidden","label":"UPC"},"agegroup":{"mode":"required","label":"Age group"},"publication":{"mode":"required","label":"Publication timing"}}},"ebook":{"messageBehavior":"ebookMessage","fields":{"title":{"mode":"required","label":"Title"},"author":{"mode":"required","label":"Author"},"identifier":{"mode":"optional","label":"Identifier number"},"agegroup":{"mode":"required","label":"Age group"},"publication":{"mode":"required","label":"Publication timing"}}},"eaudiobook":{"messageBehavior":"eaudiobookMessage","fields":{"title":{"mode":"required","label":"Title"},"author":{"mode":"required","label":"Author"},"identifier":{"mode":"optional","label":"Identifier number"},"agegroup":{"mode":"required","label":"Age group"},"publication":{"mode":"required","label":"Publication timing"}}}};
export const descriptions = {"suggestion":"Suggestions submitted by patrons and awaiting staff review. Review each suggestion and choose Purchase, Already own, Reject, or Silent close.","outstanding_purchase":"Pending purchase contains approved suggestions that are waiting to appear in Polaris. If the auto-promoter is enabled, ASAP will check Polaris automatically. Staff can also add a BIB ID manually.","pending_hold":"Pending hold contains suggestions with catalog matches that are waiting for hold placement. ASAP can place holds automatically during the hold check. Staff should confirm BIB IDs and resolve skipped items.","hold_placed":"Hold placed contains active holds ASAP is tracking for checkout or pickup completion. Staff should review items that do not close automatically.","closed":"Closed suggestions include rejected, silent-closed, fulfilled, or auto-closed suggestions. Use this tab to review outcomes or undo a closure when needed.","settings":"Settings control staff access, patron experience, workflow automation, Polaris, SMTP, and email templates."};
export const emptyStateMessages = {"suggestion":"No new suggestions need review.","outstanding_purchase":"No approved suggestions are waiting for catalog matches.","pending_hold":"No items are waiting for hold placement.","hold_placed":"No active holds are being tracked.","closed":"No closed suggestions found."};
export const statusStages = ['suggestion', 'outstanding_purchase', 'pending_hold', 'hold_placed', 'closed', 'settings'];
export const stageQueryMap = {"submitted":"suggestion","suggestion":"suggestion","new":"suggestion","purchased_waiting_for_bib":"outstanding_purchase","outstanding_purchase":"outstanding_purchase","pending_hold":"pending_hold","hold_placed":"hold_placed","closed":"closed","settings":"settings"};

export let currentStatus = 'suggestion';
export function setCurrentStatus(status) { currentStatus = status; }

export let currentSuggestions = [];
export function setCurrentSuggestions(suggestions) { currentSuggestions = suggestions; }

export let activeTagFilter = '';
export function setActiveTagFilter(filter) { activeTagFilter = filter; }

export let allSuggestions = [];
export function setAllSuggestions(suggestions) { allSuggestions = suggestions; }

export let verifiedNewSuggestionBarcode = '';
export function setVerifiedNewSuggestionBarcode(barcode) { verifiedNewSuggestionBarcode = barcode; }

export let verifiedBibId = '';
export function setVerifiedBibId(id) { verifiedBibId = id; }

export let publicationOptions = defaultPublicationOptions.slice();
export function setPublicationOptions(opts) { publicationOptions = opts; }

export let workflowSettings = { autoPromote: false, outstandingTimeoutEnabled: false, outstandingTimeoutDays: 30, outstandingTimeoutSendEmail: false, outstandingTimeoutRejectionTemplateId: '', holdPickupTimeoutEnabled: false, holdPickupTimeoutDays: 14, pendingHoldTimeoutEnabled: false, pendingHoldTimeoutDays: 14 };
export function setWorkflowSettings(settings) { workflowSettings = settings; }

export let currentLibraryContextOrgId = 'system';
export function setCurrentLibraryContextOrgId(id) { currentLibraryContextOrgId = id; }

export let lastSavedLibrarySettingsSnapshot = null;
export function setLastSavedLibrarySettingsSnapshot(snapshot) { lastSavedLibrarySettingsSnapshot = snapshot; }

export let lastSavedLibrarySettingsOrgId = 'system';
export function setLastSavedLibrarySettingsOrgId(id) { lastSavedLibrarySettingsOrgId = id; }

export let libraryTemplateOverrides = {};
export function setLibraryTemplateOverrides(overrides) { libraryTemplateOverrides = overrides; }

export let libraryContextLoadSerial = 0;
export function incrementLibraryContextLoadSerial() { libraryContextLoadSerial++; }

export let librarySelectorBound = false;
export function setLibrarySelectorBound(bound) { librarySelectorBound = bound; }

export let bootstrapAdminMessage = '';
export function setBootstrapAdminMessage(msg) { bootstrapAdminMessage = msg; }

export let setupRequired = false;
export function setSetupRequired(req) { setupRequired = req; }

export let canAssignSuperAdmin = false;
export function setCanAssignSuperAdmin(canAssign) { canAssignSuperAdmin = canAssign; }

export let currentEmailStatus = { enabled: true };
export function setCurrentEmailStatus(status) { currentEmailStatus = status; }

export let organizationsStatus = 'not_loaded';
export function setOrganizationsStatus(status) { organizationsStatus = status; }

export let organizationsStatusMessage = 'Polaris organizations have not been loaded yet. Organization selection will be available after the Polaris organization sync completes.';
export function setOrganizationsStatusMessage(msg) { organizationsStatusMessage = msg; }

export const settingsSectionIds = ['start', 'polaris', 'staff', 'smtp', 'workflow', 'patron', 'templates'];
export let currentSettingsSection = 'start';
export function setCurrentSettingsSection(section) { currentSettingsSection = section; }

export let settingsDirty = false;
export function setSettingsDirty(dirty) { settingsDirty = dirty; }

export let settingsSaving = false;
export function setSettingsSaving(saving) { settingsSaving = saving; }

export let settingsLoading = false;
export function setSettingsLoading(loading) { settingsLoading = loading; }

export let activeActionMenu = null;
export function setActiveActionMenu(menu) { activeActionMenu = menu; }

export let rowActionIdCounter = 0;
export function incrementRowActionIdCounter() { rowActionIdCounter++; return rowActionIdCounter; }

export let rowActionRegistry = new Map();

export let leapBibUrlPattern = '';
export function setLeapBibUrlPattern(pattern) { leapBibUrlPattern = pattern; }

export let lastWorkflowEnabledList = [];
export function setLastWorkflowEnabledList(list) { lastWorkflowEnabledList = list; }

export const templateFieldIds = [
  'email-submit-subject',
  'email-submit-body',
  'email-owned-subject',
  'email-owned-body',
  'email-rejected-subject',
  'email-rejected-body',
  'email-hold-subject',
  'email-hold-body'
];

export const emailTemplateDefaults = {
  suggestion_submitted: {
    subject: 'Suggestion received: {{title}}',
    body: 'Hello {{name}},\n\nThank you for suggesting {{title}} by {{author}} in {{format}} format. Our collection development team has received your request and will review it.\n\nIf we add this item, we will place a hold for you automatically and send another update.\n\nThank you for helping us shape the library collection.'
  },
  already_owned: {
    subject: '{{title}} is already available',
    body: 'Hello {{name}},\n\nThank you for suggesting {{title}} by {{author}} in {{format}} format.\n\nThe library already owns this title or has it on order. We have placed a hold on card {{barcode}} so you will be notified when it is ready.\n\nThank you for using the library\'s suggestion service.'
  },
  rejected: {
    subject: 'Update on your suggestion: {{title}}',
    body: 'Hello {{name}},\n\nThank you for suggesting {{title}} by {{author}} in {{format}} format.\n\nAfter review, we are not able to add this item to the collection at this time. We appreciate you taking the time to share your suggestion with us.\n\nThank you for helping us build a collection that reflects our community.'
  },
  hold_placed: {
    subject: 'Hold placed: {{title}}',
    body: 'Hello {{name}},\n\nGood news! We have decided to purchase {{title}} by {{author}} and have placed a hold on it for you (card {{barcode}}). You will receive another notification when it is ready to be picked up.\n\nThank you for using the library\'s suggestion service.'
  }
};
