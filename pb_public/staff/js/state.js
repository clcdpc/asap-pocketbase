export const defaultPublicationOptions = ['Already published', 'Coming soon', 'Published a while back'];
export const defaultAgeGroups = ['Adult', 'Young Adult / Teen', 'Children'];

const values = {
  pb: new window.PocketBase(window.location.origin),
  SETTINGS_RECORD_ID: 'settings0000001',
  loginContainer: document.getElementById('login-container'),
  setupContainer: document.getElementById('setup-container'),
  appContainer: document.getElementById('app-container'),
  loginForm: document.getElementById('login-form'),
  setupForm: document.getElementById('setup-form'),
  logoutBtn: document.getElementById('logout-btn'),
  profileBtn: document.getElementById('profile-btn'),
  gridContainer: document.getElementById('grid-container'),
  staffGridFilterBar: document.getElementById('staff-grid-filter-bar'),
  tagFilterSelect: document.getElementById('tag-filter'),
  settingsContainer: document.getElementById('settings-container'),
  settingsForm: document.getElementById('settings-form'),
  grid: undefined,
  formatMap: { book: 'Book', ebook: 'eBook', audiobook_cd: 'Audiobook (Physical CD)', eaudiobook: 'eAudiobook', dvd: 'DVD', music_cd: 'Music CD' },
  availableFormats: ['book', 'audiobook_cd', 'dvd', 'music_cd', 'ebook', 'eaudiobook'],
  ageMap: { adult: 'Adult', teen: 'Teen', children: 'Children' },
  currentRejectionTemplates: [],
  closeReasonMap: { rejected: 'Rejected by staff', hold_completed: 'Hold placed / completed', duplicate_hold: 'Duplicate hold / request', manual: 'Manually closed' },
  defaultPublicationOptions,
  defaultAgeGroups,
  duplicateStatusLabelDefaults: { suggestion: 'Received', outstanding_purchase: 'Under review', pending_hold: 'Being prepared', hold_placed: 'Hold placed', closed: 'Completed', rejected: 'Not selected for purchase', hold_completed: 'Completed', hold_not_picked_up: 'Closed', duplicate_hold: 'Duplicate hold / request', manual: 'Closed', silent: 'Closed', 'Silently Closed': 'Closed' },
  duplicateStatusLabelFields: [['suggestion','Received suggestion'],['outstanding_purchase','Pending purchase'],['pending_hold','Pending hold'],['hold_placed','Hold placed'],['closed','Closed'],['rejected','Rejected outcome'],['hold_completed','Fulfilled outcome'],['hold_not_picked_up','Hold not picked up'],['duplicate_hold','Duplicate hold / request'],['manual','Manual close'],['silent','Silent close']],
  patronFormatKeys: ['book', 'audiobook_cd', 'dvd', 'music_cd', 'ebook', 'eaudiobook'],
  patronFormatFields: [{ key: 'title', label: 'Title (original)', storage: 'title' },{ key: 'author', label: 'Author (original)', storage: 'author' },{ key: 'identifier', label: 'Identifier', storage: 'identifier' },{ key: 'agegroup', label: 'Age group', storage: 'agegroup' },{ key: 'publication', label: 'Publication timing', storage: 'publication' }],
  defaultPatronFormatRules: {"book":{"messageBehavior":"none","fields":{"title":{"mode":"required","label":"Title"},"author":{"mode":"required","label":"Author"},"identifier":{"mode":"optional","label":"Identifier number"},"agegroup":{"mode":"required","label":"Age group"},"publication":{"mode":"required","label":"Publication timing"}}},"audiobook_cd":{"messageBehavior":"none","fields":{"title":{"mode":"required","label":"Title"},"author":{"mode":"required","label":"Author"},"identifier":{"mode":"optional","label":"Identifier number"},"agegroup":{"mode":"required","label":"Age group"},"publication":{"mode":"required","label":"Publication timing"}}},"dvd":{"messageBehavior":"none","fields":{"title":{"mode":"required","label":"Title"},"author":{"mode":"required","label":"Director/Actors/Producer"},"identifier":{"mode":"hidden","label":"UPC"},"agegroup":{"mode":"required","label":"Age group"},"publication":{"mode":"required","label":"Publication timing"}}},"music_cd":{"messageBehavior":"none","fields":{"title":{"mode":"required","label":"Title"},"author":{"mode":"required","label":"Artist"},"identifier":{"mode":"hidden","label":"UPC"},"agegroup":{"mode":"required","label":"Age group"},"publication":{"mode":"required","label":"Publication timing"}}},"ebook":{"messageBehavior":"ebookMessage","fields":{"title":{"mode":"required","label":"Title"},"author":{"mode":"required","label":"Author"},"identifier":{"mode":"optional","label":"Identifier number"},"agegroup":{"mode":"required","label":"Age group"},"publication":{"mode":"required","label":"Publication timing"}}},"eaudiobook":{"messageBehavior":"eaudiobookMessage","fields":{"title":{"mode":"required","label":"Title"},"author":{"mode":"required","label":"Author"},"identifier":{"mode":"optional","label":"Identifier number"},"agegroup":{"mode":"required","label":"Age group"},"publication":{"mode":"required","label":"Publication timing"}}}},
  descriptions: {"suggestion":"Suggestions submitted by patrons and awaiting staff review. Review each suggestion and choose Purchase, Already own, Reject, or Silent close.","outstanding_purchase":"Pending purchase contains approved suggestions that are waiting to appear in Polaris. If the auto-promoter is enabled, ASAP will check Polaris automatically. Staff can also add a BIB ID manually.","pending_hold":"Pending hold contains suggestions with catalog matches that are waiting for hold placement. ASAP can place holds automatically during the hold check. Staff should confirm BIB IDs and resolve skipped items.","hold_placed":"Hold placed contains active holds ASAP is tracking for checkout or pickup completion. Staff should review items that do not close automatically.","closed":"Closed suggestions include rejected, silent-closed, fulfilled, or auto-closed suggestions. Use this tab to review outcomes or undo a closure when needed.","settings":"Settings control staff access, patron experience, workflow automation, Polaris, SMTP, and email templates."},
  emptyStateMessages: {"suggestion":"No new suggestions need review.","outstanding_purchase":"No approved suggestions are waiting for catalog matches.","pending_hold":"No items are waiting for hold placement.","hold_placed":"No active holds are being tracked.","closed":"No closed suggestions found."},
  statusStages: ['suggestion', 'outstanding_purchase', 'pending_hold', 'hold_placed', 'closed', 'settings'],
  stageQueryMap: {"submitted":"suggestion","suggestion":"suggestion","new":"suggestion","purchased_waiting_for_bib":"outstanding_purchase","outstanding_purchase":"outstanding_purchase","pending_hold":"pending_hold","hold_placed":"hold_placed","closed":"closed","settings":"settings"},
  currentStatus: 'suggestion',
  currentSuggestions: [],
  activeTagFilter: '',
  allSuggestions: [],
  verifiedNewSuggestionBarcode: '',
  verifiedBibId: '',
  publicationOptions: defaultPublicationOptions.slice(),
  workflowSettings: { autoPromote: false, outstandingTimeoutEnabled: false, outstandingTimeoutDays: 30, outstandingTimeoutSendEmail: false, outstandingTimeoutRejectionTemplateId: '', holdPickupTimeoutEnabled: false, holdPickupTimeoutDays: 14, pendingHoldTimeoutEnabled: false, pendingHoldTimeoutDays: 14 },
  currentLibraryContextOrgId: 'system',
  libraryTemplateOverrides: {},
  libraryContextLoadSerial: 0,
  librarySelectorBound: false,
  bootstrapAdminMessage: '',
  setupRequired: false,
  canAssignSuperAdmin: false,
  currentEmailStatus: { enabled: true },
  organizationsStatus: 'not_loaded',
  organizationsStatusMessage: 'Polaris organizations have not been loaded yet. Organization selection will be available after the Polaris organization sync completes.',
  settingsSectionIds: ['start', 'polaris', 'staff', 'smtp', 'workflow', 'patron', 'templates'],
  currentSettingsSection: 'start',
  settingsDirty: false,
  settingsSaving: false,
  settingsLoading: false,
  activeActionMenu: null,
  rowActionIdCounter: 0,
  rowActionRegistry: new Map(),
  leapBibUrlPattern: '',
  lastWorkflowEnabledList: []
};

export const env = new Proxy(values, {
  has() { return true; },
  get(target, prop) {
    if (prop === Symbol.unscopables) return undefined;
    if (prop in target) return target[prop];
    const value = window[prop];
    if (typeof value === 'function') {
      // Bind functions to window unless they appear to be constructors.
      // Constructors typically have a prototype with a constructor link back to themselves.
      // We also check the first letter as a common convention (e.g., Date vs fetch).
      const isConstructor = value.prototype && value.prototype.constructor === value;
      const startsWithUpper = prop[0] === prop[0].toUpperCase() && prop[0] !== prop[0].toLowerCase();
      if (!isConstructor && !startsWithUpper) {
        return value.bind(window);
      }
    }
    return value;
  },
  set(target, prop, value) {
    target[prop] = value;
    return true;
  }
});

export function exposeLegacyGlobals() {
  ['updateRejectionTemplate', 'removeRejectionTemplate'].forEach(name => {
    if (typeof values[name] === 'function') window[name] = values[name];
  });
}
