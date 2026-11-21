using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DecisionsDotNet;

/// <summary>
/// The OpenAI Decisions wire format (<c>model</c>, <c>input</c>, and a <c>questions</c> array of predicates, choices, and
/// levels), shared by OpenAI and Azure OpenAI.
/// </summary>
internal static class OpenAIDecisionsWire
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static string BuildBody(string model, IReadOnlyDictionary<string, DecisionsQuestion> questions, JsonElement state,
        bool hasImages, string service)
        => new JsonObject
        {
            ["model"] = model,
            ["input"] = BuildInput(state, hasImages),
            ["questions"] = new JsonArray(questions.Select(pair => (JsonNode)BuildQuestion(pair.Key, pair.Value, service)).ToArray())
        }.ToJsonString(JsonOptions);

    private static JsonNode BuildInput(JsonElement state, bool hasImages)
    {
        if (!hasImages)
            return JsonValue.Create(ImageInput.AsJsonText(state).ToString())!;

        // Images require a user message whose parts keep the state array's order.
        var parts = new JsonArray();
        foreach (var part in state.EnumerateArray())
        {
            if (ImageInput.IsImage(part))
            {
                ImageInput.ReadImage(part);
                var image = part.GetProperty("image_url");
                var node = new JsonObject { ["type"] = "input_image", ["image_url"] = image.GetProperty("url").GetString() };
                if (image.TryGetProperty("detail", out var detail)) node["detail"] = detail.GetString();
                parts.Add(node);
            }
            else parts.Add(new JsonObject { ["type"] = "input_text", ["text"] = ImageInput.AsJsonText(part).ToString() });
        }
        return new JsonArray(new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = parts });
    }

    private static JsonObject BuildQuestion(string name, DecisionsQuestion question, string service)
    {
        var instructions = Text(question.Instructions);
        switch (question)
        {
            case NoulQuestion noul:
                // Predicates take instructions only, so Noul criteria become part of them.
                if (noul.Criteria?.True is { } whenTrue) instructions += $"\nTrue when: {Text(whenTrue)}";
                if (noul.Criteria?.False is { } whenFalse) instructions += $"\nFalse when: {Text(whenFalse)}";
                return new JsonObject { ["type"] = "predicate", ["name"] = name, ["instructions"] = instructions };
            case ChoiceQuestion choice:
                return new JsonObject
                {
                    ["type"] = "choice", ["name"] = name, ["instructions"] = instructions,
                    ["choices"] = new JsonArray(choice.Criteria.Select(option => (JsonNode)new JsonObject
                    {
                        ["value"] = option.Key,
                        ["description"] = option.Value is null ? option.Key : Text(option.Value)
                    }).ToArray())
                };
            case ScoreQuestion score:
                return new JsonObject
                {
                    ["type"] = "score", ["name"] = name, ["instructions"] = instructions,
                    ["levels"] = new JsonArray(score.Criteria.Select((level, index) =>
                    {
                        var description = Text(level);
                        return (JsonNode)new JsonObject
                        {
                            ["label"] = description.Length <= 80 ? description : $"Level {index}",
                            ["description"] = description
                        };
                    }).ToArray())
                };
            case BoxQuestion:
                throw new NotSupportedException($"{service}'s Decisions API does not support Box questions.");
            default:
                throw new NotSupportedException($"{question.GetType().Name} is not supported by {service}'s Decisions API.");
        }
    }

    internal static DecisionsResponse ReadResponse(string json, IReadOnlyDictionary<string, DecisionsQuestion> questions, string service)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("answers", out var answers) ||
            answers.ValueKind != JsonValueKind.Array || !root.TryGetProperty("model", out var model) ||
            model.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(model.GetString()))
            throw new JsonException($"The {service} Decisions API returned an incomplete evaluation response.");

        var mapped = new Dictionary<string, DecisionsAnswer>(StringComparer.Ordinal);
        foreach (var answer in answers.EnumerateArray())
        {
            var name = answer.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String
                ? nameValue.GetString()! : throw new JsonException($"The {service} Decisions API returned an answer without a name.");
            if (!questions.TryGetValue(name, out var question) || mapped.ContainsKey(name))
                throw new JsonException($"The {service} Decisions API returned an unexpected answer '{name}'.");
            mapped[name] = ReadAnswer(answer, question, name, service);
        }
        if (mapped.Count != questions.Count)
            throw new JsonException($"The {service} Decisions API did not answer every question.");

        var usage = root.TryGetProperty("usage", out var usageValue) && usageValue.ValueKind == JsonValueKind.Object
            ? usageValue : throw new JsonException($"The {service} Decisions API returned no usage.");
        return new DecisionsResponse
        {
            Id = root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null,
            Provider = service,
            Model = model.GetString()!,
            Answers = mapped,
            Usage = new DecisionsUsage
            {
                InputTokens = usage.GetProperty("input_tokens").GetInt32(),
                OutputTokens = usage.TryGetProperty("output_tokens", out var output) ? output.GetInt32() : 0
            }
        };
    }

    private static DecisionsAnswer ReadAnswer(JsonElement answer, DecisionsQuestion question, string name, string service)
    {
        var type = answer.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
        switch (type, question)
        {
            case ("refusal", _):
                return new RefusalAnswer();
            case ("predicate", NoulQuestion):
                return new NoulAnswer { Noul = UnitInterval(answer, "probability", name, service) };
            case ("choice", ChoiceQuestion):
                return new ChoiceAnswer
                {
                    Choice = ChoiceValue(answer.GetProperty("choice"), service),
                    Probabilities = answer.GetProperty("probabilities").EnumerateArray().ToDictionary(
                        entry => ChoiceValue(entry.GetProperty("value"), service), entry => UnitInterval(entry, "probability", name, service)),
                    Confidence = UnitInterval(answer, "confidence", name, service)
                };
            case ("score", ScoreQuestion score):
                var levels = answer.GetProperty("probabilities").EnumerateArray().ToArray();
                return new ScoreAnswer
                {
                    Score = answer.GetProperty("score").GetDouble(),
                    Legend = Enumerable.Range(0, score.Criteria.Count).ToDictionary(
                        index => index.ToString(CultureInfo.InvariantCulture), index => Text(score.Criteria[index])),
                    Probabilities = levels.ToDictionary(
                        entry => entry.GetProperty("value").GetInt64().ToString(CultureInfo.InvariantCulture),
                        entry => UnitInterval(entry, "probability", name, service)),
                    Confidence = UnitInterval(answer, "confidence", name, service)
                };
            default:
                throw new JsonException($"The {service} Decisions API returned a missing or mismatched answer for '{name}'.");
        }
    }

    private static string ChoiceValue(JsonElement value, string service) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => throw new JsonException($"The {service} Decisions API returned an invalid choice value.")
    };

    private static double UnitInterval(JsonElement element, string property, string name, string service)
        => element.TryGetProperty(property, out var value) && value.TryGetDouble(out var number) &&
           double.IsFinite(number) && number is >= 0 and <= 1
            ? number : throw new JsonException($"The {service} Decisions API returned an invalid {property} for '{name}'.");

    private static string Text(DecisionsValue value) => ImageInput.AsJsonText(value.ToJsonElement()).ToString()!;
}
