using System.ComponentModel;
using System.Net;
using System.Text.Json;
using DecisionsDotNet;
using DecisionsDotNet.Testing;
using Microsoft.Extensions.DependencyInjection;

public enum Team
{
    [Description("Payments, invoices, and refunds")] Billing,
    [Description("Bugs, errors, and service outages")] Technical,
    Other,
}

public enum Mood
{
    [Description("Calm")] Calm = 10,
    [Description("Frustrated")] Frustrated = 20,
    [Description("Very angry")] Angry = 30,
}

public enum Aliased { First = 1, Second = 1 }
public enum Lonely { Only }

public sealed class ConditionalVerbChecks
{
    private const string Model = "test-model";

    [Fact]
    public async Task ForModelBindsWithoutCallingAndSendsOneQuestionAsync()
    {
        var recorder = new RecordingDecisionsClient(new FakeDecisionsClient().AnswerNoul(0.8));
        var model = recorder.ForModel(Model);
        Assert.Equal(0, recorder.Count);
        Assert.Same(recorder, model.Client);

        Assert.Equal(0.8, await model.ProbabilityAsync("Refund please", "Wants a refund?",
            new NoulCriteria("Asks for money back", "Anything else")));

        var request = Assert.Single(recorder.Requests);
        Assert.Equal(Model, request.Model);
        Assert.Equal("Refund please", request.State.AsText());
        var question = Assert.IsType<NoulQuestion>(Assert.Single(request.Questions, q => q.Key == DecisionModel.QuestionId).Value);
        Assert.Equal("Asks for money back", question.Criteria!.True!.AsText());
        Assert.Throws<ArgumentException>(() => recorder.ForModel(" "));
        Assert.Throws<ArgumentNullException>(() => ((IDecisionsClient)null!).ForModel(Model));
    }

