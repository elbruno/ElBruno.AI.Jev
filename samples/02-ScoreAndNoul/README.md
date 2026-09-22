# Score and Noul

Distinguish an expected rubric position from a proposition probability.

```powershell
dotnet run --project samples\02-ScoreAndNoul -- --offline
```

Expected shape: a fractional Score and a Noul probability. The boolean comparison at 0.8 is an **illustrative application policy**, not an SDK recommendation.

For one live mixed-question request, configure [user-secrets](../../docs/configuration.md) and omit `--offline`. The sample pins `jev-1.13.0` by default. Live calls may be billed; calibrate your own thresholds.
