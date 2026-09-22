# Structured state

Use a typed record with source-generated `JsonTypeInfo`, structured instructions/criteria, and a null fallback description.

```powershell
dotnet run --project samples\04-StructuredState -- --offline
```

Expected shape: a typed Choice answer. JSON is independently owned, so no source `JsonDocument` must be retained.

For one live request, configure [user-secrets](../../docs/configuration.md) and omit `--offline`. Defaults to `jev-1.13.0`; live calls may be billed. Advanced nullability is separately checked by the opt-in integration suite.
