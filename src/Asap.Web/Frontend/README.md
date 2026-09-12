# Frontend source

This directory is tracked source. MSBuild copies it incrementally to the
ignored generated wwwroot directory for build, publish, and F5. The copy
target invokes no Node or npm command.

Slice 0 intentionally contains no patron or staff application shell because
their backend contracts are not implemented yet. Pinned third-party browser
assets are retained under vendor; vendor-manifest.json records exact versions,
source references, tarball hashes, file hashes, and license files. The
PocketBase browser SDK is intentionally excluded.
