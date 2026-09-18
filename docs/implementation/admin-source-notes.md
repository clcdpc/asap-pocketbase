# Administration Source Notes

Preparation evidence for Slice 4, not implementation or an acceptance gate.
Source pin: `150b30b776565194260cc327eeeffdfb46475e81`. These observations
come from exact `git show` source reads. The normative settings inventory
controls intentional corrections; do not recreate legacy storage defects.

## Save And Scope Paths

- `pb_public/staff/js/settings/serialize-save.js` collects
  `formatIconUrlPattern` only in system context, and `form-population.js`
  populates the system control. `save-controller.js` constructs a second
  outgoing DTO but omits this property. `lib/staff/settings_save.js` and
  `lib/config/settings.js` already support saving it. This is the concrete
  save-path gap: fix the target's complete load/populate/save/use path, and
  migrate actual persisted/effective data rather than an unsaved form value.
- The serializer includes `misconfiguredMessage` only in the system UI-text
  payload; the legacy settings-UI saver does not consistently persist it.
  Both this message and `systemNotEnabledMessage` belong to SystemSettings,
  not nullable library PatronSettings overrides. Both require working target
  save/reload/runtime tests.
- `settings_routes.js` restricts system settings to super-admin and library
  settings to the owning admin/super-admin. The combined DTO is a frontend
  contract, not a reason to combine SQL domains. Its broad override summary
  includes staff and library-owned records; target banners must distinguish
  inherited settings from global staff administration and library-owned data.
- `settings_save.js` preserves explicitly supplied false workflow booleans.
  Target updates must distinguish missing fields from false, zero where legal,
  and explicit reset. Clearing one override cannot erase unrelated fields.
- `library-context.js` uses the common context switch, unsaved-change prompt,
  saved super-admin selection and latest-load/request/context guards. Preserve
  this behavior when replacing PocketBase collection calls. System-level
  shortcuts must use the same switch, not mutate context independently.

## Explicit Target Corrections

- Legacy `settings_save.js` and `settings_email.js` reset library workflow,
  UI, templates, formats, patron/custom-field records and auto-claim rules.
  Do not copy that destructive reset. Target Reset inherited overrides
  removes only inventory section 6's inherited domains and preserves all
  owned custom fields/options/rules, custom formats/rejections, auto-claim
  rules/history and business records.
- Legacy auto-claim save mutates/deletes rule rows. Target rules are versioned:
  deactivate the old row and insert a new version, preserving history and
  revalidating current eligible assignee under StaffUser serialization.
- Legacy empty enabled-library list means all participating libraries.
  Migration resolves that source behavior explicitly; target discovery adds
  inactive organizations and participation is explicit Organization.IsActive.
- Legacy UI code overlaps material-format columns and patronFormatRules JSON.
  Migration computes pinned effective behavior before consolidating it into
  typed built-in columns and relational custom-field rules. No competing JSON
  authority remains. System formats retain durable identity and are disabled,
  not deleted. Preserve stable custom format/option codes and ordering.

## Templates And Branding

- `settings_email.js` handles five built-in keys: suggestion_submitted,
  purchase_approved, already_owned, rejected and hold_placed. Blank subject
  plus body at library scope removes the override. Target content and lineage
  stay in EmailTemplate; sender identity moves to field-inheritable
  EmailSettings rather than being repeated across content rows.
- Rejection templates support system identity, library override/hide and
  library-owned custom identity. Legacy equal-to-source content removes a
  redundant override, and omitted inherited templates become hidden. Preserve
  meaningful behavior and prevent deletion/hiding of a template currently
  selected by the effective auto-rejection configuration.
- `settings_logo_routes.js` authorizes own-library admin or super-admin
  upload, plus super-admin system upload. Legacy reset clears image and alt
  together. Target individual resets keep image and alt inheritance independent;
  a full inherited reset removes both overrides without touching owned data.
- `form-population.js` clears file inputs on reload, shows current preview and
  inheritance status, and builds patron/embed links for selected library.
  Adapt this existing UX and safe DOM updates; secret controls must never be
  populated with plaintext, even where the legacy Polaris form did so.

## Runtime And Migration

`lib/config/settings.js` resolves a persisted nonblank staff URL first,
then ASAP_STAFF_URL, then ASAP_PUBLIC_URL, then localhost. The distinct
initialization helper's ASAP_BASE_URL fallback must also be preserved in
the frozen runtime artifact; do not substitute a new universal precedence.
System format-icon patterns pass through the existing normalizer on read/save.

Migration must map each populated source setting through inventory 13,
including hidden-but-supported eBook messages, independent branding bytes/alt,
whole-set absence versus meaningful replacement, provider slots and custom
field modes. Report the deliberate partial-library UI inheritance correction.
Legacy SMTP secrets are intentional drops, never Postmark credentials. The
temporary file sender changes none of these storage or reconciliation rules.

Before implementation, inspect referenced callers and regression tests as
well as the pinned workflow/UI/email/format/custom-field resolvers. Staff
lifecycle source anchors and durable target identity rules are in
`staff-source-notes.md` and the completed Slice 2/3 code, not this summary.
