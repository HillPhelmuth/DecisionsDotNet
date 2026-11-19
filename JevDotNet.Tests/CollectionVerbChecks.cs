using System.Text.Json;
using DecisionsDotNet;
using DecisionsDotNet.Testing;

public sealed class CollectionVerbChecks
{
    private sealed record Message(int Id, string Text);

    private static Message[] Messages(int count) => Enumerable.Range(0, count).Select(i => new Message(i, $"message {i}")).ToArray();

    // Answers each item's question from its own text: "spam" items are likely, everything else unlikely.
    private static FakeDecisionsClient SpamFake(Func<int, bool> isSpam) => new FakeDecisionsClient()
        .AnswerNoul(0.05)
        .AnswerNoul(0.95, q => isSpam(ItemIndex(q.Id)));

    private static int ItemIndex(string questionId) => int.Parse(questionId["item".Length..questionId.IndexOf('_')]);

    [Fact]
    public async Task TwoHundredItemsTakeTwentyRequestsAsync()
    {
        var recorder = new RecordingDecisionsClient(SpamFake(i => i % 10 == 0));
        var (spam, ham) = await recorder.ForModel("m").PartitionAsync(Messages(200), m => m.Text,
            "Is this unsolicited marketing?", minProbability: 0.8);

        Assert.Equal(20, recorder.Count);
        Assert.All(recorder.Requests, r => Assert.Equal(10, r.Questions.Count));
        Assert.Equal(20, spam.Count);
        Assert.Equal(180, ham.Count);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => i * 10), spam.Select(m => m.Id));
        Assert.True(ham.Select(m => m.Id).SequenceEqual(ham.Select(m => m.Id).Order()), "Source order is kept");
    }

    [Fact]
    public async Task RequestsCarryEachItemAndAScopedQuestionAsync()
    {
        var recorder = new RecordingDecisionsClient(SpamFake(_ => false));
        await recorder.ForModel("m").PartitionAsync(Messages(3), m => m.Text, "Spam?", 0.5,
            new DecisionCollectionOptions<Message> { ItemsPerRequest = 2 });

        Assert.Equal([2, 1], recorder.Requests.Select(r => r.Questions.Count));
        var first = recorder.Requests[0];
        var state = first.State.ToJsonElement().GetProperty("items");
        Assert.Equal("item0", state[0].GetProperty("id").GetString());
        Assert.Equal("message 1", state[1].GetProperty("input").GetString());
        Assert.Equal("About item \"item1\" only: Spam?", first.Questions["item1_q"].Instructions.AsText());
        Assert.All(recorder.Requests, r => Assert.Equal("m", r.Model));
    }

    [Fact]
    public async Task WhereStreamsMatchesABatchAtATimeAsync()
    {
        var recorder = new RecordingDecisionsClient(SpamFake(i => i is 1 or 7));
        var matches = new List<(int Id, int RequestsSoFar)>();
        await foreach (var match in recorder.ForModel("m").WhereAsync(Messages(10), m => m.Text, "Spam?", minProbability: 0.75,
                           new DecisionCollectionOptions<Message> { ItemsPerRequest = 5 }))
            matches.Add((match.Id, recorder.Count));

        Assert.Equal([(1, 1), (7, 2)], matches);
    }

    [Fact]
    public async Task AsyncSourcesWorkAndPrefilteredItemsAreNeverSentAsync()
    {
        static async IAsyncEnumerable<Message> Stream()
        {
            foreach (var message in Messages(6))
            {
                await Task.Yield();
                yield return message;
            }
        }

        var recorder = new RecordingDecisionsClient(SpamFake(_ => true));
        var options = new DecisionCollectionOptions<Message> { Prefilter = m => m.Id % 2 == 0 };
        var (spam, ham) = await recorder.ForModel("m").PartitionAsync(Stream(), m => m.Text, "Spam?", 0.5, options);

        Assert.Equal([0, 2, 4], spam.Select(m => m.Id));
        Assert.Equal([1, 3, 5], ham.Select(m => m.Id));
        Assert.Equal(3, Assert.Single(recorder.Requests).Questions.Count);

        var where = new List<int>();
        await foreach (var m in recorder.ForModel("m").WhereAsync(Stream(), m => m.Text, "Spam?", 0.5, options)) where.Add(m.Id);
        Assert.Equal([0, 2, 4], where);
    }

    [Fact]
    public async Task GroupByChoiceKeepsEvidencePerItemAsync()
    {
        var fake = new FakeDecisionsClient()
            .AnswerChoice(Team.Other, 0.5)
            .AnswerChoice(Team.Billing, 0.9, q => ItemIndex(q.Id) % 3 == 0)
            .AnswerChoice(Team.Technical, 0.7, q => ItemIndex(q.Id) % 3 == 1);
        var recorder = new RecordingDecisionsClient(fake);

        var groups = new List<DecisionGroup<Team, Message>>();
        await foreach (var group in recorder.ForModel("m").GroupByChoiceAsync<Team, Message>(Messages(7), m => m.Text, "Which team?"))
            groups.Add(group);

        Assert.Equal([Team.Billing, Team.Technical, Team.Other], groups.Select(g => g.Key));
        Assert.Equal([0, 3, 6], groups[0].Items.Select(m => m.Id));
        Assert.Equal([1, 4], groups[1].Items.Select(m => m.Id));
        Assert.All(groups[0].Results, r => Assert.Equal(0.9, r.Result.SelectedProbability));
        Assert.Equal(1, recorder.Count);
        var question = Assert.IsType<ChoiceQuestion>(recorder.Requests[0].Questions["item0_q"]);
        Assert.Equal("Payments, invoices, and refunds", question.Criteria["Billing"]!.AsText());
    }

    [Fact]
    public async Task OrderByScoreSortsLowestFirstAndKeepsTiesInOrderAsync()
    {
        var scores = new[] { 1.5, 0.2, 2.0, 0.2, 1.0 };
        var fake = new FakeDecisionsClient().Answer(q => q.Question is ScoreQuestion,
            q => new ScoreAnswer
            {
                Score = scores[ItemIndex(q.Id)],
                Legend = new Dictionary<string, string> { ["0"] = "a", ["1"] = "b", ["2"] = "c" },
                Probabilities = new Dictionary<string, double> { ["0"] = 1, ["1"] = 0, ["2"] = 0 },
                Confidence = 1,
            });

        var ordered = await fake.ForModel("m").OrderByScoreAsync<Mood, Message>(Messages(5), m => m.Text, "How upset?");
        Assert.Equal([1, 3, 4, 0, 2], ordered.Select(r => r.Item.Id));
        Assert.Equal([Mood.Calm, Mood.Calm, Mood.Frustrated, Mood.Angry, Mood.Angry], ordered.Select(r => r.Result.Value));
    }

    [Fact]
    public async Task FailuresAndRefusalsStopTheOperationAsync()
    {
        var failing = new FakeDecisionsClient().AnswerNoul(0.5).Throw(new TimeoutException(), r => r.Questions.ContainsKey("item10_q"));
        await Assert.ThrowsAsync<TimeoutException>(() =>
            failing.ForModel("m").PartitionAsync(Messages(20), m => m.Text, "Spam?", 0.5));

        var refusing = new FakeDecisionsClient().AnswerNoul(0.5).Refuse("item3_q");
        await Assert.ThrowsAsync<DecisionRefusedException>(() =>
            refusing.ForModel("m").PartitionAsync(Messages(5), m => m.Text, "Spam?", 0.5));

        var invalid = new FakeDecisionsClient().AnswerNoul(double.NaN);
        await Assert.ThrowsAsync<JsonException>(() => invalid.ForModel("m").PartitionAsync(Messages(2), m => m.Text, "Spam?", 0.5));
    }

    [Fact]
    public async Task ArgumentsAreCheckedBeforeAnyRequestAsync()
    {
        var recorder = new RecordingDecisionsClient(new FakeDecisionsClient().AnswerNoul(0.5));
        var model = recorder.ForModel("m");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => model.PartitionAsync(Messages(2), m => m.Text, "Spam?", 1.5));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => model.PartitionAsync(Messages(2), m => m.Text, "Spam?", 0.5,
            new DecisionCollectionOptions<Message> { ItemsPerRequest = 0 }));
        await Assert.ThrowsAsync<ArgumentException>(() => model.PartitionAsync(Messages(2), m => m.Text, " ", 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => model.WhereAsync(Messages(2), m => m.Text, "Spam?", double.NaN));
        Assert.Throws<ArgumentNullException>(() => model.WhereAsync((IEnumerable<Message>)null!, m => m.Text, "Spam?", 0.5));
        Assert.Equal(0, recorder.Count);

        var empty = await model.PartitionAsync(Array.Empty<Message>(), m => m.Text, "Spam?", 0.5);
        Assert.Empty(empty.Matching);
        Assert.Equal(0, recorder.Count);
    }
}
