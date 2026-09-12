## Simplicity and scope

Maintain the PocketBase application correctly until the planned C#/.NET port. Specific rules for known runtime failures, security boundaries, data scope, and application invariants take precedence over these defaults.

* Implement the smallest straightforward solution that satisfies the actual requirement and is easy to understand, maintain, and replace.
* Add abstractions only for a concrete present benefit; add extra representations of state only when required for correctness.
* Avoid generalized infrastructure, speculative extensibility, and compatibility or defensive machinery for scenarios the application does not currently support.
* Keep changes narrowly scoped. Do not expand a focused task into unrelated cleanup merely because nearby code could be improved.
* Reconsider existing complexity before extending it; simplify or remove it when doing so serves the requested change.
* Avoid heavy investment in PocketBase-specific abstractions whose value depends on a hypothetical future architecture.
* Test observable behavior and real failure modes, preserving known regression coverage. Avoid exhaustive tests of internal machinery without a concrete behavioral reason.

## Settings scope: system vs library

One of the easiest ways to introduce bugs in this project is to add a new setting without handling its scope correctly.

## PocketBase route and hook refactors

The May 2026 route-registry refactor broke production by replacing literal `routerAdd(...)` calls in `pb_hooks/main.pb.js` with a dynamic registry helper. In PocketBase's JS hook runtime, those registered callbacks failed before reaching the real handlers with `ReferenceError: method is not defined`, which made public boot endpoints such as `/api/asap/setup/status` and `/api/asap/config` return generic 400 responses.

Do not reintroduce a dynamic route registry for PocketBase hooks unless it has been proven in the actual PocketBase runtime, not only Node tests.

### Route refactor rules

* Keep `pb_hooks/main.pb.js` routes as explicit `routerAdd(method, path, (e) => { return require(...).handler(e); })` registrations.
* Do not replace literal route registrations with loop-generated callbacks, closure-based registries, or generic dispatch wrappers unless a PocketBase runtime smoke test covers the changed routes.
* Do not rely on Node-only tests for hook entrypoint behavior. PocketBase's embedded JS runtime can differ from Node in callback and closure behavior.
* After changing `pb_hooks/main.pb.js`, start PocketBase with the project hooks and migrations directories and verify the affected routes with real HTTP requests.
* At minimum, smoke test `GET /api/asap/setup/status` and `GET /api/asap/config` after route or boot-handler changes.
* If a public boot endpoint returns `{"message":"Something went wrong while processing your request.","status":400}`, check PocketBase server logs first; the browser response may hide the real hook exception.

### Query and app-context rules

* Use `routeUtils.queryValue(e, "name")` instead of `e.request.url.query().get("name")` in hook route code.
* Pass `e.app` into config helpers from route handlers, for example `config.getSettings(e.app)` and `config.polaris(e.app)`.
* Avoid adding new route-time dependencies on implicit global `$app` when the handler already receives `e.app`.

### PocketBase data access rules

Do not write or execute raw SQL for application data access, schema updates, migrations, tests, or repair scripts unless the user explicitly asks for a one-off forensic SQL query.

Use PocketBase APIs instead:

* `app.findRecordById`
* `app.findFirstRecordByFilter`
* `app.findRecordsByFilter`
* `app.save`
* `app.delete`
* collection field APIs in migrations
* existing project helpers that wrap PocketBase records

Do not use raw SQL strings through database handles, query builders, shell commands, SQLite CLIs, or ad hoc scripts to read or mutate project data. Raw SQL bypasses PocketBase record rules, relation handling, hook behavior, JSON normalization expectations, and migration conventions.

If a task seems to require SQL, first determine whether it can be expressed using PocketBase record/collection APIs. If it cannot, document why and do not introduce raw SQL unless the task explicitly authorizes it.

Before implementing any new setting, decide and document which of these models it uses:

* system-only: one value for the whole installation
* library-only: each library has its own value
* system default with library override: the system value is the default, but a library can override it

Do not add a setting until its scope is clear.

### Required implementation rule

For any new setting, the load path, form population path, save path, and read/use path must all agree on the same scope model.

A setting is not complete unless all four are handled:

