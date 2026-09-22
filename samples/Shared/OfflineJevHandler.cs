using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Jev.Samples;

internal sealed class OfflineJevHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Method == HttpMethod.Get)
        {
            return Reply("""{"models":[{"name":"jev-latest","description":"Synthetic discovery fixture","release_date":"2026-09-22"},{"name":"jev-preview","description":"Synthetic discovery fixture","release_date":null}]}""");
        }

        JsonNode body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
        var answers = new JsonObject();
        foreach ((string name, JsonNode? questionNode) in body["questions"]!.AsObject())
        {
            JsonNode question = questionNode!;
            string type = question["type"]!.GetValue<string>();
            JsonNode answer;
            if (type == "choice")
            {
                string[] labels = question["criteria"]!.AsObject().Select(pair => pair.Key).ToArray();
                var probabilities = new JsonObject();
                foreach (string label in labels) probabilities[label] = label == labels[0] ? 1.0 : 0.0;
                answer = new JsonObject { ["type"] = type, ["choice"] = labels[0], ["probabilities"] = probabilities, ["confidence"] = 1.0 };
            }
            else if (type == "score")
            {
                JsonArray criteria = question["criteria"]!.AsArray();
                var probabilities = new JsonObject();
                var legend = new JsonObject();
                for (int index = 0; index < criteria.Count; index++)
                {
                    string key = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    probabilities[key] = index == 0 || index == criteria.Count - 1 ? 0.5 : 0;
                    legend[key] = criteria[index]?.DeepClone();
                }

                answer = new JsonObject
                {
                    ["type"] = type,
                    ["score"] = (criteria.Count - 1) / 2.0,
                    ["probabilities"] = probabilities,
                    ["legend"] = legend,
                    ["confidence"] = 0.25
                };
            }
            else if (type == "noul")
            {
                answer = new JsonObject { ["type"] = type, ["noul"] = 0.25 };
            }
            else
            {
                throw new InvalidOperationException("The offline fixture does not support this question type.");
            }

            answers[name] = answer;
        }

        return Reply(new JsonObject
        {
            ["model"] = "offline-fixture",
            ["answers"] = answers,
            ["usage"] = new JsonObject { ["input_tokens"] = 0, ["output_tokens"] = 0 }
        }.ToJsonString());
    }

    private static HttpResponseMessage Reply(string json)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.Add("x-typesafe-request-id", "offline-fixture");
        return response;
    }
}
