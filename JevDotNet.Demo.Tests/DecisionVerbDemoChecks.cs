using DecisionsDotNet;
using DecisionsDotNet.Testing;
using JevDotNet.Demo.Services;

// The demo pages now ask through the conditional verbs. These checks pin the wire shape the pages show and the logic on top.
public sealed class DecisionVerbDemoChecks
{
    private const string Model = "typesafe/jev-1.14";

    [Fact]
    public void ScenarioQuestionsKeepTheirWireNames()
    {
        var support = DemoScenarioCatalog.Questions("support");
        Assert.Equal(["team", "urgent", "frustration"], support.Keys);
        var team = Assert.IsType<ChoiceQuestion>(support["team"]);
        Assert.Equal(["Billing", "Technical", "Account", "Other"], team.Criteria.Keys);
        Assert.IsType<NoulQuestion>(support["urgent"]);
        Assert.Equal(3, Assert.IsType<ScoreQuestion>(support["frustration"]).Criteria.Count);
        Assert.Equal(["possible_violation", "category", "severity"], DemoScenarioCatalog.Questions("content").Keys);
        Assert.Equal(["intent", "demo_interest", "fit"], DemoScenarioCatalog.Questions("leads").Keys);
        Assert.Throws<ArgumentException>(() => DemoScenarioCatalog.BuildInput("support", " "));
        Assert.Throws<ArgumentException>(() => DemoScenarioCatalog.BuildInput("support", new string('x', DemoScenarioCatalog.MaxInputLength + 1)));
    }

    [Fact]
    public async Task SupportTriageRoutesFromOneRequestAsync()
    {
        var fake = new FakeDecisionsClient()
            .AnswerChoice("team", "Billing", 0.9)
            .AnswerNoul("urgent", 0.8)
            .AnswerScore("frustration", 0.4);
        var (response, recommendation) = await DemoScenarioCatalog.EvaluateAsync("support", "Charged twice!", fake.ForModel(Model),
            TestContext.Current.CancellationToken);

        Assert.Single(fake.Requests);
        Assert.Equal(3, response.Answers.Count);
        Assert.Equal("Route to billing · priority", recommendation.Title);

        fake.AnswerChoice("team", "Technical", 0.5);
        var (_, unsure) = await DemoScenarioCatalog.EvaluateAsync("support", "Hmm", fake.ForModel(Model), TestContext.Current.CancellationToken);
        Assert.Equal("Human review suggested", unsure.Title);
    }

    [Theory]
    [InlineData(0.9, 2.0, "Priority human review")]
    [InlineData(0.9, 0.5, "Human review suggested")]
    [InlineData(0.5, 2.0, "Human review suggested")]
    [InlineData(0.2, 2.0, "Low review priority")]
    public async Task ContentReviewUsesAVerdictBandAsync(double violation, double severity, string expected)
    {
        var fake = new FakeDecisionsClient()
            .AnswerNoul("possible_violation", violation)
            .AnswerChoice("category", "Spam", 0.9)
            .AnswerScore("severity", severity);
        var (_, recommendation) = await DemoScenarioCatalog.EvaluateAsync("content", "Buy now", fake.ForModel(Model),
            TestContext.Current.CancellationToken);
        Assert.Equal(expected, recommendation.Title);
    }

    [Fact]
    public async Task ARefusedViolationCheckStillGetsAReviewAsync()
    {
        var fake = new FakeDecisionsClient()
            .Refuse("possible_violation")
            .AnswerChoice("category", "Other", 0.9)
            .AnswerScore("severity", 0);
        var (_, recommendation) = await DemoScenarioCatalog.EvaluateAsync("content", "...", fake.ForModel(Model),
            TestContext.Current.CancellationToken);
        Assert.Equal("Human review suggested", recommendation.Title);
    }

    [Fact]
    public async Task LeadsNeedAllThreeSignalsAsync()
    {
        var fake = new FakeDecisionsClient()
            .AnswerChoice("intent", "Buying", 0.9)
            .AnswerNoul("demo_interest", 0.9)
            .AnswerScore("fit", 1.8);
        var (_, follow) = await DemoScenarioCatalog.EvaluateAsync("leads", "Demo please", fake.ForModel(Model), TestContext.Current.CancellationToken);
        Assert.Equal("Sales follow-up suggested", follow.Title);

        fake.AnswerNoul("demo_interest", 0.6);
        var (_, standard) = await DemoScenarioCatalog.EvaluateAsync("leads", "Demo please", fake.ForModel(Model), TestContext.Current.CancellationToken);
        Assert.Equal("Standard follow-up", standard.Title);
    }

    [Fact]
    public async Task LaneChoiceMapsToALaneNumberAsync()
    {
        var fake = new FakeDecisionsClient().AnswerChoice(Lane.Right, 0.8);
        var snapshot = new LaneSnapshot(1, true, false, 2, 0, 0, [new LaneObstacle(2, 900)]);
        var choice = await LaneDecision.ChooseAsync(fake.ForModel(Model), snapshot, TestContext.Current.CancellationToken);

        Assert.Equal(3, LaneDecision.Validate(choice));
        var question = Assert.IsType<ChoiceQuestion>(Assert.Single(fake.Requests).Questions[DecisionModel.QuestionId]);
        Assert.Equal(["Left", "Center", "Right"], question.Criteria.Keys);
        Assert.Contains("Current lane: 2", Assert.Single(fake.Requests).State.ToJsonElement().GetString());
    }

    [Fact]
    public async Task JudgeRatesByTheMostLikelyLevelAsync()
    {
        var fake = new FakeDecisionsClient().AnswerScore(3.3);
        var input = new JudgeInput("What is 2 + 2?", "4");
        var score = await JudgeCatalog.ScoreAsync(fake.ForModel(Model), "relevance", input, TestContext.Current.CancellationToken);
        var result = JudgeCatalog.ReadResult(score, TimeSpan.FromSeconds(1));

        Assert.Equal(4, result.Rating);
        Assert.Equal(4.3, result.WeightedScore, 6);
        Assert.Equal(1, result.Probabilities.Sum(), 6);
        Assert.Throws<ArgumentException>(() => JudgeCatalog.BuildState("completeness", input));
    }
}
