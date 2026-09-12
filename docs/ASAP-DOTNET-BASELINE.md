# ASAP .NET Port Baseline

- PocketBase behavior reference: `150b30b776565194260cc327eeeffdfb46475e81`
- Port branch: `codex/csharp-port`
- Draft pull request: `#264`
- Intended permanent post-cutover tag convention: `pocketbase-final-YYYYMMDD`
- Currently deployed PocketBase production SHA: unverified

The permanent tag is not created until successful .NET production cutover. It
must point to the exact PocketBase commit frozen for that cutover, including any
intervening emergency production fix.

The pinned source contains `clc-carousel-manual-import-example/`. That subtree
is unrelated repository pollution, not ASAP behavior, a dependency, or
migration input, and is removed from the port branch in Slice 0.
