# Microsoft.Extensions.AI tools

Expose a caller-configured Jev decision through a real `AIFunction`, inspect its schema, and invoke it with an explicit textual state.

```powershell
dotnet run --project samples\07-MEAI-Tools -- --offline
```

Expected shape: a JSON result containing typed answers, complete uncertainty information, usage, and native metadata. It can be given to an independently configured chat provider via `ChatOptions.Tools`; see the [integration guide](../../docs/microsoft-extensions-ai.md).

For one live Jev request, configure [user-secrets](../../docs/configuration.md) and omit `--offline`. Defaults to pinned `jev-1.13.0`; calls may be billed. This sample directly invokes the function and requires no other chat-provider key. Tool results can contain sensitive native data; displaying this synthetic example is not a default logging recommendation.
