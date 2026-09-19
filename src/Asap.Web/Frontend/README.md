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

`staff/` is the pinned PocketBase-era staff interface adapted to the ASP.NET
Core APIs. It is intentionally the primary compatibility client while the .NET
backend is validated against the established production workflow and visual
language.

`staff-next/` preserves the newer .NET staff interface for comparison and
possible future development. It remains runnable at `/staff-next/`, but is not
the primary acceptance surface.
