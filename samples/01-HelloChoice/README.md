# Hello Choice

Classify a synthetic invoice request and inspect the exact label, full distribution, and confidence.

```powershell
dotnet run --project samples\01-HelloChoice -- --offline
```

Offline mode prints synthetic `billing` with its distribution; it is not a model-quality demonstration. For one live request, configure [user-secrets](../../docs/configuration.md) and omit `--offline`. The default sample model is pinned to `jev-1.13.0`; override `Jev:DefaultModel` deliberately.

Live calls may be billed. Confidence is not the chosen probability or a correctness guarantee.
