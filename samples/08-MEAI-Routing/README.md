# Microsoft.Extensions.AI routing

Use Jev Choice to select between two clearly labeled deterministic `IChatClient` implementations.

```powershell
dotnet run --project samples\08-MEAI-Routing -- --offline
```

Expected shape: the selected client's reply and separate Jev decision metadata. Offline Choice always selects the first label, so this demonstrates wiring rather than model quality.

For one live Jev routing decision, configure [user-secrets](../../docs/configuration.md) and omit `--offline`. The router explicitly pins `jev-1.13.0`. Calls may be billed; downstream chat remains synthetic until you replace it with real providers and their own credentials.

The 0.6 confidence threshold is illustrative. Low-confidence/unknown routes throw without hidden expensive fallbacks. Jev decision usage is separate from chat usage.
