using DecisionsDotNet;
using DecisionsDotNet.Testing;

public sealed class TestingPackageChecks
{
    [Fact]
    public async Task NewerRulesOverrideOlderOnesAsync()
    {
        var fake = new FakeDecisionsClient()
            .AnswerNoul(0.1)
            .AnswerNoul("urgent", 0.95)
            .AnswerChoice("billing", 0.7)
            .AnswerScore(1.25);

        var response = await fake.EvaluateAsync(DecisionsRequest.Create("x")
            .Noul("urgent", "Urgent?").Noul("spam", "Spam?")
            .Choice("team", "Team?", "billing", "tech", "other")
            .Score("mood", "Mood?", "Calm", "Upset", "Angry")
            .Build("m"));

        Assert.Equal(0.95, response.GetAnswer<NoulAnswer>("urgent").Noul);
        Assert.Equal(0.1, response.GetAnswer<NoulAnswer>("spam").Noul);
        var team = response.GetAnswer<ChoiceAnswer>("team");
        Assert.Equal("billing", team.Choice);
        Assert.Equal(1, team.Probabilities.Values.Sum(), 10);
        Assert.Equal(0.15, team.Probabilities["tech"], 10);
        var mood = response.GetAnswer<ScoreAnswer>("mood");
        Assert.Equal(1.25, mood.Score);
        Assert.Equal(1.25, mood.Probabilities.Sum(pair => int.Parse(pair.Key) * pair.Value), 10);
        Assert.Equal("Upset", mood.Legend["1"]);
        Assert.Equal("m", response.Model);
        Assert.Equal("fake", response.Provider);
        Assert.Equal("fake-1", response.Id);
    }

    [Fact]
    public async Task UnmatchedQuestionsFailLoudlyAsync()
    {
        var fake = new FakeDecisionsClient().AnswerNoul(0.5);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fake.EvaluateAsync(DecisionsRequest.Create("x").Choice("team", "Which team?", "a", "b").Build("m")));
        Assert.Contains("'team'", error.Message);
        Assert.Contains("Which team?", error.Message);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task MatchFunctionsSeeTheQuestionAndRequestAsync()
    {
        var fake = new FakeDecisionsClient()
            .AnswerNoul(0.0)
            .AnswerNoul(0.99, q => q.Instructions.Contains("refund", StringComparison.OrdinalIgnoreCase) && q.State.Contains("charged"))
            .Refuse(q => q.Request.Model == "strict");

        var model = fake.ForModel("m");
        Assert.True(await model.MatchesAsync("I was charged twice", "Wants a refund?", 0.9));
        Assert.False(await model.MatchesAsync("Hello", "Wants a refund?", 0.9));
        var verdict = await fake.ForModel("strict").VerdictAsync("I was charged twice", "Wants a refund?", (0.3, 0.7));
        Assert.Equal(VerdictOutcome.Refused, verdict.Outcome);
    }

    [Fact]
    public async Task ThrowRulesCanTargetRequestsAsync()
    {
        var fake = new FakeDecisionsClient().AnswerNoul(0.5)
            .Throw(new TimeoutException(), request => request.Model == "slow");
        Assert.Equal(0.5, await fake.ForModel("fast").ProbabilityAsync("x", "Q?"));
        await Assert.ThrowsAsync<TimeoutException>(() => fake.ForModel("slow").ProbabilityAsync("x", "Q?"));
        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task RecorderKeepsFailuresAndCanBeClearedAsync()
    {
        var recorder = new RecordingDecisionsClient(new FakeDecisionsClient().Throw(new TimeoutException()));
        await Assert.ThrowsAsync<TimeoutException>(() => recorder.ForModel("m").ProbabilityAsync("x", "Q?"));

        var evaluation = Assert.Single(recorder.Evaluations);
        Assert.IsType<TimeoutException>(evaluation.Error);
        Assert.Null(evaluation.Response);
        Assert.Equal("m", evaluation.Request.Model);

        recorder.Clear();
        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public async Task FakeChoicesCanUseEnumsAndSubsetsAsync()
    {
        var model = new FakeDecisionsClient().AnswerChoice(Team.Other, 0.55).ForModel("m");
        var subset = new Dictionary<Team, DecisionsValue?> { [Team.Billing] = null, [Team.Other] = null };
        var route = await model.ChooseAsync("x", "Team?", subset);
        Assert.Equal(Team.Other, route.Value);
        Assert.Equal(0.45, route.Probabilities[Team.Billing], 10);
    }
}
