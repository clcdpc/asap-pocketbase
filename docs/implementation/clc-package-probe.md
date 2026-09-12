# CLC Package Compatibility Probe

Verified 2026-09-12 as a Slice 1 prerequisite, not an architecture review.
Luna High performed the bounded probe; Astra independently reran the combined
fake invocation. Scratch project/source are ignored under
`.git/asap-package-probe`. No real Polaris or Postmark requests were made.

## Published Inputs

- NuGet `Clc.Polaris.Api` current prerelease `4.0.0-beta.3`, source
  `clcdpc/polaris-api-csharp` commit
  `9a1670b798b4b546d3527fd335578e137b9a0dee`. Nuspec requires
  `Clc.Rest.Client >= 3.0.0-beta.2`. The published async APIs accept cancellation
  for authentication, current patron data, reference/pickup, updates, BIB and
  hold calls, including intermediate protected-token acquisition.
- NuGet `Clc.Postmark.Api` latest `1.0.2` requires
  `Clc.Rest.Client >= 2.1.2`. Its nuspec supplies no source repository URL and
  its package includes no source. Package SHA-256:
  `14ec0d538f7d6f61e1d150dbfebedbef2643dec378e8d1021e4508ed49ee3bdb`.
- Combined normal transitive restore selects one Rest `3.0.0-beta.2` assembly.
  The project compiles, so a successful build alone misses the incompatibility.

## Runtime Evidence

`dotnet run --project .git/asap-package-probe/ProbeTransitive.csproj --no-build`
uses only fake HttpMessageHandler transport and `.invalid` endpoints.

Constructing the published Postmark client fails before any HTTP request:

```text
System.MissingMethodException: Method not found:
System.Net.Http.HttpClient Clc.Rest.RestClient.get_Client()
REQUEST_COUNT 0
```

Reflection finds only synchronous Send overloads and no CancellationToken send
parameter. Polaris ApiKeyValidateAsync succeeds against the fake handler and
the handler receives a cancellable transport token. This checks the dependency
combination, not live service credentials or all provider operations.

## Resolution Boundary

Do not duplicate Postmark protocol in ASAP, hide incompatibility behind a fake,
or weaken the outbox's complete-operation timeout/lease contract. A fresh
independent Astra Max consultation confirmed a focused modification of the
actual CLC Postmark package as the appropriate path: preserve existing request
construction/models, adapt to Rest 3, inject HttpClient and implement genuinely
cancellable SendAsync through response-body consumption with no internal send
retries. This is dependency work, not a second ASAP transport or architecture.
No upstream publication is authorized or required just to develop/test it.

An older stable Polaris/Rest combination is not automatically forbidden by the
pack's version-selection wording, but no exact-version evidence establishes a
compatible, completely bounded alternative. Wrapping synchronous Send with
Task.Run/WaitAsync only bounds waiting and does not cancel the underlying send.

Source access or a corrected maintained package is now requested from the
operator. A maintainable source checkout/archive with project files is enough;
no push/publication credentials are needed. Decompilation may aid inspection
but source reconstruction is not assumed to be a small maintainable extension.
Before adoption, probe stalled-header/body cancellation, sending, constructor
compatibility and credential isolation with fake transport. Until then Slice 1
cannot pass its real Postmark path gate; Slice 0 remains independent.

Public/authenticated GitHub searches for clcdpc Postmark and the exact package
name returned no repository; public Bitbucket clcdpc listing exposed none.
No corresponding library checkout exists in the workspace's sibling projects.
No assumption of authority to publish an upstream package is made.
