# Library Additional Fields Design

## Summary

Add library-only additional fields that staff can define once and then use across patron submission formats. Each library can add text, textarea, and select fields, then configure each format to show each field as required, optional, or hidden. Submitted values are stored with the title request and are visible/editable in staff request surfaces.

This design intentionally does not add email placeholders, dynamic grid columns, system-level defaults, or cross-library inheritance in v1.

## Goals

- Let each library define reusable additional patron-form fields.
- Let each format decide which additional fields are required, optional, or hidden.
- Store submitted additional field values on `title_requests`.
- Show and edit additional field values in staff request detail/edit workflows.
- Keep system settings free of this library-only configuration.
- Preserve DOM safety by rendering all new dynamic UI with DOM APIs and plain text.

## Non-Goals

- System-level additional field defaults.
- Email/template placeholders for additional fields.
- Dynamic staff grid columns for each custom field.
- Query/reporting/filtering on individual additional field values.
- Additional v1 field types beyond `text`, `textarea`, and `select`.

## Scope Model

Additional fields are library-only settings.

- Default value: an empty definition list for system context and for libraries with no definitions.
- Library override source: `patron_settings_overrides.additionalFieldDefinitions` and `patronFormatRules[format].customFields`.
- Save path: library settings saves persist definitions and per-format custom field rules. System saves omit these fields.
- Runtime resolution: public config resolves definitions only for the selected library. There is no system fallback.

The load path, form population path, save path, and runtime read path must all use this same library-only model.

## Data Model

Add `additionalFieldDefinitions` to the library patron settings override/config path. The stored value is normalized JSON:

```json
[
  {
    "key": "platform",
    "label": "Platform",
    "type": "select",
    "helpText": "Choose the preferred game platform.",
    "enabled": true,
    "sortOrder": 10,
    "options": [
      { "id": "nintendo_switch", "label": "Nintendo Switch", "enabled": true, "sortOrder": 10 },
      { "id": "ps5", "label": "PlayStation 5", "enabled": true, "sortOrder": 20 }
    ]
  }
]
```

Extend per-format rules with a `customFields` object keyed by field key:

```json
{
  "videogame": {
    "messageBehavior": "none",
    "fields": {},
    "customFields": {
      "platform": { "mode": "required" },
      "content_warning": { "mode": "optional" }
    }
  }
}
```

Add `customFields` to `title_requests` as JSON. Submitted values are stored by stable key, not label:

```json
{
  "platform": {
    "label": "Platform",
    "type": "select",
    "value": "nintendo_switch",
    "displayValue": "Nintendo Switch"
  },
  "local_note": {
    "label": "Local note",
    "type": "textarea",
    "value": "Patron wants large print if possible."
  }
}
```

Changing a field label later does not rewrite historical request values. Disabling or deleting a definition does not delete historical submitted values.

## Patron Runtime

Public config includes the selected library's additional field definitions and effective per-format custom field rules. When a patron selects a format, the form renders enabled additional fields for that format under the existing canonical fields.

Frontend validation marks required additional fields as required before submission. Backend validation in the patron submission path enforces the same rules before saving:

- Hidden fields are discarded from new submissions.
- Required fields must contain a nonblank value.
- Text and textarea values are trimmed and length-limited.
- Select values must match an enabled configured option by id or label.
- Saved select values retain both the stable option id and a display label.

If a library has no additional field definitions, public behavior is unchanged.

## Staff Settings

Add an "Additional Fields" editor in the library patron/form settings area, after the existing format display/rules controls.

The editor supports:

- Add, edit, disable, and reorder field definitions.
- Field types: `text`, `textarea`, `select`.
- Required labels and stable keys.
- Help text.
- Select option editing with stable option ids.

In the existing per-format rules accordion, add an "Additional fields" area for each format. Each library-defined field can be set to required, optional, or hidden for that format.

System context does not show or save additional field controls.

## Staff Request Views

Request JSON includes `customFields`.

Staff request detail/edit surfaces show submitted additional field values as plain text. Staff can edit custom field values with the same format-specific validation rules used for patron submissions.

The main staff grid does not add dynamic columns in v1. This keeps the table stable and avoids making every library's fields alter shared workflow layout.

Historical custom values remain visible in staff details even if the field definition is later disabled or deleted. New submissions do not collect disabled or deleted fields.

## DOM Safety And Sanitization

New additional-field UI must not use `innerHTML` for dynamic content.

Use:

- `document.createElement`
- `textContent`
- `setAttribute`
- `append`, `appendChild`, or `replaceChildren`
- `.value` for form controls
- `classList` for classes

Labels, help text, select options, submitted values, and historical values are plain text. Select option labels are not markup. Staff and patron UIs render values with `textContent` or form control values, never by interpolating runtime data into HTML strings.

Backend validation stores only normalized plain text for v1 field values.

## Validation And Errors

Definition validation:

- Keys use lowercase letters, numbers, and underscores.
- Keys are unique within a library.
- Labels are required.
- Type must be `text`, `textarea`, or `select`.
- Select definitions must have at least one enabled option before they can be required for a format.
- Disabled fields cannot be newly required by a format.

Value validation:

- Required custom fields must have nonblank values.
- Hidden custom fields are removed from new submissions.
- Text values are limited to 250 characters.
- Textarea values are limited to 2000 characters.
- Select option ids and labels are limited to 128 characters.
- Unknown field keys are ignored for new patron submissions.

If malformed JSON is read from PocketBase, normalization returns empty definitions/rules and logs where backend code already has logging access.

## Storage And Migration

Use PocketBase collection field APIs in migrations. Do not use raw SQL.

Required schema additions:

- JSON field for library additional field definitions on the library patron settings override/config collection.
- JSON field `customFields` on `title_requests`.

Existing records receive an empty `customFields` value by default through normalization/read paths. No migration should rewrite existing title request data.

## Testing

Automated coverage includes:

- Config normalization for definitions, keys, labels, types, select options, enabled flags, and invalid input.
- Settings scope: library saves include additional fields, system saves omit them, and switching libraries does not leak definitions.
- Public config: each library receives only its own additional field definitions and per-format custom field rules.
- Patron validation: required fields are enforced, hidden fields are discarded, select values must match configured options, and valid values are saved.
- Request persistence: saved custom fields appear in `title_requests` JSON output.
- Staff edit validation: custom fields can be edited with the same rules.
- DOM safety: focused tests or static analysis cover new additional-field UI paths and prevent `innerHTML` for dynamic content.

Manual smoke coverage:

- Save field definitions at a library level.
- Make one custom field required for one format and hidden for another.
- Submit patron requests for both formats.
- Confirm staff can view and edit saved custom field values.

## Open Decisions

No open product decisions remain for v1.
