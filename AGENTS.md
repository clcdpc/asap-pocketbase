## Settings scope: system vs library
One of the easiest ways to introduce bugs in this project is to add a new setting without handling its scope correctly.

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
