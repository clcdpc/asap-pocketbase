## Settings scope: system vs library
One of the easiest ways to introduce bugs in this project is to add a new setting without handling its scope correctly.

## PocketBase route and hook refactors
The May 2026 route-registry refactor broke production by replacing literal `routerAdd(...)` calls in `pb_hooks/main.pb.js` with a dynamic registry helper. In PocketBase's JS hook runtime, those registered callbacks failed before reaching the real handlers with `ReferenceError: method is not defined`, which made public boot endpoints such as `/api/asap/setup/status` and `/api/asap/config` return generic 400 responses.

Do not reintroduce a dynamic route registry for PocketBase hooks unless it has been proven in the actual PocketBase runtime, not only Node tests.

### Route refactor rules
- Keep `pb_hooks/main.pb.js` routes as explicit `routerAdd(method, path, (e) => { return require(...).handler(e); })` registrations.
- Do not replace literal route registrations with loop-generated callbacks, closure-based registries, or generic dispatch wrappers unless a PocketBase runtime smoke test covers the changed routes.
- Do not rely on Node-only tests for hook entrypoint behavior. PocketBase's embedded JS runtime can differ from Node in callback and closure behavior.
- After changing `pb_hooks/main.pb.js`, start PocketBase with the project hooks and migrations directories and verify the affected routes with real HTTP requests.
- At minimum, smoke test `GET /api/asap/setup/status` and `GET /api/asap/config` after route or boot-handler changes.
- If a public boot endpoint returns `{"message":"Something went wrong while processing your request.","status":400}`, check PocketBase server logs first; the browser response may hide the real hook exception.

### Query and app-context rules
- Use `routeUtils.queryValue(e, "name")` instead of `e.request.url.query().get("name")` in hook route code.
- Pass `e.app` into config helpers from route handlers, for example `config.getSettings(e.app)` and `config.polaris(e.app)`.
- Avoid adding new route-time dependencies on implicit global `$app` when the handler already receives `e.app`.

### PocketBase data access rules
Do not write or execute raw SQL for application data access, schema updates, migrations, tests, or repair scripts unless the user explicitly asks for a one-off forensic SQL query.

Use PocketBase APIs instead:
- `app.findRecordById`
- `app.findFirstRecordByFilter`
- `app.findRecordsByFilter`
- `app.save`
- `app.delete`
- collection field APIs in migrations
- existing project helpers that wrap PocketBase records

Do not use raw SQL strings through database handles, query builders, shell commands, SQLite CLIs, or ad hoc scripts to read or mutate project data. Raw SQL bypasses PocketBase record rules, relation handling, hook behavior, JSON normalization expectations, and migration conventions.

If a task seems to require SQL, stop and first translate it into PocketBase record/collection API calls. If that is not possible, document why and ask before proceeding.

Before implementing any new setting, decide and document which of these models it uses:
- system-only: one value for the whole installation
- library-only: each library has its own value
- system default with library override: the system value is the default, but a library can override it

Do not add a setting until its scope is clear.

### Required implementation rule
For any new setting, the load path, form population path, save path, and read/use path must all agree on the same scope model.

A setting is not complete unless all four are handled:
1. where the default value comes from
2. where a library-specific override comes from
3. where the value is saved
4. how runtime code resolves the effective value

### Preferred model
When possible, implement scoped settings using explicit fallback behavior:
- read library override first
- fall back to system value when no override exists
- save to the correct scope intentionally, never implicitly

### Do not do this
- Do not assume every new field belongs only at the system level.
- Do not save based only on what screen the user is on.
- Do not load from one scope and save to another.
- Do not add a field to the form without adding matching load/save/fallback logic.
- Do not treat “missing override” and “blank override” as the same thing unless that is explicitly intended.

### Notes, comments, and audit history
Workflow notes must distinguish clearly between:
- existing committed history
- user draft comments
- pending system-generated audit text
- Do not show past-tense audit entries in editable notes fields before an action succeeds.
If preview text is needed, show it separately and label it as pending.
Only append system audit/history entries after successful completion of the action.

### Implementation guidance for agents
When adding a new setting:
- identify its scope in the PR description or code comment
- add it to the system-level loader if it has a system default
- add it to the library-level loader if it supports overrides
- make sure the save handler writes to the currently selected scope intentionally
- make sure the runtime reader uses the same fallback logic as the editor UI
- verify that switching between system and library views preserves the expected value

### System-only settings
System-only settings live only at the system level. They are not library defaults, and a library context must not be able to edit or save them.

When presenting system-only settings from a library context:
- use "system level" language, not "system defaults"
- route any "switch to system level" action through the same context-switch path as the library selector
- warn about unsaved library changes before switching away from the library context
- keep system-only controls disabled while in library context, except for the explicit switch-to-system-level action
- never populate or save system-only fields from blank or disabled library-context controls
- verify library-context saves do not include system-only payload keys, while system-context saves still do

