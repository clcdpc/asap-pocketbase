# New patron request staff email (#498)

## Staff email producers

| Message | Producer | Recipients and address | Template scope |
| --- | --- | --- | --- |
| New patron purchase request | `PatronSuggestionService` in both public and staff-assisted insert paths | Active, eligible staff who can view the request and have the Profile preference enabled; primary `NotificationEmail` | Request-owning library override, then system default |
| Title request assignment | Title request assignment mutation | Assigned staff member; primary notification address | Request library, then system default |
| Purchase reminder | Purchase mutation when staff request a reminder | Eligible request staff; primary notification address | Request library, then system default |
| Additional-copy reminder or assignment | Additional-copy create and assignment mutations | Eligible task staff; primary notification address | Source request library, then system default |
| Weekly action summary | Weekly summary job | Active opted-in staff within the authorized summary scope; weekly address or primary fallback | Library scope, then system default |

New-request outbox rows and their initial delivery evidence are written in the
same SQL transaction as the accepted title request. Immediate dispatch starts
after commit; the recurring outbox sweep recovers pending rows if dispatch is
interrupted. Delivery rechecks identity, current access, active status, primary
address and the new-request preference. The rendered subject/body are saved at
submission time. `requestUrl` is blank when the configured staff URL is absent
or invalid; it never receives an invented host.

The diagnostic test email is a transport check, not a business notification.
Staff templates do not receive patron name, barcode or email tokens.