1. where the initial or default value comes from for the declared scope
2. where a library-specific override comes from, if the model supports one
3. where the value is saved
4. how runtime code resolves the effective value

### Preferred model

For settings declared as system default with library override, use explicit fallback behavior:

* read library override first
* fall back to system value when no override exists
* save to the correct scope intentionally, never implicitly

### Do not do this

* Do not assume every new field belongs only at the system level.
* Do not save based only on what screen the user is on.
* Do not load from one scope and save to another.
* Do not add a field to the form without matching load/save logic and fallback logic where the declared scope requires it.
* Do not treat “missing override” and “blank override” as the same thing unless that is explicitly intended.

### Notes, comments, and audit history

Workflow notes must distinguish clearly between:

* existing committed history
* user draft comments
* pending system-generated audit text
* Do not show past-tense audit entries in editable notes fields before an action succeeds.

If preview text is needed, show it separately and label it as pending.

Only append system audit/history entries after successful completion of the action.

### Implementation guidance for agents

When adding a new setting:

* identify its scope in the PR description or code comment
* add it to the system-level loader if it is system-only or has a system default
* add it to the library-level loader if it is library-only or supports overrides
* make sure the save handler writes intentionally to a scope supported by the setting and selected context
* make sure the runtime reader and editor UI resolve values the same way, including fallback where supported
* verify that switching between system and library views preserves the expected value

### System-only settings

System-only settings live only at the system level. They are not library defaults, and a library context must not be able to edit or save them.

When presenting system-only settings from a library context:

* use "system level" language, not "system defaults"
* route any "switch to system level" action through the same context-switch path as the library selector
* warn about unsaved library changes before switching away from the library context
* keep system-only controls disabled while in library context, except for the explicit switch-to-system-level action
* never populate or save system-only fields from blank or disabled library-context controls
* verify library-context saves do not include system-only payload keys, while system-context saves still do

### Global records with scoped fields

Not every settings-adjacent screen is a library override setting. Staff access manages global `staff_users` records whose role and library fields determine scope; it does not save a library settings override.

When a screen manages global records with scoped fields:

* keep library context controls only when they filter or preselect record scope
* do not show "using system defaults" or "saving will create a library-specific override" messaging
* keep create/update/delete APIs scoped by authorization and record fields, not by settings override state
* add UI regression coverage near `tests/settings_staff_scope_banner.test.js` when changing settings navigation or scope banners

### Patron public option settings

Publication timing is a system default with library overrides.

Important paths:

* system defaults live in `ui_settings`
* library public-form overrides live in `patron_settings_overrides`

When resolving public-form options for a library, do not use a library `ui_settings` row as the default source. A library `ui_settings` row can exist only for branding or text overrides and may have blank option fields. Resolve option defaults from the system `ui_settings` row, then apply `patron_settings_overrides` for the selected library.

PocketBase JSON fields may come back through hooks as byte arrays. Never pass raw JSON-field arrays directly to the settings editor or public form. Decode byte-array JSON, parse it, and normalize it into `{ id, label, enabled, sortOrder }` objects before rendering. If publication timing labels ever display as numbers such as `91`, `123`, or `34`, treat that as a failed normalization bug.

When repopulating selects after a library/settings-context change, do not preserve invalid stale values for new suggestion fields. Preserving an old value is allowed only for edit flows where the existing record may contain a historical value.

Keep regression coverage for this behavior near `tests/config_ui_text_patron_options_scope.test.js` and `tests/staff_public_option_selects.test.js`.

### Frontend DOM safety

Prefer safe DOM APIs over `innerHTML` for new UI code. Never interpolate runtime data into arbitrary HTML strings.

Default to safe DOM APIs:

* create elements with `document.createElement`
* set text with `textContent`
* set attributes with `setAttribute`
* append children with `append`, `appendChild`, or `replaceChildren`
* use `classList` for classes

Do not inject patron, staff, Polaris, API, settings, or other runtime data into HTML strings.

If markup must be generated dynamically, build it with DOM nodes instead of string concatenation. Correct existing unsafe `innerHTML` when the requested work modifies or depends on that unsafe path. Do not rewrite unaffected markup merely because it is in the same file or nearby, or broaden a focused task solely for unrelated DOM cleanup.