    [Theory]
    [InlineData(0.75, 0.75, true)]
    [InlineData(0.7499, 0.75, false)]
    [InlineData(0.0, 0.0, true)]
    [InlineData(1.0, 1.0, true)]
    [InlineData(0.99, 1.0, false)]
    public async Task MatchesComparesAgainstTheCallersThresholdAsync(double probability, double threshold, bool expected)
    {
        var model = new FakeDecisionsClient().AnswerNoul(probability).ForModel(Model);
        Assert.Equal(expected, await model.MatchesAsync("x", "Is it?", minProbability: threshold));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.PositiveInfinity)]
    public async Task InvalidThresholdsFailBeforeAnyRequestAsync(double threshold)
    {
        var recorder = new RecordingDecisionsClient(new FakeDecisionsClient().AnswerNoul(0.5));
        var model = recorder.ForModel(Model);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => model.MatchesAsync("x", "Is it?", threshold));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => model.VerdictAsync("x", "Is it?", (threshold, 0.9)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => model.VerdictAsync("x", "Is it?", (0.1, threshold)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => model.VerdictAsync("x", "Is it?", (0.8, 0.2)));
        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public async Task ShortCircuitingSkipsTheModelAsync()
    {
        var recorder = new RecordingDecisionsClient(new FakeDecisionsClient().AnswerNoul(0.9));
        var model = recorder.ForModel(Model);
        var ticketIsOpen = false;
        if (ticketIsOpen && await model.MatchesAsync("x", "Refund?", minProbability: 0.75))
            Assert.Fail("A closed ticket never reaches the model.");
        Assert.Equal(0, recorder.Count);
    }

    [Theory]
    [InlineData(0.71, VerdictOutcome.Yes)]
    [InlineData(0.7, VerdictOutcome.Unsure)]
    [InlineData(0.5, VerdictOutcome.Unsure)]
    [InlineData(0.3, VerdictOutcome.Unsure)]
    [InlineData(0.29, VerdictOutcome.No)]
    public async Task VerdictsUseAnInclusiveUnsureBandAsync(double probability, VerdictOutcome expected)
    {
        var verdict = await new FakeDecisionsClient().AnswerNoul(probability).ForModel(Model)
            .VerdictAsync("post", "Harassment?", unsureBand: (0.3, 0.7));
        Assert.Equal(expected, verdict.Outcome);
        Assert.Equal(probability, verdict.Probability);
        Assert.Equal(probability, verdict.Answer!.Noul);
        Assert.Equal(Model, verdict.Response.Model);
    }

    [Fact]
    public async Task RefusalsAreNeverFalseAsync()
    {
        var model = new FakeDecisionsClient().Refuse().ForModel(Model);

        var verdict = await model.VerdictAsync("post", "Harassment?", (0.3, 0.7));
        Assert.Equal(VerdictOutcome.Refused, verdict.Outcome);
        Assert.Null(verdict.Probability);
        Assert.Null(verdict.Answer);
        Assert.IsType<RefusalAnswer>(verdict.Response.Answers[DecisionModel.QuestionId]);

        var refused = await Assert.ThrowsAsync<DecisionRefusedException>(() => model.MatchesAsync("post", "Harassment?", 0.5));
        Assert.Equal(DecisionModel.QuestionId, refused.QuestionId);
        await Assert.ThrowsAsync<DecisionRefusedException>(() => model.ProbabilityAsync("post", "Harassment?"));
        await Assert.ThrowsAsync<DecisionRefusedException>(() => model.ChooseAsync<Team>("post", "Team?"));
        await Assert.ThrowsAsync<DecisionRefusedException>(() => model.ScoreAsync<Mood>("post", "Mood?"));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public async Task InvalidProbabilitiesFromTheProviderAreRejectedAsync(double probability)
    {
        var model = new FakeDecisionsClient().AnswerNoul(probability).ForModel(Model);
        await Assert.ThrowsAsync<JsonException>(() => model.MatchesAsync("x", "Is it?", 0.5));
        await Assert.ThrowsAsync<JsonException>(() => model.VerdictAsync("x", "Is it?", (0.3, 0.7)));
    }

    [Fact]
    public async Task ProviderFailuresPropagateAsync()
    {
        var failure = new DecisionsApiException(HttpStatusCode.Unauthorized, "{\"error\":\"bad key\"}");
        var model = new FakeDecisionsClient().AnswerNoul(0.9).Throw(failure).ForModel(Model);
        Assert.Same(failure, await Assert.ThrowsAsync<DecisionsApiException>(() => model.MatchesAsync("x", "Is it?", 0.5)));
        Assert.Same(failure, await Assert.ThrowsAsync<DecisionsApiException>(() => model.VerdictAsync("x", "Is it?", (0.3, 0.7))));
        Assert.Same(failure, await Assert.ThrowsAsync<DecisionsApiException>(() => model.ChooseAsync<Team>("x", "Team?")));
    }

    [Fact]
    public async Task CancellationReachesTheClientAsync()
    {
        var recorder = new RecordingDecisionsClient(new FakeDecisionsClient().AnswerNoul(0.9));
        using var cts = new CancellationTokenSource();
        await recorder.ForModel(Model).MatchesAsync("x", "Is it?", 0.5, cancellationToken: cts.Token);
        Assert.Equal(cts.Token, Assert.Single(recorder.Evaluations).CancellationToken);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            recorder.ForModel(Model).MatchesAsync("x", "Is it?", 0.5, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task ChooseOffersEnumMembersWithDescriptionsAsync()
    {
        var recorder = new RecordingDecisionsClient(new FakeDecisionsClient().AnswerChoice(Team.Technical, probability: 0.6));
        var route = await recorder.ForModel(Model).ChooseAsync<Team>("Site is down", "Which team should handle this request?");

        Assert.Equal(Team.Technical, route.Value);
        Assert.Equal(0.6, route.SelectedProbability);
        Assert.Equal(0.2, route.Probabilities[Team.Billing], 10);
        Assert.Equal(3, route.Probabilities.Count);
        Assert.Equal("Technical", route.Answer.Choice);
        Assert.Equal(Model, route.Response.Model);

        var question = Assert.IsType<ChoiceQuestion>(Assert.Single(recorder.Requests).Questions[DecisionModel.QuestionId]);
        Assert.Equal(["Billing", "Technical", "Other"], question.Criteria.Keys);
        Assert.Equal("Payments, invoices, and refunds", question.Criteria["Billing"]!.AsText());
        Assert.Null(question.Criteria["Other"]);
    }

    [Fact]
    public async Task ChooseWithExplicitCriteriaOffersOnlyThatSubsetAsync()
    {
        var recorder = new RecordingDecisionsClient(new FakeDecisionsClient().AnswerChoice(Team.Billing, 0.9));
        var criteria = new Dictionary<Team, DecisionsValue?> { [Team.Billing] = "Money", [Team.Other] = null };
        var route = await recorder.ForModel(Model).ChooseAsync("Refund", "Team?", criteria);

        Assert.Equal(Team.Billing, route.Value);
        var question = Assert.IsType<ChoiceQuestion>(Assert.Single(recorder.Requests).Questions[DecisionModel.QuestionId]);
        Assert.Equal(["Billing", "Other"], question.Criteria.Keys);
        Assert.Equal("Money", question.Criteria["Billing"]!.AsText());

        // A label from the enum that was not offered in this call is rejected.
        var outside = new FakeDecisionsClient().AnswerChoice(Team.Technical, 0.9).ForModel(Model);
        await Assert.ThrowsAsync<JsonException>(() => outside.ChooseAsync("Refund", "Team?", criteria));
    }

    [Fact]
    public async Task ChooseRejectsUnknownLabelsAndMissingProbabilitiesAsync()
    {
        var unknown = new FakeDecisionsClient().AnswerChoice("Sales", 0.9).ForModel(Model);
        await Assert.ThrowsAsync<JsonException>(() => unknown.ChooseAsync<Team>("x", "Team?"));

        var caseMismatch = new FakeDecisionsClient().AnswerChoice("billing", 0.9).ForModel(Model);
        await Assert.ThrowsAsync<JsonException>(() => caseMismatch.ChooseAsync<Team>("x", "Team?"));

        var numeric = new FakeDecisionsClient().AnswerChoice("0", 0.9).ForModel(Model);
        await Assert.ThrowsAsync<JsonException>(() => numeric.ChooseAsync<Team>("x", "Team?"));

        var missing = new FakeDecisionsClient().Answer(q => true, _ => new ChoiceAnswer
        {
            Choice = "Billing",
            Probabilities = new Dictionary<string, double> { ["Other"] = 1 },
            Confidence = 1,
        }).ForModel(Model);
        await Assert.ThrowsAsync<JsonException>(() => missing.ChooseAsync<Team>("x", "Team?"));

        var invalid = new FakeDecisionsClient().AnswerChoice(Team.Billing, probability: 1.5).ForModel(Model);
        await Assert.ThrowsAsync<JsonException>(() => invalid.ChooseAsync<Team>("x", "Team?"));
    }

    [Fact]
    public async Task EnumsThatCannotMapBackAreRejectedBeforeAnyRequestAsync()
    {
        var recorder = new RecordingDecisionsClient(new FakeDecisionsClient().AnswerChoice("First"));
        var model = recorder.ForModel(Model);
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.ChooseAsync<Aliased>("x", "Which?"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            model.ChooseAsync("x", "Which?", new Dictionary<Team, DecisionsValue?> { [(Team)42] = null }));
        await Assert.ThrowsAsync<ArgumentException>(() => model.ScoreAsync<Lonely>("x", "How?"));
        Assert.Equal(0, recorder.Count);
    }

    [Theory]
    [InlineData(0.0, Mood.Calm)]
    [InlineData(0.49, Mood.Calm)]
    [InlineData(0.5, Mood.Frustrated)]
    [InlineData(1.6, Mood.Angry)]
    [InlineData(2.0, Mood.Angry)]
    public async Task EnumScoresMapToTheNearestLevelAsync(double score, Mood expected)
    {
        var recorder = new RecordingDecisionsClient(new FakeDecisionsClient().AnswerScore(score));
        var result = await recorder.ForModel(Model).ScoreAsync<Mood>("I am fuming", "How frustrated is the customer?");

        Assert.Equal(expected, result.Value);
        Assert.Equal(score, result.Score);
        Assert.Equal([Mood.Calm, Mood.Frustrated, Mood.Angry], result.Levels);
        var question = Assert.IsType<ScoreQuestion>(Assert.Single(recorder.Requests).Questions[DecisionModel.QuestionId]);
        Assert.Equal(["Calm", "Frustrated", "Very angry"], question.Criteria.Select(level => level.AsText()));
    }

    [Fact]
    public async Task ScoresOutsideTheLevelsAreRejectedAsync()
    {
        await Assert.ThrowsAsync<JsonException>(() =>
            new FakeDecisionsClient().AnswerScore(2.5).ForModel(Model).ScoreAsync<Mood>("x", "How?"));
        await Assert.ThrowsAsync<JsonException>(() =>
            new FakeDecisionsClient().AnswerScore(double.NaN).ForModel(Model).ScoreAsync("x", "How?", ["Low", "High"]));
    }

    [Theory]
    [InlineData("3", 0.5)]
    [InlineData("-1", 0.5)]
    [InlineData("01", 0.5)]
    [InlineData("x", 0.5)]
    [InlineData("1", double.NaN)]
    [InlineData("1", 1.5)]
    public async Task ScoreDistributionsAreValidatedAsync(string level, double probability)
    {
        var fake = new FakeDecisionsClient().Answer(_ => true, _ => new ScoreAnswer
        {
            Score = 0.5,
            Legend = new Dictionary<string, string> { ["0"] = "a", ["1"] = "b", ["2"] = "c" },
            Probabilities = new Dictionary<string, double> { ["0"] = 0.5, [level] = probability },
            Confidence = 0.5,
        });
        await Assert.ThrowsAsync<JsonException>(() => fake.ForModel(Model).ScoreAsync<Mood>("x", "How?"));
        await Assert.ThrowsAsync<JsonException>(() => fake.ForModel(Model).ScoreAsync("x", "How?", ["a", "b", "c"]));
    }

    [Fact]
    public async Task ExplicitAndTextScoresAsync()
    {
        var model = new FakeDecisionsClient().AnswerScore(0.8).ForModel(Model);
        var levels = new Dictionary<Mood, DecisionsValue> { [Mood.Angry] = "Furious", [Mood.Calm] = "Fine" };
        var picked = await model.ScoreAsync("x", "How?", levels);
        Assert.Equal(Mood.Angry, picked.Value);
        Assert.Equal([Mood.Calm, Mood.Angry], picked.Levels);

        var text = await model.ScoreAsync("x", "How severe?", ["Minor", "Major", "Critical"]);
        Assert.Equal(1, text.Level);
        Assert.Equal(3, text.LevelCount);
        Assert.Equal(0.8, text.Score);
    }

    [Fact]
    public async Task EvaluateBuildsWithTheBoundModelAsync()
    {
        var recorder = new RecordingDecisionsClient(new FakeDecisionsClient().AnswerNoul(0.4).AnswerChoice("a"));
        var response = await recorder.ForModel(Model).EvaluateAsync(
            DecisionsRequest.Create("x").Noul("n", "N?", out var n).Choice("c", "C?", "a", "b"));
        Assert.Equal(0.4, response.Get(n).Noul);
        Assert.Equal(Model, Assert.Single(recorder.Requests).Model);
    }

    [Fact]
    public async Task VerbsWorkAgainstTheRealHttpPathAsync()
    {
        var body = """
            {"id":"r1","provider":"TypeSafe","model":"typesafe/jev-1.14","answers":{"decision":{"type":"noul","noul":0.82}},
             "usage":{"input_tokens":12,"output_tokens":1,"cost":0.0001}}
            """;
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body),
        }));
        using var client = new OpenRouterDecisionsClient("key", http);
        var verdict = await client.ForModel("typesafe/jev-1.14").VerdictAsync("x", "Is it?", (0.3, 0.7));
        Assert.Equal(VerdictOutcome.Yes, verdict.Outcome);
        Assert.Equal(12, verdict.Response.Usage.InputTokens);
    }

    [Fact]
    public void DecisionModelsResolveFromDependencyInjection()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDecisionsClient>(new FakeDecisionsClient());
        services.AddKeyedSingleton<IDecisionsClient>("other", new FakeDecisionsClient());
        services.AddDecisionModel("primary-model");
        services.AddKeyedDecisionModel("cheap", "cheap-model");
        services.AddKeyedDecisionModel("routed", "routed-model", clientServiceKey: "other");
        using var provider = services.BuildServiceProvider();

        var primary = provider.GetRequiredService<DecisionModel>();
        Assert.Equal("primary-model", primary.ModelId);
        Assert.Same(provider.GetRequiredService<IDecisionsClient>(), primary.Client);
        Assert.Equal("cheap-model", provider.GetRequiredKeyedService<DecisionModel>("cheap").ModelId);
        var routed = provider.GetRequiredKeyedService<DecisionModel>("routed");
        Assert.Same(provider.GetRequiredKeyedService<IDecisionsClient>("other"), routed.Client);
    }

    [Fact]
    public void ClassifyMatchesVerdicts()
    {
        Assert.Equal(VerdictOutcome.Yes, DecisionVerdict.Classify(0.9, 0.3, 0.7));
        Assert.Equal(VerdictOutcome.Unsure, DecisionVerdict.Classify(0.5, 0.5, 0.5));
        Assert.Equal(VerdictOutcome.No, DecisionVerdict.Classify(0, 0.1, 0.2));
        Assert.Throws<ArgumentOutOfRangeException>(() => DecisionVerdict.Classify(double.NaN, 0.3, 0.7));
    }
}