### Global records with scoped fields
Not every settings-adjacent screen is a library override setting. Staff access manages global `staff_users` records whose role and library fields determine scope; it does not save a library settings override.

When a screen manages global records with scoped fields:
- keep library context controls only when they filter or preselect record scope
- do not show "using system defaults" or "saving will create a library-specific override" messaging
- keep create/update/delete APIs scoped by authorization and record fields, not by settings override state
- add UI regression coverage near `tests/settings_staff_scope_banner.test.js` when changing settings navigation or scope banners

### Patron public option settings
Publication timing is a system default with library overrides.

Important paths:
- system defaults live in `ui_settings`
- library public-form overrides live in `patron_settings_overrides`

When resolving public-form options for a library, do not use a library `ui_settings` row as the default source. A library `ui_settings` row can exist only for branding or text overrides and may have blank option fields. Resolve option defaults from the system `ui_settings` row, then apply `patron_settings_overrides` for the selected library.

PocketBase JSON fields may come back through hooks as byte arrays. Never pass raw JSON-field arrays directly to the settings editor or public form. Decode byte-array JSON, parse it, and normalize it into `{ id, label, enabled, sortOrder }` objects before rendering. If publication timing labels ever display as numbers such as `91`, `123`, or `34`, treat that as a failed normalization bug.

When repopulating selects after a library/settings-context change, do not preserve invalid stale values for new suggestion fields. Preserving an old value is allowed only for edit flows where the existing record may contain a historical value.

Keep regression coverage for this behavior near `tests/config_ui_text_patron_options_scope.test.js` and `tests/staff_public_option_selects.test.js`.

### Frontend DOM safety
Do not use `innerHTML` for new UI code.

Default to safe DOM APIs:
- create elements with `document.createElement`
- set text with `textContent`
- set attributes with `setAttribute`
- append children with `append`, `appendChild`, or `replaceChildren`
- use `classList` for classes

Do not inject patron, staff, Polaris, API, settings, or other runtime data into HTML strings.

If markup must be generated dynamically, build it with DOM nodes instead of string concatenation. If an existing code path already uses `innerHTML`, prefer refactoring it to DOM construction when touching that area.

Only use `innerHTML` when all of the following are true:
- the content is static developer-authored markup, or it has gone through a project-approved sanitizer
- no user/API/runtime data is interpolated into the string
- the reason is documented in a nearby code comment
- tests or manual verification cover the rendered path

Never treat escaping helpers as permission to build arbitrary HTML strings. Escaping reduces risk, but DOM construction is the preferred pattern.

### Analytics scope
All analytics must respect library scope.

Rules:
- Library users see only their own library’s analytics.
- Super admin may view a specific library or an all-libraries global view.
- Scope must be applied in the query/data layer, not just implied in the UI.
- Every new metric must define its scope behavior before implementation.

### Frontend request and refresh architecture
Use the frontend request helpers and load guards consistently. Do not introduce one-off fetch patterns in staff or patron code when the existing helpers cover the behavior.

- Prefer shared request helpers for JSON API calls over raw `fetch`.
- Keep cross-app behavior in the shared request core limited to transport concerns: JSON parsing, headers, cache mode, abort signals, and normalized errors.
- Keep auth/session policy in app-specific wrappers. Staff auth stays in `pb_public/staff/js/http.js`; patron session-expiry behavior stays in `pb_public/patron/js/api.js`.
- Explicitly protect screen-level staff loads with abort-plus-stale-result guards.
- Protect screen-level staff loads with abort-plus-stale-result guards so fast tab, analytics, or library-context switches cannot render stale data.
- Route mutation follow-up reloads through explicit refresh helpers instead of scattering direct `loadTab(currentStatus)` calls through action modules.
- When identifying a staff load, include the relevant scope inputs in the request path and guard logic: current status, workflow scope, analytics scope/range, selected settings library context, and current auth context where relevant.
- Do not add TanStack Query or another frontend cache layer unless the frontend architecture changes materially.
- Add regression coverage when changing request helpers or guarded load paths, especially for grid, settings, and analytics behavior.

### Manual verification checklist for new settings
For each new setting, verify all of the following:
- save at system level persists correctly
- save at library level persists correctly
- library value overrides system value only where intended
- removing or disabling an override falls back to the system value correctly
- switching between libraries does not leak values from another library
- reopening Settings shows the correct value for the selected scope

### Good design pattern
Use one clear source of truth for the selected settings scope. If the UI tracks a current org/system selection, all load and save helpers should consume that same state instead of re-deriving scope ad hoc in multiple places.
