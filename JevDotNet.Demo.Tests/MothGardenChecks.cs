using System.Net;
using DecisionsDotNet;
using DecisionsDotNet.Testing;
using JevDotNet.Demo.Services.MothGarden;

// Moth Garden runs on ticks with no clock, so these checks step it by hand and drive the moths with a fake decision model.
public sealed class MothGardenChecks
{
    private const string Model = "typesafe/jev-1.14";
    private static readonly Func<Action, Task> Inline = action => { action(); return Task.CompletedTask; };

    [Fact]
    public void TheMazeIsOneConnectedGardenWithoutDeadEndsOutsideTheRoost()
    {
        var open = MothGardenMaze.OpenCells;
        Assert.All(open, cell => Assert.True(MothGardenMaze.Distance(MothGardenMaze.FireflyStart, cell) < int.MaxValue));
        Assert.All(open.Except(MothGardenMaze.RoostCells),
            cell => Assert.True(MothGardenMaze.OpenHeadings(cell).Count() >= 2, $"{cell.Text} is a dead end."));
        Assert.Equal(4, MothGardenMaze.RoostCells.Count);
        Assert.Equal(4, MothGardenMaze.BloomCells.Count);
        Assert.Equal(1, MothGardenMaze.Distance(new MazeCell(1, 1), new MazeCell(2, 1)));
        Assert.Equal(new MazeCell(1, 1), MothGardenMaze.NearestOpen(-5, -5));
    }

    [Fact]
    public void TheFireflyTakesAQueuedTurnAtTheFirstOpening()
    {
        var game = new MothGardenGame();
        game.Start();
        var start = game.Firefly;
        game.Steer(Heading.Up); // a hedge is above the start, so the firefly keeps flying left until it can turn
        game.Advance();
        Assert.Equal(start with { Column = start.Column - 1 }, game.Firefly);
        Assert.Equal(Heading.Up, game.QueuedHeading);
        Assert.Equal(MothGardenGame.PollenPoints, game.Score);

        while (game.QueuedHeading is not null) game.Advance();
        Assert.Equal(Heading.Up, game.FireflyHeading);
        Assert.True(MothGardenMaze.IsOpen(game.Firefly));
    }

    [Fact]
    public void ABloomDazzlesReleasedMothsAndTheFireflyCanCatchThem()
    {
        var game = new MothGardenGame();
        game.Start();
        FlyTo(game, MothGardenMaze.BloomCells.First(cell => cell.Row > 10));

        Assert.True(game.BloomRemaining > 0);
        var dusk = game.Moths[0];
        Assert.True(dusk.IsDazzled);
        Assert.All(game.Moths.Skip(1).Where(moth => moth.WaitTicks > 0), moth => Assert.False(moth.IsDazzled));

        // A dazzled moth flees: its reflex turn never brings it closer to the firefly than the alternatives.
        var exits = MothGardenMaze.OpenHeadings(dusk.Position).ToList();
        var flee = game.ReflexTurn(dusk, exits);
        Assert.Equal(exits.Max(h => MothGardenMaze.Distance(dusk.Position.Step(h), game.Firefly)),
            MothGardenMaze.Distance(dusk.Position.Step(flee), game.Firefly));
    }

    [Fact]
    public void LosingEveryLifeEndsTheGame()
    {
        var game = new MothGardenGame();
        game.Start();
        for (var tick = 0; tick < 5000 && !game.IsOver; tick++) game.Advance(); // the firefly idles against a hedge

        Assert.Equal(GardenStatus.Lost, game.Status);
        Assert.Equal(0, game.Lives);
        var ticks = game.Ticks;
        game.Advance();
        Assert.Equal(ticks, game.Ticks);
    }

