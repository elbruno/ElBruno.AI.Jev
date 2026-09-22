# Dependency injection

Inject `IJevDecisionClient` into a small application service. Shared `SampleConfiguration` demonstrates Generic Host, `AddJev`, configuration, startup validation, and a synthetic handler override.

```powershell
dotnet run --project samples\06-DependencyInjection -- --offline
```

Expected shape: a review probability. The singleton SDK facade uses a fresh factory HTTP client per operation while factory handlers are pooled.

For one live request, configure [user-secrets](../../docs/configuration.md) and omit `--offline`. Defaults to `jev-1.13.0`; live calls may be billed. See [configuration](../../docs/configuration.md) for explicit multi-account registration and ownership.
