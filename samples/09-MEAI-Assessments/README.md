# Microsoft.Extensions.AI assessments

Buffer a deterministic chat stream, assess the whole output, then release the approved original updates.

```powershell
dotnet run --project samples\09-MEAI-Assessments -- --offline
```

Expected shape: a synthetic reply appears only after approval. Offline Noul returns 0.25, permitting this illustrative `< 0.5` policy.

For one live Jev assessment, configure [user-secrets](../../docs/configuration.md) and omit `--offline`. Defaults to pinned `jev-1.13.0`; calls may be billed. Chat remains a deterministic demonstration.

`BufferedOutputReview` does not also check the input. Compose a separate input wrapper when needed. Generation cost has already occurred if the output is rejected. The example's threshold is not calibrated, and the assessment is **not guaranteed content safety or prompt-injection protection**. See the [integration guide](../../docs/microsoft-extensions-ai.md).