    [Fact]
    public void ADecisionPointOffersOnlyTheOpenTurnsAtTheNextJunction()
    {
        var game = new MothGardenGame();
        game.Start();
        var dusk = game.Moths[0];
        var point = game.NextDecision(dusk);

        Assert.NotNull(point);
        Assert.Equal(dusk.Ticket, point.Ticket);
        Assert.Equal(dusk.Position, point.Junction);
        Assert.Equal([Heading.Left, Heading.Right], point.Options.Keys.Order());
        Assert.DoesNotContain(dusk.Heading.Opposite(), point.Options.Keys);
        Assert.Contains("Dusk", point.State);
        Assert.Contains("stalker", point.State);
        Assert.Contains("steps away", point.Options[Heading.Left]);
        Assert.StartsWith("Which way should Dusk turn", point.Question);
    }

    [Fact]
    public async Task TheModelSteersAMothWhenItsAnswerArrivesInTimeAsync()
    {
        var game = new MothGardenGame();
        game.Start();
        var fake = new FakeDecisionsClient().AnswerChoice(Heading.Up).AnswerChoice(Heading.Right, 0.8, Offers(Heading.Right));
        var director = new MothDirector(game, Inline);
        var dusk = game.Moths[0];
        var junction = dusk.Position;

        await Task.WhenAll(director.Dispatch(fake.ForModel(Model), TestContext.Current.CancellationToken));

        var asked = fake.Requests.Select(r => r.Questions.Single()).Single(q => q.Value.Instructions.AsText().Contains("Dusk"));
        var question = Assert.IsType<ChoiceQuestion>(asked.Value);
        Assert.Equal(["Left", "Right"], question.Criteria.Keys.Order());
        Assert.Equal(director.Requests, director.Answered);
        Assert.Equal(Heading.Right, director.LastDecision(dusk)!.Choice.Value);
        Assert.True(director.LastDecision(dusk)!.InTime);

        game.Advance();
        Assert.Equal(junction.Step(Heading.Right), dusk.Position);
        Assert.Equal(1, dusk.ModelTurns);
        Assert.Equal(0, dusk.ReflexTurns);
        Assert.True(dusk.LastTurnByModel);
    }

