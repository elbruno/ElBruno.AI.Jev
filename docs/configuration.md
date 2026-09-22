# Configuration and credentials

The SDK targets **official TypeSafe AI**, defaulting to `https://api.typesafe.ai`. Do not use an independent proxy credential or change the endpoint as an automatic fallback.

## Development

All samples and the live integration-test project share `UserSecretsId` **ElBruno.AI.Jev.Development**:

```powershell
.\scripts\Set-JevUserSecrets.ps1
dotnet user-secrets set "Jev:DefaultModel" "jev-1.13.0" --project samples\01-HelloChoice
```

The setup script prompts for a masked key and sends JSON to `dotnet user-secrets set --id ElBruno.AI.Jev.Development` through standard input. It sets only `Jev:ApiKey`, preserving the existing model and other settings. Run it once for all ten samples and live integration tests; `-WhatIf` previews the target without prompting or writing anything.

Never put the key in chat, source, command arguments, or test recordings. User-secrets are outside the repository, but **are not encrypted** and are only for development. A plaintext representation is necessarily created briefly in process memory to pass it to Secret Manager. The library itself never loads user-secrets or environment variables.

The sample host loads user-secrets explicitly, then environment variables and command-line configuration. `--offline` is a separate explicit switch installing a synthetic HTTP handler; it never falls back to live calls or silently handles missing live credentials.

### HTTP 401 after saving a key

Successful secret setup confirms local storage, not service authentication. If
the official API returns `401 Unauthorized`, verify that the key is active and
comes from the [official TypeSafe dashboard](https://console.typesafe.ai/keys),
not the independent `jevtypesafeai.com` proxy. Rerun the masked setup script to
replace the development key. An existing `Jev__ApiKey` environment variable takes
precedence over user-secrets, so check for an unintended override without
printing its value. Do not retry repeatedly or send the key to another host.

## Dependency injection

```csharp
builder.Services.AddJev(options =>
{
    options.ApiKey = builder.Configuration["Jev:ApiKey"] ?? "";
    options.DefaultModel = JevModels.Version1_13_0;
});
```

Resolve `IJevDecisionClient` or `JevClient`. Options validate on startup and resolution. Errors name configuration fields, never the credential value.

`AddJev` registers one default singleton facade. It creates and disposes a factory HTTP client **per operation**, while `IHttpClientFactory` manages handlers. It does not capture one short-lived HTTP client indefinitely. The returned `IHttpClientBuilder` allows configuring handlers and logging. Authorization is set on each request, not a shared client's default headers.

For several independently configured accounts, explicitly construct/register separate `JevClient` instances with the desired service keys/lifetimes. Do not call `AddJev` repeatedly expecting independent accounts: it configures one default client.

## Explicit HTTP ownership

```csharp
using var http = new HttpClient(new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
});
using var client = new JevClient(http, options); // borrowed by default
```

The constructor does not mutate the supplied client's timeout, base address, or default headers. Set `disposeHttpClient: true` only to transfer ownership. The parameterless-transport constructor owns its transport and disables redirects. Do not dispose a client while requests are running.

Clients snapshot options at construction. Rotate credentials by replacing the client/options registration through your application's lifecycle; mutating the original options object does not change an existing client.

## Production

Inject credentials from the application's controlled secret provider. In a host that reads environment variables, `Jev__ApiKey` maps to `Jev:ApiKey`. Avoid command-line credentials, which can appear in shell history/process listings.

An explicitly configured endpoint must be an HTTPS **origin**, without a path, user information, query, or fragment. Test-only HTTP requires both a loopback host and `AllowInsecureLoopback = true`. Do not derive endpoint overrides from untrusted requests.

The SDK has no native body logging. Applications should also configure HTTP/OTel logging to avoid sensitive headers and payloads. `RawRepresentation` and opt-in `IncludeErrorBody` are sensitive escape hatches, not safe default telemetry.
