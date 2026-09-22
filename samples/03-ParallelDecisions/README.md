# Parallel decisions

Evaluate category, urgency, and review probability against a single shared ticket.

```powershell
dotnet run --project samples\03-ParallelDecisions -- --offline
```

Expected shape: Choice, Score, Noul, and request/usage metadata. Questions are independent, not a dependent workflow or service stream.

For one live request, configure [user-secrets](../../docs/configuration.md) and omit `--offline`. Defaults to pinned `jev-1.13.0`. Additional questions consume input tokens; live calls may be billed.
