using System.Text.Json;
using System.Text.Json.Nodes;
using DecisionsDotNet;
using DecisionsDotNet.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

public sealed class AIFunctionChecks
{
    [Fact]
    public async Task FixedToolExposesItsSchemaAndEvaluatesAsync()
    {
        var client = new FakeClient();
        var triage = Triage(client);
        Assert.True(triage.Name == "triage_ticket" && triage.Description == "Scores a ticket.", "Fixed tool metadata");
        var schema = triage.JsonSchema.GetProperty("properties");
        Assert.True(schema.TryGetProperty("input", out _) && !schema.TryGetProperty("cancellationToken", out _), "Fixed tool schema");

        var result = Parse(await triage.InvokeAsync(new AIFunctionArguments { ["input"] = "Charged twice" }, TestCancellation));
        var sent = client.Sent!;
        Assert.True(sent.State.AsText() == "Charged twice" && sent.Model == "typesafe/jev-1.14" && sent.Questions.Count == 2, "Fixed tool request");
        Assert.True(result["answers"]!["refund"]!["probability_yes"]!.GetValue<double>() == 0.912, "Yes/no result rounded");
        Assert.True(result["answers"]!["team"]!["choice"]!.GetValue<string>() == "billing", "Choice result");
    }

    [Fact]
    public async Task OpenToolBuildsQuestionsFromItsArgumentsAsync()
    {
        var client = new FakeClient();
        var open = client.AsOpenDecisionsAIFunction("gpt-6-luna");
        Assert.True(open.Name == "evaluate_decisions" && open.JsonSchema.GetProperty("properties").TryGetProperty("questions", out _),
            "Open tool schema");
        var questions = JsonSerializer.SerializeToElement(new[]
        {
            new { id = "angry", type = "score", instructions = "How angry?", levels = new[] { "Calm", "Upset", "Furious" } },
            new { id = "refund", type = "yes_no", instructions = "Wants a refund?", levels = (string[]?)null }
        });

        var result = Parse(await open.InvokeAsync(
            new AIFunctionArguments { ["input"] = "Ugh", ["questions"] = questions }, TestCancellation));
        var sent = client.Sent!;
        Assert.True(sent.Model == "gpt-6-luna" && sent.Questions["angry"] is ScoreQuestion { Criteria.Count: 3 } &&
            sent.Questions["refund"] is NoulQuestion, "Open tool builds questions");
        Assert.True(result["answers"]!["angry"]!["most_likely_level"]!.GetValue<string>() == "Furious", "Score most likely level");
    }

    [Fact]
    public async Task OpenToolRejectsAnUnknownQuestionTypeAsync()
    {
        var open = new FakeClient().AsOpenDecisionsAIFunction("gpt-6-luna");
        var error = await Assert.ThrowsAnyAsync<ArgumentException>(async () => await open.InvokeAsync(new AIFunctionArguments
        {
            ["input"] = "x",
            ["questions"] = JsonSerializer.SerializeToElement(new[] { new { id = "a", type = "guess", instructions = "?" } })
        }, TestCancellation));
        Assert.Contains("yes_no", error.Message);
    }

    [Fact]
    public void FixedToolRejectsEmptyQuestions()
        => Assert.ThrowsAny<ArgumentException>(() =>
            new FakeClient().AsAIFunction("t", "d", new Dictionary<string, DecisionsQuestion>(), "m"));

    [Fact]
    public void ToolPlugsIntoAnAgent()
    {
        // The README's agent wiring compiles against Microsoft Agent Framework.
        AIAgent agent = new ChatClientAgent(new NoChat(), instructions: "Call triage_ticket first.", tools: [Triage(new FakeClient())]);
        Assert.NotNull(agent);
    }

    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    private static AIFunction Triage(IDecisionsClient client) => client.AsAIFunction("triage_ticket", "Scores a ticket.",
        DecisionsRequest.Create("unused")
            .Noul("refund", "Wants money back?")
            .Choice("team", "Which team?", "billing", "other")
            .Questions, "typesafe/jev-1.14");

    private static JsonObject Parse(object? value) => JsonNode.Parse(JsonSerializer.Serialize(value))!.AsObject();

    private sealed class FakeClient : IDecisionsClient
    {
        public DecisionsRequest? Sent { get; private set; }

        public Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
        {
            Sent = request;
            return Task.FromResult(new DecisionsResponse
            {
                Model = "jev-1.14.0",
                Usage = new DecisionsUsage { InputTokens = 1, OutputTokens = 1 },
                Answers = request.Questions.ToDictionary(pair => pair.Key, pair => pair.Value switch
                {
                    NoulQuestion => (DecisionsAnswer)new NoulAnswer { Noul = 0.91234 },
                    ChoiceQuestion => new ChoiceAnswer { Choice = "billing", Confidence = 0.7,
                        Probabilities = new Dictionary<string, double> { ["billing"] = 0.8, ["other"] = 0.2 } },
                    ScoreQuestion => new ScoreAnswer { Score = 1.4, Confidence = 0.5,
                        Legend = new Dictionary<string, string> { ["0"] = "Calm", ["1"] = "Upset", ["2"] = "Furious" },
                        Probabilities = new Dictionary<string, double> { ["0"] = 0.1, ["1"] = 0.4, ["2"] = 0.5 } },
                    _ => throw new InvalidOperationException()
                })
            });
        }
    }

    private sealed class NoChat : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
