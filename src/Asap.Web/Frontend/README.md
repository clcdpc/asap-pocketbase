# Frontend source

This directory is tracked source. MSBuild copies it to the
ignored generated wwwroot directory for build, publish, and F5. The copy
target invokes no Node or npm command. `Build/FrontendAssets.targets` computes a
SHA-256 identity over sorted published frontend paths and source bytes, replacing
`__ASAP_FRONTEND_VERSION__` only in generated staff/patron HTML. Identical inputs
retain the same version and unchanged generated HTML retains its timestamp.
The target runs before static asset discovery and publish collection, including
`publish --no-build`; tracked templates and module imports stay unchanged.
SDK endpoint precompression is disabled because this application serves files
through `UseStaticFiles`; unused compressed copies can otherwise retain a prior
build's HTML. Publish validation recomputes the identity from the packaged bytes.

First-party HTML, JavaScript (including shared/nested modules and the patron
embed loader), and CSS require HTTP revalidation. Static-file ETag/Last-Modified
validators allow unchanged responses to return 304. Pinned `/vendor/` URLs use
one-year immutable caching; images/fonts outside that directory retain the
default policy. Already-open pages are never automatically reloaded.

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
