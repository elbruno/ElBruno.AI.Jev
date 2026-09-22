using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElBruno.AI.Jev;

if (args.Length != 5)
{
    throw new ArgumentException("Expected: package-version, assembly-sha256, portable-pdb-path, expected-repository-url-or-dash, expected-commit-or-dash.");
}

Assembly assembly = typeof(JevClient).Assembly;
string version = args[0];
string numericVersion = version.Split('-')[0] + ".0";
Check(assembly.GetName().Version?.ToString() == numericVersion, "Assembly version does not match the package.");
Check(FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion == numericVersion, "File version does not match the package.");
string? informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
Check(informationalVersion?.Split('+')[0] == version, "Informational version does not match the package.");
Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) == args[1],
    "The restored assembly is not the assembly in the validated package.");

using (FileStream pdbStream = File.OpenRead(args[2]))
using (MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream))
using (FileStream assemblyStream = File.OpenRead(assembly.Location))
using (var peReader = new PEReader(assemblyStream))
{
    MetadataReader reader = provider.GetMetadataReader();
    var pdbId = new BlobContentId(reader.DebugMetadataHeader!.Id);
    DebugDirectoryEntry codeView = peReader.ReadDebugDirectory().Single(entry => entry.Type == DebugDirectoryEntryType.CodeView);
    Check(peReader.ReadCodeViewDebugDirectoryData(codeView).Guid == pdbId.Guid,
        "Symbol package PDB does not match the packaged assembly.");

    if (args[3] != "-")
    {
        string repository = args[3].Replace("https://github.com/", "", StringComparison.Ordinal);
        Guid sourceLinkKind = new("CC110556-A091-4D38-9FEC-25AB9A351A6A");
        CustomDebugInformation sourceLink = reader.CustomDebugInformation
            .Select(reader.GetCustomDebugInformation)
            .Single(entry => reader.GetGuid(entry.Kind) == sourceLinkKind);
        using JsonDocument document = JsonDocument.Parse(reader.GetBlobBytes(sourceLink.Value));
        JsonProperty[] mappings = document.RootElement.GetProperty("documents").EnumerateObject().ToArray();
        Check(mappings.Length > 0, "Source Link contains no document mappings.");
        foreach (JsonProperty mapping in mappings)
        {
            string target = mapping.Value.GetString() ?? "";
            string prefix = $"https://raw.githubusercontent.com/{repository}/";
            Check(target.StartsWith(prefix, StringComparison.Ordinal), "Source Link targets an unexpected repository.");
            string commit = target[prefix.Length..].Split('/')[0];
            Check(commit.Length == 40 && commit.All(Uri.IsHexDigit), "Source Link must target a real immutable commit.");
            Check(string.Equals(commit, args[4], StringComparison.OrdinalIgnoreCase),
                "Source Link does not match the repository commit in the package manifest.");
        }
    }
}

using var handler = new FakeJevHandler();
using var httpClient = new HttpClient(handler);
using var client = new JevClient(httpClient, new JevClientOptions
{
    ApiKey = "package-test-placeholder",
    Endpoint = new Uri("https://package-tests.invalid"),
    DefaultModel = "package-test-model",
    MaxRetries = 0
});
IJevDecisionClient decisions = client;
var category = new JevQuestionKey<JevChoiceAnswer>("category");
var priority = new JevQuestionKey<JevScoreAnswer>("priority");
var escalation = new JevQuestionKey<JevNoulAnswer>("escalation");
var request = new JevDecisionRequest(JevJson.Parse("""{"ticket":"Synthetic package test"}"""))
    .WithQuestion(category, new JevChoiceQuestion("Choose a category.",
        new Dictionary<string, string?> { ["Billing"] = "An account issue", ["Other"] = null }))
    .WithQuestion(priority, new JevScoreQuestion("Assess priority.", ["Low", "High"]))
    .WithQuestion(escalation, new JevNoulQuestion("Escalation is required."));

JevDecisionResponse response = await decisions.EvaluateAsync(request);
Check(response.GetAnswer(category).Choice == "Billing", "Choice labels were not preserved.");
Check(response.GetAnswer(category).Probabilities["Billing"] == 0.8, "Choice distribution mismatch.");
Check(response.GetAnswer(priority).Score == 0.25, "Fractional score mismatch.");
Check(response.GetAnswer(priority).Legend["1"].GetString() == "High", "Score legend mismatch.");
Check(response.GetAnswer(escalation).Probability == 0.2, "Noul probability mismatch.");
Check(response.Model == "package-test-model-resolved", "Resolved model mismatch.");
Check(response.Usage.InputTokens == 12 && response.Usage.OutputTokens is null, "Nullable usage mismatch.");
Check(response.RequestId == "package-test-request", "Request ID mismatch.");
Check(response.RawRepresentation?.GetProperty("future_field").GetBoolean() == true, "Unknown metadata was lost.");

JevModelList models = await decisions.ListModelsAsync();
Check(models.Models.Single().Name == "package-test-model", "Model discovery mismatch.");
using var cancellation = new CancellationTokenSource();
cancellation.Cancel();
try
{
    await decisions.EvaluateAsync(request, cancellation.Token);
    throw new InvalidOperationException("Pre-cancelled evaluation unexpectedly succeeded.");
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
}
Check(handler.CallCount == 2, "Unexpected requests, retries, or cancellation behavior.");
Console.WriteLine($"PASS: packed ElBruno.AI.Jev {version}; versions, symbols, typed decisions, model discovery, and cancellation. No service network calls.");

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

internal sealed class FakeJevHandler : HttpMessageHandler
{
    public int CallCount { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        if (request.RequestUri?.Host != "package-tests.invalid" ||
            request.Headers.Authorization?.Scheme != "Bearer" ||
            request.Headers.Authorization.Parameter != "package-test-placeholder")
        {
            throw new InvalidOperationException("Unexpected fake endpoint or authentication.");
        }

        string json;
        if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/v1/systemone")
        {
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            JsonElement root = body.RootElement;
            if (root.GetProperty("model").GetString() != "package-test-model" ||
                root.GetProperty("state").GetProperty("ticket").GetString() != "Synthetic package test" ||
                root.GetProperty("questions").EnumerateObject().Count() != 3 ||
                root.GetProperty("questions").GetProperty("category").GetProperty("criteria").GetProperty("Other").ValueKind != JsonValueKind.Null ||
                root.GetProperty("questions").GetProperty("priority").GetProperty("criteria")[1].GetString() != "High" ||
                root.GetProperty("questions").GetProperty("escalation").GetProperty("type").GetString() != "noul")
            {
                throw new InvalidOperationException("Packaged serialization contract mismatch.");
            }
            json = """
                {
                  "model": "package-test-model-resolved",
                  "answers": {
                    "escalation": { "type": "noul", "noul": 0.2 },
                    "priority": { "type": "score", "score": 0.25, "probabilities": { "0": 0.75, "1": 0.25 }, "legend": { "0": "Low", "1": "High" }, "confidence": 0.8 },
                    "category": { "type": "choice", "choice": "Billing", "probabilities": { "Billing": 0.8, "Other": 0.2 }, "confidence": 0.7 }
                  },
                  "usage": { "input_tokens": 12 },
                  "future_field": true
                }
                """;
        }
        else if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/v1/models")
        {
            json = """{"models":[{"name":"package-test-model","description":"Synthetic fixture","release_date":"2026-01-01"}]}""";
        }
        else
        {
            throw new InvalidOperationException($"Unexpected fake request: {request.Method} {request.RequestUri.AbsolutePath}");
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        response.Headers.Add("x-typesafe-request-id", "package-test-request");
        return response;
    }
}