    [Fact]
    public async Task EachJunctionIsAskedAboutOnceAndOnlyOneRequestPerMothIsInFlightAsync()
    {
        var game = new MothGardenGame();
        game.Start();
        var gate = new TaskCompletionSource<DecisionsResponse>();
        var client = new GatedClient(gate.Task);
        var director = new MothDirector(game, Inline);
        var model = client.ForModel(Model);

        var first = director.Dispatch(model, TestContext.Current.CancellationToken);
        Assert.Equal(game.Moths.Count, first.Count);
        Assert.Empty(director.Dispatch(model, TestContext.Current.CancellationToken));
        Assert.All(game.Moths, moth => Assert.True(director.IsDeciding(moth)));
        Assert.Equal(game.Moths.Count, client.Calls);
        gate.SetCanceled(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ALateAnswerIsDroppedAndTheMothTurnsOnReflexAsync()
    {
        var game = new MothGardenGame();
        game.Start();
        var dusk = game.Moths[0];
        var point = game.NextDecision(dusk)!;

        game.Advance(); // Dusk reaches its junction with no decision, so its reflex picks the turn
        Assert.Equal(1, dusk.ReflexTurns);
        Assert.False(dusk.LastTurnByModel);
        Assert.False(game.SubmitTurn(dusk, point.Ticket, Heading.Left));

        var fake = new FakeDecisionsClient().AnswerChoice(Heading.Left);
        var director = new MothDirector(game, Inline);
        var model = new LateModel(fake, () => game.Advance()).ForModel(Model);
        await Task.WhenAll(director.Dispatch(model, TestContext.Current.CancellationToken));
        Assert.True(director.Late > 0);
        Assert.Contains(game.Moths, moth => director.LastDecision(moth) is { InTime: false });
    }

    [Fact]
    public async Task ARejectedKeyHaltsModelSteeringButTheGameGoesOnAsync()
    {
        var game = new MothGardenGame();
        game.Start();
        var fake = new FakeDecisionsClient().Throw(new DecisionsApiException(HttpStatusCode.Unauthorized, "{}"));
        var director = new MothDirector(game, Inline);

        await Task.WhenAll(director.Dispatch(fake.ForModel(Model), TestContext.Current.CancellationToken));

        Assert.True(director.Halted);
        Assert.True(director.Failures > 0);
        Assert.Contains("API key", director.LastError);
        for (var i = 0; i < 20; i++) game.Advance();
        Assert.Empty(director.Dispatch(fake.ForModel(Model), TestContext.Current.CancellationToken));
        Assert.True(game.Moths[0].ReflexTurns > 0);
    }

    [Fact]
    public async Task TheRequestCapStopsAskingAsync()
    {
        var game = new MothGardenGame();
        game.Start();
        var fake = new FakeDecisionsClient().AnswerChoice(Heading.Left);
        var director = new MothDirector(game, Inline, maxRequests: 2);

        await Task.WhenAll(director.Dispatch(fake.ForModel(Model), TestContext.Current.CancellationToken));

        Assert.Equal(2, director.Requests);
        Assert.True(director.Halted);
        Assert.Contains("2-request limit", director.LastError);
    }

    [Fact]
    public async Task AModelSteeredGamePlaysThroughWithoutErrorsAsync()
    {
        var game = new MothGardenGame(seed: 7);
        game.Start();
        var fake = new FakeDecisionsClient()
            .AnswerChoice(Heading.Right, 0.6)
            .AnswerChoice(Heading.Down, 0.6, Offers(Heading.Down))
            .AnswerChoice(Heading.Left, 0.6, Offers(Heading.Left))
            .AnswerChoice(Heading.Up, 0.6, Offers(Heading.Up));
        var director = new MothDirector(game, Inline, maxRequests: 10_000);
        var model = fake.ForModel(Model);
        var headings = new[] { Heading.Left, Heading.Up, Heading.Right, Heading.Down };

        for (var tick = 0; tick < 3000 && !game.IsOver; tick++)
        {
            if (tick % 23 == 0) game.Steer(headings[tick / 23 % 4]);
            game.Advance();
            await Task.WhenAll(director.Dispatch(model, TestContext.Current.CancellationToken));
            Assert.All(game.Moths, moth => Assert.True(MothGardenMaze.IsOpen(moth.Position)));
        }

        Assert.Equal(0, director.Failures);
        Assert.True(game.Moths.Sum(moth => moth.ModelTurns) > 0);
        Assert.True(game.Score > 0);
    }

    private static Func<FakeQuestion, bool> Offers(Heading heading)
        => q => ((ChoiceQuestion)q.Question).Criteria.ContainsKey(heading.ToString());

    private static void FlyTo(MothGardenGame game, MazeCell goal)
    {
        for (var tick = 0; tick < 400 && game.Firefly != goal; tick++)
        {
            var toward = MothGardenMaze.OpenHeadings(game.Firefly).MinBy(h => MothGardenMaze.Distance(game.Firefly.Step(h), goal));
            game.Steer(toward);
            game.Advance();
            Assert.True(game.Status is GardenStatus.Playing or GardenStatus.Caught, "The firefly was caught before reaching the bloom.");
        }
        Assert.Equal(goal, game.Firefly);
    }

    private sealed class GatedClient(Task<DecisionsResponse> gate) : IDecisionsClient
    {
        public int Calls;
        public Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return gate;
        }
    }

    /// <summary>Lets the game tick past the junction before the answer comes back.</summary>
    private sealed class LateModel(IDecisionsClient inner, Action beforeAnswer) : IDecisionsClient
    {
        public async Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
        {
            for (var i = 0; i < 12; i++) beforeAnswer();
            return await inner.EvaluateAsync(request, cancellationToken);
        }
    }
}
