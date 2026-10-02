# Frontend source

This directory is tracked source. MSBuild copies it incrementally to the
ignored generated wwwroot directory for build, publish, and F5. The copy
target invokes no Node or npm command.

The patron and staff shells are the shipped application frontend. They use the
ASP.NET Core JSON endpoints and shared browser helpers; Node and npm are only
used by the development and CI test suites. Pinned third-party browser assets
are retained under vendor; vendor-manifest.json records exact versions, source
references, tarball hashes, file hashes, and license files. No browser SDK or
generated dependency bundle is required at runtime.

Staff routes round-trip Profile, request stages, operational library scope
(`scope=all` or an active member library), and Additional Copies `copyStatus`
(`open` by default, or `closed`). Settings uses its separate `settingsScope`
and panel hash. Operations scope remains session-only; changing it does not
push a history entry. Analytics retains its existing scope/range load model.

Staff history entries mark their position and detail origin. Explicit detail
close returns to a known parent entry, or replaces an initial deep link with
its aligned list. Rejected draft navigation restores the original history
entry. Request workflow mutations require saved/reverted editor fields, and
Close, Escape, navigation, sign-out, and unload protect unsaved drafts.

Manual Operations POSTs are single-flight across all controls, including
email retries. Pending intent is saved in session storage before dispatch.
Uncertain outcomes keep new operations disabled across navigation/reload;
refreshing Operations permits an explicit retry of the retained operation.
Forced summaries reuse the durable run identity and per-recipient outbox
business keys. Test email reuses an actor/library/operation outbox key under
the existing SQL locks. Failed-email retries retain the original rowversion.
Ordinary workflow and weekly-summary retries retain their existing durable
work and weekly-period deduplication. Navigation never aborts a mutation as
evidence of rollback.