Only use `innerHTML` when all of the following are true:

* the content is static developer-authored markup, or it has gone through a project-approved sanitizer
* no user/API/runtime data is interpolated into the string
* the reason is documented in a nearby code comment
* tests or manual verification cover the rendered path

Never treat escaping helpers as permission to build arbitrary HTML strings. Escaping reduces risk, but DOM construction is the preferred pattern.

### Analytics scope

All analytics must respect library scope.

Rules:

* Library users see only their own library’s analytics.
* Super admin may view a specific library or an all-libraries global view.
* Scope must be applied in the query/data layer, not just implied in the UI.
* Every new metric must define its scope behavior before implementation.

### Frontend request and refresh architecture

Use the existing frontend request helpers for staff and patron requests they cover. Do not introduce one-off fetch patterns or additional generalized layers when those helpers already provide the required transport behavior.

* When using `requestJson` or app wrappers such as `authorizedJson`, pass plain object bodies for JSON requests and let the helper serialize them. Do not pre-stringify JSON bodies unless you also intentionally manage the `Content-Type` contract; otherwise PocketBase routes may reject the request as `Unsupported Content-Type`.
* Keep cross-app behavior in the shared request core limited to transport concerns: JSON parsing, headers, cache mode, abort signals, and normalized errors.
* Keep auth/session policy in app-specific wrappers. Staff auth stays in `pb_public/staff/js/http.js`; patron session-expiry behavior stays in `pb_public/patron/js/api.js`.
* When staff requests can overlap and an older response could overwrite data for a newer UI scope, prevent stale rendering. Account for relevant changes in status/tab, workflow scope, analytics scope/range, selected library/settings context, and authentication context.
* Use the repository's existing abort/stale-result approach where appropriate. Do not add cancellation or stale-response machinery to loads that cannot meaningfully race with a newer scoped request.
* Route mutation follow-up reloads through explicit refresh helpers instead of scattering direct `loadTab(currentStatus)` calls through action modules.
* Do not add TanStack Query or another frontend cache layer unless the frontend architecture changes materially.
* Add regression coverage when changing request helpers or guarded load paths, especially for grid, settings, and analytics behavior.

### Manual verification checklist for new settings

Verify each new setting against its declared scope model; do not add scope behavior just to satisfy this checklist.

* System-only: verify system saves persist, reopening Settings shows the stored value, and library contexts cannot edit or save it. Do not add library-specific behavior.
* Library-only: verify library saves persist, switching libraries does not leak values, and reopening Settings shows the selected library's stored value. Do not add system-default behavior.
* System default with library override: verify system value persistence, library override persistence, override precedence only where intended, and fallback to the system value when an override is removed or disabled. Verify switching libraries does not leak values and reopening Settings shows the correct effective and/or stored values for the selected scope.

### Good design pattern

Use one clear source of truth for the selected settings scope. If the UI tracks a current org/system selection, all load and save helpers should consume that same state instead of re-deriving scope ad hoc in multiple places.

### JSON vs Goja Data Types and Normalization

PocketBase hooks interface with data originating from multiple engines. Be extremely careful when writing normalization or parser helpers to coerce types safely:

* PocketBase JSON fields loaded from SQLite are often handed to Goja hooks as raw byte arrays.
* JSON responses from external HTTP APIs (e.g., Polaris) are standard JavaScript objects containing primitives like `number`, `string`, and `boolean`.
* If a helper can receive a JavaScript `number` where it expects an array or string (e.g. iterating over `bytes.length`), handle it safely or coerce it using `String(value)` according to the actual contract.
* Falsy primitive values like `0` and `false` must not be accidentally swallowed or converted to `""` during normalization unless explicitly desired.
* Handle null or missing values when they are part of the actual contract, and normalize boundary values before rendering or use.
* Test representations the boundary being changed can materially produce, including representative primitives, falsy values, null/missing values, strings, and byte arrays when relevant to that helper. Keep existing regression tests protecting known historical failures.
* Prefer compact, table-driven tests when several representations need coverage. Do not build speculative test matrices for values the runtime contract cannot produce.
