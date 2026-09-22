# Models and pinning

List account-visible models, then send an explicitly pinned decision and inspect the resolved model.

```powershell
dotnet run --project samples\05-ModelsAndPinning -- --offline
```

Expected shape: discovery entries plus requested/resolved IDs. Offline responses deliberately identify the model as `offline-fixture`.

For live execution, configure [user-secrets](../../docs/configuration.md) and omit `--offline`: this makes **two requests**, including one decision that may be billed. The decision explicitly pins `jev-1.13.0`. Discovery is not a whitelist of all valid pinned IDs.
