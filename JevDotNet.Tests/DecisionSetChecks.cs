using System.Text.Json;
using DecisionsDotNet;
using DecisionsDotNet.Testing;

public sealed class TicketTriage
{
    [Noul("Does this require immediate attention?", UnsureMin = 0.3, UnsureMax = 0.7, WhenTrue = "Blocking or time-sensitive")]
    public DecisionVerdict Urgent { get; init; } = null!;

    [Choice("Which team should handle this request?")]
    public DecisionChoice<Team> Team { get; init; } = null!;

    [Score("How frustrated is the customer?")]
    public DecisionScore<Mood> Mood { get; init; } = null!;

    [Noul("Is the customer asking for a refund?", MinProbability = 0.75, Id = "refund")]
    public bool WantsRefund { get; init; }

    [Noul("Does the customer mention a competitor?")]
    public double Competitor { get; init; }

    [Choice("Which team is second best?")]
    public Team Backup { get; init; }

    [Score("How severe is the problem?", "Cosmetic", "Degraded", "Down")]
    public int Severity { get; init; }

    public string NotAQuestion { get; set; } = "untouched";
}

public sealed class NoQuestions { public int Value { get; set; } }
public sealed class VerdictWithoutBand { [Noul("Q?")] public DecisionVerdict Verdict { get; init; } = null!; }
public sealed class BoolWithoutThreshold { [Noul("Q?")] public bool Flag { get; init; } }
public sealed class WrongType { [Choice("Q?")] public string Label { get; init; } = ""; }
public sealed class DuplicateIds { [Noul("A?", Id = "x")] public double A { get; init; } [Noul("B?", Id = "x")] public double B { get; init; } }
public sealed class ReadOnlyProperty { [Noul("Q?")] public double Value { get; } }
public sealed class BadBand { [Noul("Q?", UnsureMin = 0.8, UnsureMax = 0.2)] public DecisionVerdict Verdict { get; init; } = null!; }

public sealed class DecisionSetChecks
{
    [Fact]
    public async Task DecideAsksEveryQuestionInOneRequestAsync()
    {
        var recorder = new RecordingDecisionsClient(new FakeDecisionsClient()
            .AnswerNoul(0.9)
            .AnswerNoul("Competitor", 0.15)
            .AnswerChoice(Team.Billing, 0.8)
            .AnswerChoice("Backup", "Other")
            .AnswerScore(1.7)
            .AnswerScore("Severity", 0.4));

        var result = await recorder.ForModel("m").DecideWithResponseAsync<TicketTriage>("Charged twice, need it back now!");
        var triage = result.Value;

        var request = Assert.Single(recorder.Requests);
        Assert.Equal(["Urgent", "Team", "Mood", "refund", "Competitor", "Backup", "Severity"], request.Questions.Keys);
        Assert.Equal("Blocking or time-sensitive",
            Assert.IsType<NoulQuestion>(request.Questions["Urgent"]).Criteria!.True!.AsText());

        Assert.Equal(VerdictOutcome.Yes, triage.Urgent.Outcome);
        Assert.Equal(Team.Billing, triage.Team.Value);
        Assert.Equal(0.8, triage.Team.SelectedProbability);
        Assert.Equal(Mood.Angry, triage.Mood.Value);
        Assert.True(triage.WantsRefund);
        Assert.Equal(0.15, triage.Competitor);
        Assert.Equal(Team.Other, triage.Backup);
        Assert.Equal(0, triage.Severity);
        Assert.Equal("untouched", triage.NotAQuestion);
        Assert.Same(result.Response, triage.Team.Response);
        Assert.Equal("m", result.Response.Model);

        // The plan is cached, and DecideAsync returns the same values.
        var again = await recorder.ForModel("m").DecideAsync<TicketTriage>("Again");
        Assert.Equal(Team.Billing, again.Team.Value);
        Assert.Equal(2, recorder.Count);
    }

    [Fact]
    public async Task RefusalsOnlyFitVerdictPropertiesAsync()
    {
        var fake = new FakeDecisionsClient().AnswerNoul(0.9).AnswerChoice("Billing").AnswerScore(0)
            .Refuse("Urgent");
        var triage = await fake.ForModel("m").DecideAsync<TicketTriage>("x");
        Assert.Equal(VerdictOutcome.Refused, triage.Urgent.Outcome);

        fake.Refuse("Team");
        var refused = await Assert.ThrowsAsync<DecisionRefusedException>(() => fake.ForModel("m").DecideAsync<TicketTriage>("x"));
        Assert.Equal("Team", refused.QuestionId);
    }

    [Fact]
    public async Task InvalidAnswersAreRejectedAsync()
    {
        var fake = new FakeDecisionsClient().AnswerNoul(0.9).AnswerChoice("Sales").AnswerScore(0);
        await Assert.ThrowsAsync<JsonException>(() => fake.ForModel("m").DecideAsync<TicketTriage>("x"));
    }

    [Fact]
    public async Task InvalidDecisionSetsFailBeforeAnyRequestAsync()
    {
        var recorder = new RecordingDecisionsClient(new FakeDecisionsClient().AnswerNoul(0.5).AnswerChoice("x"));
        var model = recorder.ForModel("m");
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.DecideAsync<NoQuestions>("x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.DecideAsync<VerdictWithoutBand>("x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.DecideAsync<BoolWithoutThreshold>("x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.DecideAsync<WrongType>("x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.DecideAsync<DuplicateIds>("x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.DecideAsync<ReadOnlyProperty>("x"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => model.DecideAsync<BadBand>("x"));
        Assert.Equal(0, recorder.Count);
    }
}
