using System.Net;
using System.Text.Json;
using DecisionsDotNet;

public sealed class BatchChecks
{
    private sealed record Message(string Id, string Text);

    private static readonly Message[] Messages =
        Enumerable.Range(1, 5).Select(i => new Message($"m{i}", $"Text {i}")).ToArray();

    private static DecisionsBatchOptions<Message> Options(bool continueOnError = true) => new()
    {
        ItemId = message => message.Id,
        State = batch => DecisionsValue.FromObject(new { messages = batch }),
        Questions = message => new Dictionary<string, DecisionsQuestion>
        {
            ["refund"] = new NoulQuestion($"Does {message.Id} want a refund?"),
            ["angry"] = new NoulQuestion($"Is {message.Id} angry?")
        },
        Model = "typesafe/jev-1.14",
        ItemsPerRequest = 2,
        ContinueOnError = continueOnError
    };

    [Fact]
    public async Task BatchesPreserveItemMappingAndReportFailuresAsync()
    {
        var bodies = new List<JsonElement>();
        var client = FailingOnThirdMessage(bodies);

        var results = new List<DecisionsBatchResult<Message>>();
        await foreach (var result in client.EvaluateBatchesAsync(Messages, Options(), TestCancellation))
            results.Add(result);

        Assert.Equal([2, 2, 1], results.Select(result => result.Items.Count));
        Assert.Equal([1, 2, 3], results.Select(result => result.Number));
        Assert.Equal(["m1_refund", "m1_angry", "m2_refund", "m2_angry"],
            bodies[0].GetProperty("questions").EnumerateObject().Select(p => p.Name));
        Assert.True(bodies[0].GetProperty("state").GetProperty("messages").GetArrayLength() == 2 &&
            bodies[0].GetProperty("model").GetString() == "typesafe/jev-1.14", "Batch state and model");
        Assert.True(results[0].Succeeded && results[0].GetAnswer<NoulAnswer>(Messages[1], "angry").Noul == 0.25, "Per-item answer");
        Assert.True(!results[1].Succeeded && results[1].Error is DecisionsApiException, "Failed batch is reported");
        Assert.True(results[2].Succeeded, "Later batches continue after a failure");
        var error = Assert.Throws<InvalidOperationException>(() => results[1].GetAnswer<NoulAnswer>(Messages[2], "refund"));
        Assert.IsType<DecisionsApiException>(error.InnerException);
    }

    [Fact]
    public async Task FailurePropagatesWhenContinueOnErrorIsOffAsync()
        => await Assert.ThrowsAsync<DecisionsApiException>(async () =>
        {
            await foreach (var _ in FailingOnThirdMessage().EvaluateBatchesAsync(
                Messages, Options(continueOnError: false), TestCancellation)) { }
        });

    [Fact]
    public async Task DuplicateItemIdsAreRejectedAsync()
        => await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in Answering().EvaluateBatchesAsync(
                new[] { Messages[0], Messages[0] }, Options(), TestCancellation)) { }
        });

    [Fact]
    public async Task CancellationStopsFurtherBatchesAsync()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        var seen = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in Answering().EvaluateBatchesAsync(Messages, Options(), cancel.Token))
            {
                seen++;
                cancel.Cancel();
            }
        });
        Assert.Equal(1, seen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreCancelledBatchingThrowsWithoutEnumeratingItemsAsync(bool lazy)
    {
        var enumerated = 0;
        IEnumerable<Message> Lazy() { enumerated++; yield return Messages[0]; }
        var source = lazy ? Lazy() : Array.Empty<Message>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in Answering().EvaluateBatchesAsync(source, Options(), new CancellationToken(canceled: true))) { }
        });
        Assert.Equal(0, enumerated);
    }

    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    private static FakeClient Answering() => new(request => Answer(request.Questions.Keys));

    private static FakeClient FailingOnThirdMessage(List<JsonElement>? bodies = null) => new(request =>
    {
        bodies?.Add(JsonSerializer.SerializeToElement(request));
        if (request.Questions.ContainsKey("m3_refund"))
            throw new DecisionsApiException(HttpStatusCode.BadRequest, "too big", "OpenRouter");
        return Answer(request.Questions.Keys);
    });

    private static DecisionsResponse Answer(IEnumerable<string> ids) => new()
    {
        Model = "jev-1.14.0",
        Answers = ids.ToDictionary(id => id, id => (DecisionsAnswer)new NoulAnswer { Noul = id.EndsWith("angry") ? 0.25 : 0.75 }),
        Usage = new DecisionsUsage { InputTokens = 1, OutputTokens = 1 }
    };

    private sealed class FakeClient(Func<DecisionsRequest, DecisionsResponse> respond) : IDecisionsClient
    {
        public Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(respond(request));
    }
}
