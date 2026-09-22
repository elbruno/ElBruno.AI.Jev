# Testing and validation status

## Offline suite

```powershell
dotnet build ElBruno.AI.Jev.slnx -c Release
dotnet test ElBruno.AI.Jev.slnx -c Release --no-build
dotnet run --project samples\03-ParallelDecisions -c Release --no-build -- --offline
```

Normal tests use instrumented handlers and deterministic chat clients, not Jev or another cloud model. Fixtures are authored from documented contracts; they are **not recorded evidence of server compatibility**.

Coverage excludes generated JSON metadata, not handwritten transport/mapping code. CI checks line/branch coverage and builds and runs every sample with explicit offline fixtures. Package checks restore the actual local `.nupkg` in an isolated consumer without project references. See the [release guide](releasing.md) for the package check script.

## Live suite: deliberate later handoff

Live tests are skipped unless `JEV_RUN_LIVE=1`. Once enabled, a missing key is a failure, not a skip or empty pass.

Configure the shared development store locally:

```powershell
.\scripts\Set-JevUserSecrets.ps1
dotnet user-secrets set "Jev:DefaultModel" "jev-1.13.0" --project tests\ElBruno.AI.Jev.IntegrationTests
$env:JEV_RUN_LIVE = "1"
dotnet test tests\ElBruno.AI.Jev.IntegrationTests -c Release
Remove-Item Env:JEV_RUN_LIVE
```

The full live suite has **three HTTP attempts total**, runs sequentially, uses synthetic ticket data, disables retries, and applies a 30-second per-operation deadline. It does not run load tests, deliberately trigger rate limits, or assume cancellations are free.

One test lists models and sends a mixed Choice/Score/Noul request. The other probes structured input, nullable instructions/descriptions, and structured Score legends. Failures in disputed contract behavior must be investigated and documented, not hidden with catch-and-pass logic.

Assertions inspect schema, correlation, ranges, types, and reported metadata rather than exact nondeterministic wording or presumed model accuracy. No response/key is written to a recording.

## Current boundary

Live service compatibility is unverified until this opt-in suite is deliberately run with an official key. Full generative-chat demos additionally require a separately configured chat provider. No amount of fixture testing proves account access, pricing, model availability, or the provider's undocumented behavior.
