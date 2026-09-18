# Hangfire Operator Assets

Versioned files are exact bytes from the selected `Hangfire.SqlServer` NuGet
package, with its accompanying notices. The manifest records the package and
individual file SHA-256 hashes and target schema version. `.gitattributes`
preserves those bytes on Windows checkouts.

`1.8.25/install.sql` targets `[HangFire]` schema version 9. It is an operator
provisioning/upgrade asset, not part of the application DACPAC and not executed
by the ordinary runtime. Normal storage options disable automatic schema
preparation. A successful script command alone is not a compatibility check:
the upstream script can return without migrating an already newer schema.

Use only the port pack's controlled deployment flow for an existing database:
stop all workers, verify the required backup before DDL, apply version-matched
assets under the operator identity, and validate actual resulting compatibility
before restart. These source assets do not by themselves implement or prove
that deployment/recovery gate. Runtime identities remain least-privilege and
must not gain schema DDL rights.
