# Frontend source

This directory is tracked source. MSBuild copies it incrementally to the
ignored generated wwwroot directory for build, publish, and F5. The copy
target invokes no Node or npm command.

The patron and staff shells are the shipped application frontend. They use the
ASP.NET Core JSON endpoints and shared browser helpers; Node and npm are only
used by the development and CI test suites. Pinned third-party browser assets
are retained under vendor; vendor-manifest.json records exact versions, source
references, tarball hashes, file hashes, and license files. No browser SDK or

## Staff-assisted suggestions

The staff workflow's **New suggestion** action uses the scoped
`POST /api/asap/staff/patron-lookup` endpoint to verify a patron in Polaris
before showing the request form. A verified form submits to
`POST /api/asap/staff/title-requests`; the server applies the selected
servicing-library configuration, live pickup-branch preference race checks,
duplicate/auto-claim rules, optional confirmation outbox, creation provenance,
and immediate identifier processing in the existing request pipeline. The
form's catalog helper uses the scoped `POST /api/asap/staff/catalog-search`
endpoint to search Polaris by title, author, or identifier without exposing
provider credentials to the browser.
