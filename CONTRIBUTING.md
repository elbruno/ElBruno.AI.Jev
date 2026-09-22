# Contributing

Use the .NET 10 SDK selected by `global.json`. Keep changes scoped to the official TypeSafe Jev decision API and preserve the distinction between decisions and generative chat.

```powershell
dotnet build ElBruno.AI.Jev.slnx -c Release
dotnet test ElBruno.AI.Jev.slnx -c Release --no-build
dotnet format ElBruno.AI.Jev.slnx whitespace --verify-no-changes
```

Add deterministic contract and failure-path tests with every behavior change. Never require credentials for normal PR checks. Do not copy sensitive inputs or real keys into fixtures. Unknown fields must not be lost, unsupported features must not silently succeed, and new defaults must not unexpectedly increase inference cost.

Public APIs require XML documentation and corresponding sample/docs updates. Prefer standard Microsoft.Extensions.AI exchange types in integrations and keep native Choice/Score/Noul semantics intact. Do not suppress experimental API warnings globally.

Before a release, validate actual packages with the release guide, review public API changes, and compare against the previous stable package using SDK package validation. Live compatibility checks and publication require owner approval.

Report bugs with package/model versions, HTTP status, safe request ID, and a minimal synthetic reproduction. Never include credentials, raw production prompts, or unredacted HTTP logs.
