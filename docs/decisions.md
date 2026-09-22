# Decisions and model semantics

Reference: [official API](https://docs.typesafe.ai/api), [primitives](https://docs.typesafe.ai/primitives/choice), [models](https://docs.typesafe.ai/models).

## Typed questions

Create a `JevQuestionKey<TAnswer>` and use `request.WithQuestion(key, question)`. The association is checked at compile time when adding the question and at runtime when retrieving `response.GetAnswer(key)`. Duplicate identifiers are rejected. The raw dictionary constructor is available for dynamically assembled requests.

Requests/questions are immutable snapshots. `JsonElement` inputs are cloned so the original `JsonDocument` can be disposed. Use `JevJson.Text` for text, `JevJson.Parse` for JSON, or `JevJson.From(value, jsonTypeInfo)` for user types with explicit serialization metadata.

State must be a JSON string, object, or array. Questions are evaluated independently against the same state. IDs correlate answers; their ordering is not meaningful and they are not instructions to the model.

## Choice

`JevChoiceQuestion` takes 1-255 case-sensitive labels and descriptions. Labels and descriptions influence inference. The SDK never substitutes enum ordinals, changes casing, or automatically adds an `other` label.

`JevChoiceAnswer` preserves `Choice`, `Probabilities`, and server `Confidence`. Confidence is not the selected option's probability and is not guaranteed accuracy. Define an explicit fallback option and review policy when the labels are not exhaustive.

## Score

Provide 2-10 self-contained ordered levels. The response is a fractional expected zero-based position:

`score = sum(index * probability[index])`

Keep the full distribution: certainty at level 1 and equal probabilities at levels 0 and 2 both yield 1.0 but have different uncertainty. The library neither rounds scores nor interprets them as percentages. Probability and legend keys remain native string indices (`"0"`, `"1"`...). Legends can contain structured JSON.

## Noul

`JevNoulAnswer.Probability` represents the probability of a proposition, in [0,1]. It has no invented confidence or automatic boolean/severity conversion. Thresholds belong to the application and need calibration. True/false outcome criteria are optional.

## Models

As documented when this preview was created:

| Constant | ID | Behavior |
| --- | --- | --- |
| `JevModels.Version1_13_0` | `jev-1.13.0` | Pinned version |
| `JevModels.Latest` | `jev-latest` | Moving stable alias; SDK default |
| `JevModels.Preview` | `jev-preview` | Moving preview alias; may equal stable |

Any nonblank future string ID is accepted. `/v1/models` can list aliases without listing every valid pinned ID. Discovery is not a whitelist. The response's `Model` records what actually executed.

Samples default to the pinned model. Recalibrate thresholds after changing models. Context/rate limits are provider-controlled and can change; the SDK does not invent a character-to-token estimator.

## Native response fidelity

Full native JSON, including unknown fields, is retained in `RawRepresentation`. Required fields, answer discriminators, expected question IDs, distributions, score indices, and numeric ranges are checked. Missing usage stays nullable. Unsupported answer types cause an explicit `JevProtocolException` with native data, not an empty successful answer.

The SDK does not renormalize distributions or recompute server confidence.

## Known contract questions

The HTTP reference and advanced/SDK docs disagree about some nullability. This preview accepts omitted/null instructions and structured/null descriptions as described by advanced docs, but requires non-null state as the HTTP reference does. Structured Score legends are preserved. Live tests include an explicit advanced-contract probe.

Exact identifier limits, Choice tie behavior, idempotency, remote cancellation/billing guarantees, and model retirement policy are not fully documented. These limitations must be reviewed during real-service validation before stable release.

Type-safe output is not semantic infallibility. The provider acknowledges that adversarial state can influence assessments; this SDK does not claim prompt-injection immunity.
