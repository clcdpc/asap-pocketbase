# Deployment report fixtures

`test27-check-constraint-replacements.xml` is the unchanged `deployment.xml`
from [test.27 attempt 2](https://github.com/clcdpc/asap-pocketbase/actions/runs/41678004381/attempts/2),
artifact `asap-test-sql-plan-41678004381-2` (ID `13255209381`), source commit
`05ac188294fc89a71826123bdf7167f3c7907393`.

Its SHA-256 is `7862d0ffa8408a78c4e878796531af5d06efd165738003728fd66e89be56f97d`.
It contains schema metadata only: three legacy-column loss warnings, 39
check-constraint drop/create pairs, the legacy index/default drops and two
table alterations. The retained SQL recreates and validates all 39 checks.
The former object allowlist rejected the first check-constraint replacement.
This fixture verifies that the full plan is authorized only with the DEV
override enabled; protected mode and target guards remain separate contracts.

`legacy-retirement-report.xml` is the smaller offline DacFx model-comparison
fixture used for the existing integrity and activation contracts.
