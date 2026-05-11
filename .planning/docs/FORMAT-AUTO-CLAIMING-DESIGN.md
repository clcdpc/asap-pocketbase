# Format Auto-Claiming Design

## Summary

ASAP should automatically claim newly submitted or format-edited suggestions based on library-specific format assignment rules. This extends the existing claim model on `title_requests`; it must not create a parallel assignment system.

Core product rule:

> Each library may configure one automatic claimant per suggestion format. When a suggestion is submitted or its format is changed, ASAP checks that library's format claiming rules. If a matching rule exists and the suggestion is unclaimed or currently auto-claimed, ASAP claims the suggestion for the configured staff member using the existing claim behavior. Manual claims are sticky and are never overwritten by automation. The interface clearly labels whether a claim was manual or automatically assigned by a format rule.

## Scope Model

Format auto-claiming rules are **library-only**.

There is no system default, no global consortium-level rule, and no library override of a system value. A rule is valid only inside the library identified by the suggestion's `libraryOrgId` / `libraryOrganization`.

Required scope paths:

| Path | Behavior |
| --- | --- |
| Default value | No default rule exists. Missing rule means no automatic claimant for that library/format. |
| Library override | Not an override model. The library rule is the only rule. |
| Save path | Admin save endpoints must write `format_claim_rules` rows for the selected library intentionally. |
| Runtime read path | Submission/edit automation must query `format_claim_rules` by the suggestion's effective library and format. |

## Data Model

### New Collection: `format_claim_rules`

Purpose: stores one active automatic claimant per format per library.

Fields:

| Field | Type | Notes |
| --- | --- | --- |
| `libraryOrganization` | Relation to `polaris_organizations` | Preferred canonical scope relation. |
| `libraryOrgId` | Text | Denormalized Polaris organization ID for filtering and compatibility with existing routes. |
| `format` | Text | Format code stored on `title_requests.format`, such as `book` or `dvd`. |
| `staffUser` | Relation to `staff_users` | Claim target. Must belong to the same library unless super-admin/system behavior is explicitly supported later. |
| `staffUserId` | Text | Denormalized staff ID for simple claim writes and display fallback. |
| `active` | Bool | Allows disabling without deleting history references. |
| `createdBy` | Relation/text | Staff user that created the rule. |
| `updatedBy` | Relation/text | Staff user that last changed the rule. |
| `created` | Date | PocketBase managed. |
| `updated` | Date | PocketBase managed. |

Indexes and constraints:

```text
unique(libraryOrgId, format)
index(libraryOrgId)
index(staffUserId)
```

The uniqueness rule enforces "one automatic claimant per format per library."

### `title_requests` Claim Metadata

Existing claim fields:

```text
claimedByStaffUserId
claimedByDisplayName
claimedAt
```

Add metadata fields:

```text
claimType
claimRuleId
```

Allowed `claimType` values:

| Value | Meaning |
| --- | --- |
| empty | Legacy/manual-compatible value for older claims before migration. Treat as manual when a claimant exists. |
| `manual` | A staff member manually claimed the suggestion. |
| `automatic_format_rule` | Automation claimed or reassigned the suggestion from a matching format rule. |

`claimRuleId` is set only when `claimType = automatic_format_rule`. It should be cleared for manual claims and unclaimed suggestions.

## Claim Semantics

Automation may change a claim only when:

```text
no current claimant exists
OR claimType = automatic_format_rule
```

Automation must not change a claim when:

```text
claimedByStaffUserId is present
AND claimType is empty/manual/unknown
```

Manual claim behavior:

| Action | Required Result |
| --- | --- |
| Staff manually claims a suggestion | Set claimant fields, `claimType = manual`, clear `claimRuleId`. |
| Staff/admin manually clears a claim | Clear claimant fields, `claimType`, and `claimRuleId`. |
| Manually claimed suggestion changes format | Keep manual claim; optionally show a warning before save. |
| Manual claim is cleared | Suggestion can become eligible for automation on the next relevant edit or explicit apply action. |

Auto claim behavior:

| Situation | Result |
| --- | --- |
| New unclaimed suggestion submitted with matching library/format rule | Auto-claim to configured staff user. |
| New unclaimed suggestion submitted with no matching rule | Leave unclaimed. |
| Auto-claimed suggestion changes to format with matching rule | Reassign to the matching staff user. |
| Auto-claimed suggestion changes to format with no matching rule | Clear the auto-claim and return to unclaimed. |
| Manually claimed suggestion changes to any format | Do not reassign or clear. |

## Service Contract

Create a small backend service, for example:

```text
applyFormatClaimRule(app, titleRequest, options)
```

Options:

```text
trigger: "submission" | "format_changed" | "manual_apply"
previousFormat?: string
actorName?: string
```

Flow:

```text
If the request has a manual/legacy claim:
    return unchanged

Find active format_claim_rules row where:
    libraryOrgId = titleRequest.libraryOrgId
    format = titleRequest.format

If no rule exists:
    If current claim is automatic_format_rule:
        clear claim fields and record audit event
    return

If rule exists:
    Resolve the staff user
    If staff user is missing/inactive/invalid:
        leave unchanged, record warning/audit event if appropriate
    Else:
        set existing claim fields using the same display-name behavior as manual claims
        set claimType = automatic_format_rule
        set claimRuleId = rule.id
        record audit event
```

The service should reuse or share code with the existing manual claim logic in `lib/staff_routes.js` so that "claimed" means the same thing everywhere.

## Runtime Integration

Call the service from:

| Trigger | Integration Point |
| --- | --- |
| Patron submission | After `records.createSuggestion(...)` succeeds in `lib/patron_routes.js`, before confirmation response. |
| Staff-created suggestion | After `records.createSuggestion(...)` succeeds in `lib/staff_routes.js`, before response/side effects complete. |
| Staff edit | In the `/api/asap/staff/title-requests/{id}/action` path only when `format` changes. |
| Manual apply action | Optional future admin endpoint that applies current rules to existing unclaimed/auto-claimed suggestions. |

Do not run reassignment on every edit. Re-evaluate when the effective library or format changes, or when an explicit admin action asks for it.

## Admin Configuration UI

Configuration should be available from two directions, backed by the same `format_claim_rules` collection.

### Format Settings View

For each format in the selected library:

```text
Format: DVD
Automatically claim suggestions to: [No automatic claimant / Staff member dropdown]
```

Save behavior:

| Selection | Save Result |
| --- | --- |
| No automatic claimant | Delete/deactivate the library's rule for that format. |
| Staff member selected | Upsert the library's rule for that format. |

### Staff Settings View

For each staff member:

```text
Automatically claimed formats
- DVD
- Large Print
- Audiobook
```

Editable version:

```text
Formats automatically assigned to this staff member:
[ ] Book
[x] DVD
[x] Large Print
[ ] Magazine
```

If a format is already assigned to another staff member, show a confirmation before replacing the rule:

```text
DVD is currently assigned to Jane Smith. Assigning it to Mark Jones will replace that rule.
```

### Bulk Setup View

Recommended first admin surface:

```text
Format        Auto-claim staff
Book          [dropdown]
DVD           [dropdown]
Large Print   [dropdown]
Audiobook     [dropdown]
```

This is likely the simplest initial UI because it makes the one-person-per-format rule visible.

## Edit Warning UI

When a staff user changes a suggestion's format, warn before save.

For unclaimed or auto-claimed suggestions:

```text
Changing the format may update the automatic claim assignment for this suggestion.
```

For currently auto-claimed suggestions:

```text
This suggestion is currently auto-claimed based on its format. Changing the format may reassign it to another staff member.
```

For manually claimed suggestions:

```text
This suggestion was manually claimed. Changing the format will not change the current claim.
```

Implementation note: new UI code must use DOM construction APIs rather than `innerHTML`, especially because claim labels can include staff names and runtime data.

## Claim Display

Suggestion list/detail views should distinguish claim source.

Manual:

```text
Claimed by Mark Jones
Manual claim
```

Automatic:

```text
Claimed by Jane Smith
Auto-assigned by format rule
```

Avoid staff-facing language like "special manual claim type." Use "Manual claim."

## Audit And History

Use `title_request_events` for claim automation history. Append to editable `notes` only after the action succeeds and only if the existing workflow requires staff-visible note history there.

Recommended events:

| Event Type | Message |
| --- | --- |
| `claim_auto_assigned` | `Auto-claimed by Jane Smith because DVD is assigned to Jane Smith for this library.` |
| `claim_auto_reassigned` | `Auto-claim changed from Jane Smith to Mark Jones because format changed from DVD to Audiobook.` |
| `claim_auto_cleared` | `Auto-claim cleared because no automatic claimant is configured for Audiobook.` |
| `claim_manual_assigned` | `Manually claimed by Mark Jones.` |
| `claim_manual_cleared` | `Manual claim cleared by Admin Name.` |

Audit rules:

- Do not show past-tense audit text in editable note fields before save succeeds.
- If the edit modal needs preview text, show it separately as pending.
- Only append system audit/history entries after successful completion.

## Permissions

Rule management:

| Role | Permission |
| --- | --- |
| `staff` | View claim labels; cannot configure rules. |
| `admin` | Configure rules for their own library only. |
| `super_admin` | Configure rules for any selected library. |

Runtime claiming:

Automation runs as system code but must resolve rules using the suggestion's library, not the actor's current settings screen or UI selection.

Inactive staff:

- Prevent creating new rules that point to inactive/deleted staff.
- Do not automatically delete existing rules if a staff member is later removed/deactivated.
- Surface invalid rules as "needs attention" in admin UI and skip automation until fixed.

## API Additions

Recommended endpoints:

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/asap/staff/format-claim-rules?libraryOrgId=...` | List rules for a library. |
| `POST` | `/api/asap/staff/format-claim-rules` | Bulk upsert rules for the selected library. |
| `POST` | `/api/asap/staff/title-requests/apply-format-claim-rules` | Optional future backlog apply action. |

The bulk upsert endpoint should accept the full intended library rule set for known formats so removing a dropdown value intentionally clears that rule.

## Backlog Apply Action

Useful but not required for the first implementation.

Behavior:

```text
Apply current format claiming rules to unclaimed and auto-claimed suggestions only.
Never touch manual claims.
Report how many suggestions were claimed, reassigned, cleared, skipped manual, and skipped invalid rule.
```

This action should be explicit and admin-triggered; it should not silently process historical suggestions when the feature is enabled.

## Test Plan

Backend unit tests:

- Rule lookup is library-only and does not fall back to system/global values.
- One format can be assigned to only one staff member per library.
- Same format can be assigned to different staff members in different libraries.
- New submission with matching rule gets `claimType = automatic_format_rule`.
- New submission with no matching rule remains unclaimed.
- Manual claim sets `claimType = manual` and clears `claimRuleId`.
- Automation does not overwrite manual or legacy claimed records.
- Auto-claimed format change reassigns when a new rule exists.
- Auto-claimed format change clears claim when no rule exists.
- Admin from library A cannot manage library B rules.
- Super admin can manage any selected library rules.

Frontend/manual verification:

- Saving rules in one library does not affect another library.
- Switching selected library shows only that library's rule values.
- Format settings and staff settings show the same underlying assignments.
- Format-change warning differs for unclaimed, auto-claimed, and manual claims.
- Claim badges identify manual vs automatic source.
- No new UI path injects runtime data with `innerHTML`.

## Implementation Order

1. Add migration for `format_claim_rules`, `claimType`, and `claimRuleId`.
2. Extract shared claim mutation helpers so manual and automatic claims use one source of truth.
3. Add `applyFormatClaimRule(...)` service and backend tests.
4. Wire submission and format-change triggers.
5. Add admin rule API endpoints with permission tests.
6. Add claim display metadata to `records.titleRequestToJson(...)`.
7. Add admin UI and edit warning UI using safe DOM APIs.
8. Add optional backlog apply action after first-pass behavior is stable.

## Open Product Decisions

Resolved recommendations for first pass:

- If an auto-claimed suggestion changes to a format with no rule, clear the auto-claim.
- Reassignment should run on submission and relevant edits, not every edit.
- Manual claims block automation until manually cleared.
- Backlog processing should be a separate explicit admin action.

Still open:

- Whether claim automation audit events should also append visible system notes to `title_requests.notes`, or rely only on `title_request_events`.
- Exact UI location for the first admin surface: existing format settings, staff access settings, or a new bulk matrix section.
