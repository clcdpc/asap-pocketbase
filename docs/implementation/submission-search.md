# Search ASAP contract (#467)

Based on merged main `feebff80e5309a9adf2fdb7482418c1a97938015` (#477,
including #466 draft blockers; #469's Requests/navigation/counts are present).
The current-tab filters and their independent workflow totals remain separate.

- Read-only `POST /api/asap/staff/search`, with existing current-staff middleware
  and antiforgery. Terms stay in the JSON body, never a URL, history entry,
  storage, analytics or application log. EF sensitive-data logging is disabled.
- Active submission libraries only. Library staff/admin scope is always their
  current SQL library, regardless of submitted scope. Super-admins select a
  current library or `all`; System/organization 1 is invalid. The response owns
  the effective scope and available choices. Settings System context supplies
  no consortium authorization.
- Five title workflow groups plus distinct copy Open/Closed groups. Searchable
  persisted fields: title, author, native ID, submitted identifier and BIB ID;
  title requests also have first/last/full patron name and barcode. Copies never
  inherit a patron from creator/source links. IDs are exact decimal strings.
- Case-insensitive, accent-sensitive `Latin1_General_100_CI_AS_SC` text matching.
  Input whitespace is trimmed/collapsed. Every token must match a field on the
  same submission (including the combined first/last name). LIKE escapes `~`,
  `%`, `_`, `[` explicitly. Quotes, Unicode and markup are literal text.
  Native request IDs match exact whole numeric tokens using bigint parameters;
  other identifiers support literal token containment. Overlong numbers never
  truncate into a valid request identity.
- Rank: exact barcode/native ID/submitted identifier/BIB, exact title/author/full
  name, prefix title/author/full name, other matches. Ties: copy then title,
  ascending native bigint ID. Preview groups preserve that order within groups;
  full pages preserve the global order. Each typed submission appears once.
- Limits: 160 raw characters, 12 tokens, preview 12, full pages 25 (API 1–50),
  page 1–100000. Blank input returns idle guidance/scope without scanning requests.
  Invalid inputs fail before search. U+0000 is rejected, since the selected SQL
  collation ignores it in LIKE and would broaden literal queries. Pages clamp to
  the last existing page.
- Facets/`total` count the entire query and authorized scope; `filteredTotal`
  additionally applies the selected type/status group. Counts do not describe
  only the preview/page and do not replace Requests' workflow-tab totals.
  Independent read statements can observe concurrent changes; refresh or repeat
  the query recovers. No heavy snapshot/transaction infrastructure is added.
- EF projects minimal rows through SQL UNION ALL, one grouped-count query and
  one ordered OFFSET/FETCH page. No activity/notes/email/provider DTOs are built.
  Cancellation flows through HTTP/context/EF, including empty searches.
- Ctrl/Cmd+K opens the header dialog unless composing or another modal owns the
  keyboard. Debounce is 180ms. Arrows + Enter select simple listbox options with
  no nested interactive controls; Tab reaches normal dialog controls. Escape
  restores the launcher. An explicit Search button in title/copy details can
  reversibly suspend their host without unmounting drafts.
  The header owns its contrast independently of the current view's palette.
  Short viewports place query and library controls beside each other so the
  results remain readable with View all visible; arrow selection scrolls the
  identifying title even when a result is taller than the available pane.
- Search loads bind query/scope/filter/page/mode to a session and revision;
  replacement/close/navigation/sign-out aborts and rejects stale success/failure.
  Full results keep query/scope in session memory under `stage=search`; reloading
  starts idle. No search recents are retained.
  Reopening quick search from full results retains that view's explicit scope,
  filter and page. Other views initialize from their operational scope.
  Replacing that context also cancels in-flight search detail preparation and
  invalidates its departure consent, including changes made in the source view.
- Selection carries typed native ID, accepted scope and session owner to existing
  authoritative details. The existing prepare/revalidate/commit departure owner
  retains source drafts until acceptance. Current status drives alignment. Failed
  or denied details preserve the source and show recoverable feedback; subsequent
  preview/full refresh re-reads results. Full-results detail history returns to
  the retained results view through the normal guarded traversal.
  Navigation retires an open quick dialog before gathering departure consent,
  including browser history traversal, without unmounting source drafts.
  Detail preparation marks `searchResult=true` so both detail owners recheck
  active-library eligibility for search. Ordinary authorized All-scope detail
  access retains inactive-library journal/recovery behavior.

Real SQL query plans and measured costs, independent review findings, exact-head
CI/acceptance receipts and integration checkpoints belong in the PR. Raw receipts
remain outside Git. No speculative index, provider call or schema change.

Observed performance target for the retained 10,035-title/2,003-copy fixture:
first measured HTTP search within 2 seconds, subsequent searches within 1 second
(25-row pages, single-library and all-authorized scopes). These include current
staff middleware and serialization, but exclude UI debounce/session preflight.
Seeding warms SQL buffers; no server cache flush or cold-machine claim is made.
The plan uses substring predicates over library index ranges and bounded sorting; measurements do not justify
a speculative index. Browser receipts separately measure input-to-results time.

Consolidated human acceptance remains pending in #398: record build SHA,
tester/date, role/scope, OS/browser/actual screen reader; verify opening, typing,
scope/count/loading/error announcements, arrows/Enter, View all/filter/paging,
Escape/focus return, dirty Settings/title/copy warnings and Back/Forward.
Screenshots/axe do not establish that human acceptance. Full cross-app appearance
remains #456; #472/#479 integrations require their actual merged baseline.
