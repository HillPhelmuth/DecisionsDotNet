using System.Net;
using System.Net.Http.Headers;
using DecisionsDotNet;
using ExtractionsDotNet;

/// <summary>
/// How long the clients wait between attempts. Both SDKs share the same rules, so every case runs against each.
/// </summary>
public sealed class RetryTimingChecks
{
    public enum Sdk { Decisions, Extractions }

    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(Sdk.Decisions)]
    [InlineData(Sdk.Extractions)]
    public async Task BackoffDoublesFromTheInitialDelayUpToTheCapAsync(Sdk sdk)
    {
        var clock = new RecordingClock();
        var attempts = await RunAsync(sdk, clock, _ => Busy(), maxAttempts: 5,
            initialDelay: TimeSpan.FromSeconds(2), maxDelay: TimeSpan.FromSeconds(10));

        Assert.Equal(5, attempts);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(10)], clock.Delays);
    }

    [Theory]
    [InlineData(Sdk.Decisions)]
    [InlineData(Sdk.Extractions)]
    public async Task RetryAfterSecondsReplaceTheBackoffAsync(Sdk sdk)
    {
        var clock = new RecordingClock();
        await RunAsync(sdk, clock, _ => Busy(new RetryConditionHeaderValue(TimeSpan.FromSeconds(7))), maxAttempts: 2);

        Assert.Equal([TimeSpan.FromSeconds(7)], clock.Delays);
    }

    [Theory]
    [InlineData(Sdk.Decisions)]
    [InlineData(Sdk.Extractions)]
    public async Task RetryAfterDateIsMeasuredFromNowAsync(Sdk sdk)
    {
        var clock = new RecordingClock();
        await RunAsync(sdk, clock, _ => Busy(new RetryConditionHeaderValue(clock.Now.AddSeconds(9))), maxAttempts: 2);

        Assert.Equal([TimeSpan.FromSeconds(9)], clock.Delays);
    }

    [Theory]
    [InlineData(Sdk.Decisions)]
    [InlineData(Sdk.Extractions)]
    public async Task RetryAfterDateInThePastRetriesAtOnceAsync(Sdk sdk)
    {
        var clock = new RecordingClock();
        var attempts = await RunAsync(sdk, clock, _ => Busy(new RetryConditionHeaderValue(clock.Now.AddMinutes(-5))), maxAttempts: 2);

        Assert.Equal(2, attempts);
        Assert.Empty(clock.Delays);
    }

    [Theory]
    [InlineData(Sdk.Decisions, false)]
    [InlineData(Sdk.Decisions, true)]
    [InlineData(Sdk.Extractions, false)]
    [InlineData(Sdk.Extractions, true)]
    public async Task RetryAfterIsCappedAtTheMaximumDelayAsync(Sdk sdk, bool asDate)
    {
        var clock = new RecordingClock();
        var retryAfter = asDate
            ? new RetryConditionHeaderValue(clock.Now.AddHours(1))
            : new RetryConditionHeaderValue(TimeSpan.FromHours(1));
        await RunAsync(sdk, clock, _ => Busy(retryAfter), maxAttempts: 2);

        Assert.Equal([Cap], clock.Delays);
    }

    [Theory]
    [InlineData(Sdk.Decisions)]
    [InlineData(Sdk.Extractions)]
    public async Task CancellingDuringBackoffStopsWithoutAnotherAttemptAsync(Sdk sdk)
    {
        var clock = new RecordingClock { HoldDelays = true };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var attempts = 0;
        var call = SendAsync(sdk, clock, _ => { attempts++; return Busy(); }, 3, TimeSpan.FromSeconds(1), Cap, cancellation.Token);

        await clock.DelayStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(call.IsCompleted);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            call.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(1, attempts);
    }

    /// <summary>Runs one call that fails every attempt and returns how many attempts were made.</summary>
    private static async Task<int> RunAsync(Sdk sdk, RecordingClock clock, Func<int, HttpResponseMessage> respond,
        int maxAttempts, TimeSpan? initialDelay = null, TimeSpan? maxDelay = null)
    {
        var attempts = 0;
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => SendAsync(sdk, clock, _ => respond(++attempts), maxAttempts,
            initialDelay ?? TimeSpan.FromSeconds(1), maxDelay ?? Cap, TestContext.Current.CancellationToken));
        return attempts;
    }

    private static async Task SendAsync(Sdk sdk, RecordingClock clock, Func<HttpRequestMessage, HttpResponseMessage> respond,
        int maxAttempts, TimeSpan initialDelay, TimeSpan maxDelay, CancellationToken cancellationToken)
    {
        using var http = new HttpClient(new StubHandler(respond));
        if (sdk == Sdk.Decisions)
        {
            using var client = new TypeSafeDecisionsClient("key", http, new TypeSafeDecisionsClientOptions
            {
                MaxAttempts = maxAttempts, InitialRetryDelay = initialDelay, MaxRetryDelay = maxDelay, Clock = clock
            });
            await client.EvaluateAsync(new DecisionsRequest("state",
                new Dictionary<string, DecisionsQuestion> { ["ok"] = new NoulQuestion("OK?") },
                TypeSafeDecisionsClient.DefaultModel), cancellationToken);
        }
        else
        {
            using var client = new OpenRouterExtractionsClient("key", http, new OpenRouterExtractionsClientOptions
            {
                MaxAttempts = maxAttempts, InitialRetryDelay = initialDelay, MaxRetryDelay = maxDelay, Clock = clock
            });
            await client.ExtractAsync(new ExtractionRequest("cloudflare/clairic",
                new Dictionary<string, ExtractionField> { ["value"] = new TextField() }) { State = "Invoice" }, cancellationToken);
        }
    }

    private static HttpResponseMessage Busy(RetryConditionHeaderValue? retryAfter = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("busy") };
        response.Headers.RetryAfter = retryAfter;
        return response;
    }
}
