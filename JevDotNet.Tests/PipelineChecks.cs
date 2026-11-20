using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using DecisionsDotNet;
using DecisionsDotNet.Caching;
using DecisionsDotNet.Pipeline;
using DecisionsDotNet.Testing;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public sealed class PipelineChecks
{
    private static DecisionsRequest Request(string state = "Charged twice", string model = "typesafe/jev-1.14")
        => DecisionsRequest.Create(state).Noul("refund", "Wants a refund?").Choice("team", "Team?", "billing", "other").Build(model);

    private static FakeDecisionsClient Answers() => new FakeDecisionsClient().AnswerNoul(0.9).AnswerChoice("billing", 0.8);

    [Fact]
    public async Task FirstUseIsOutermostAsync()
    {
        var order = new List<string>();
        var client = new DecisionsClientBuilder(Answers())
            .Use(inner => new Tracing(inner, "outer", order))
            .Use(inner => new Tracing(inner, "inner", order))
            .Build();
        await client.EvaluateAsync(Request());
        Assert.Equal(["outer", "inner"], order);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public async Task FallbackOnlyForTransientProviderErrorsAsync(HttpStatusCode status, bool fallsBack)
    {
        var primary = new FakeDecisionsClient().Throw(new DecisionsApiException(status, "{}"));
        var fallback = new RecordingDecisionsClient(Answers());
        var client = new DecisionsClientBuilder(primary)
            .UseFallback(fallback, model => model == "typesafe/jev-1.14" ? "jev-1.14.0" : model)
            .Build();

        if (fallsBack)
        {
            var response = await client.EvaluateAsync(Request());
            Assert.Equal("jev-1.14.0", response.Model);
            Assert.Equal("jev-1.14.0", Assert.Single(fallback.Requests).Model);
            Assert.Equal(Request().Questions.Keys, fallback.Requests[0].Questions.Keys);
        }
        else
        {
            var error = await Assert.ThrowsAsync<DecisionsApiException>(() => client.EvaluateAsync(Request()));
            Assert.Equal(status, error.StatusCode);
            Assert.Equal(0, fallback.Count);
        }
    }

    [Fact]
    public void TransientClassificationCoversNetworkAndMalformedResponses()
    {
        Assert.True(FallbackDecisionsClient.IsTransient(new HttpRequestException("socket")));
        Assert.True(FallbackDecisionsClient.IsTransient(new TimeoutException()));
        Assert.True(FallbackDecisionsClient.IsTransient(new TaskCanceledException()));
        Assert.True(FallbackDecisionsClient.IsTransient(new JsonException()));
        Assert.False(FallbackDecisionsClient.IsTransient(new HttpRequestException("no", null, HttpStatusCode.NotFound)));
        Assert.False(FallbackDecisionsClient.IsTransient(new ArgumentException()));
        Assert.False(FallbackDecisionsClient.IsTransient(new DecisionRefusedException("q", new DecisionsResponse
        {
            Model = "m", Answers = new Dictionary<string, DecisionsAnswer>(), Usage = new DecisionsUsage { InputTokens = 0, OutputTokens = 0 },
        })));
    }

    [Fact]
    public async Task CallerCancellationNeverFallsBackAsync()
    {
        using var cts = new CancellationTokenSource();
        var primary = new Cancelling(cts);
        var fallback = new RecordingDecisionsClient(Answers());
        var client = new DecisionsClientBuilder(primary).UseFallback(fallback).Build();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.EvaluateAsync(Request(), cts.Token));
        Assert.Equal(0, fallback.Count);
    }

    [Fact]
    public async Task PipelinesRegisterFromDependencyInjectionAsync()
    {
        var services = new ServiceCollection();
        var primary = new RecordingDecisionsClient(new FakeDecisionsClient().Throw(new DecisionsApiException(HttpStatusCode.ServiceUnavailable, "{}")));
        var backup = new RecordingDecisionsClient(Answers());
        services.AddKeyedSingleton<IDecisionsClient>("openrouter", primary);
        services.AddKeyedSingleton<IDecisionsClient>("typesafe", backup);
        services.AddDecisionsClient(sp => sp.GetRequiredKeyedService<IDecisionsClient>("openrouter"))
            .UseFallback("typesafe")
            .UseLogging()
            .UseOpenTelemetry();
        services.AddKeyedDecisionsClient("plain", sp => sp.GetRequiredKeyedService<IDecisionsClient>("typesafe"));
        services.AddDecisionModel("typesafe/jev-1.14");
        using var provider = services.BuildServiceProvider();

        var model = provider.GetRequiredService<DecisionModel>();
        Assert.True(await model.MatchesAsync("Charged twice", "Wants a refund?", 0.5));
        Assert.Equal(1, primary.Count);
        Assert.Equal(1, backup.Count);
        Assert.IsType<RecordingDecisionsClient>(provider.GetRequiredKeyedService<IDecisionsClient>("plain"));
    }

    [Fact]
    public async Task LoggingRecordsIdsAndUsageButNeverStateAsync()
    {
        var logger = new CapturingLogger();
        var client = new DecisionsClientBuilder(Answers()).UseLogging(new CapturingLoggerFactory(logger)).Build();
        await client.EvaluateAsync(Request("Secret account 1234"));

        var info = Assert.Single(logger.Entries, e => e.Level == LogLevel.Information);
        Assert.Contains("refund,team", info.Message);
        Assert.Contains("typesafe/jev-1.14", info.Message);
        var debug = Assert.Single(logger.Entries, e => e.Level == LogLevel.Debug);
        Assert.Contains("refund=noul:0.9", debug.Message);
        Assert.Contains("team=choice:billing@0.8", debug.Message);
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("Secret") || e.Message.Contains("Wants a refund"));

        var failing = new DecisionsClientBuilder(new FakeDecisionsClient().Throw(new TimeoutException()))
            .UseLogging(new CapturingLoggerFactory(logger)).Build();
        await Assert.ThrowsAsync<TimeoutException>(() => failing.EvaluateAsync(Request()));
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.IsType<TimeoutException>(warning.Error);
    }

    [Fact]
    public async Task OpenTelemetryRecordsSpansAndMetricsAsync()
    {
        const string source = "DecisionsDotNet.Tests.Otel";
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var measurements = new List<(string Name, double Value, Dictionary<string, object?> Tags)>();
        using var meters = new MeterListener();
        meters.InstrumentPublished = (instrument, l) => { if (instrument.Meter.Name == source) l.EnableMeasurementEvents(instrument); };
        meters.SetMeasurementEventCallback<long>((i, v, tags, _) => measurements.Add((i.Name, v, ToDictionary(tags))));
        meters.SetMeasurementEventCallback<double>((i, v, tags, _) => measurements.Add((i.Name, v, ToDictionary(tags))));
        meters.Start();

        var fake = new FakeDecisionsClient().AnswerNoul(0.9).Refuse("team");
        var client = new DecisionsClientBuilder(new WithUsage(fake)).UseOpenTelemetry(source).Build();
        await client.EvaluateAsync(Request());

        var activity = Assert.Single(activities);
        Assert.Equal("decisions.evaluate", activity.OperationName);
        Assert.Equal("refund,team", activity.GetTagItem("decisions.question.ids"));
        Assert.Equal("noul:0.9", activity.GetTagItem("decisions.answer.refund"));
        Assert.Equal("refusal", activity.GetTagItem("decisions.answer.team"));
        Assert.Equal(12, activity.GetTagItem("decisions.usage.input_tokens"));
        Assert.Equal(0.002, activity.GetTagItem("decisions.usage.cost"));

        Assert.Contains(measurements, m => m.Name == "decisions.client.tokens" && m.Value == 12 && (string?)m.Tags["decisions.token.type"] == "input");
        Assert.Contains(measurements, m => m.Name == "decisions.client.refusals" && m.Value == 1);
        Assert.Contains(measurements, m => m.Name == "decisions.client.cost" && Math.Abs(m.Value - 0.002) < 1e-9);
        Assert.Contains(measurements, m => m.Name == "decisions.client.duration" && (string?)m.Tags["decisions.outcome"] == "success");

        var failing = new DecisionsClientBuilder(new FakeDecisionsClient().Throw(new TimeoutException())).UseOpenTelemetry(source).Build();
        await Assert.ThrowsAsync<TimeoutException>(() => failing.EvaluateAsync(Request()));
        Assert.Equal(ActivityStatusCode.Error, activities[^1].Status);
        Assert.Contains(measurements, m => m.Name == "decisions.client.duration" && (string?)m.Tags["decisions.outcome"] == "error");
    }

    [Fact]
    public async Task CachingAnswersIdenticalRequestsOnceAsync()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        var recorder = new RecordingDecisionsClient(Answers());
        services.AddKeyedSingleton<IDecisionsClient>("inner", recorder);
        services.AddDecisionsClient(sp => sp.GetRequiredKeyedService<IDecisionsClient>("inner"), ServiceLifetime.Singleton)
            .UseCaching(TimeSpan.FromMinutes(5));
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IDecisionsClient>();

        var first = await client.EvaluateAsync(Request());
        var second = await client.EvaluateAsync(Request());
        Assert.Equal(1, recorder.Count);
        Assert.Equal(first.GetAnswer<NoulAnswer>("refund").Noul, second.GetAnswer<NoulAnswer>("refund").Noul);
        Assert.Equal("billing", second.GetAnswer<ChoiceAnswer>("team").Choice);

        await client.EvaluateAsync(Request("Different state"));
        await client.EvaluateAsync(Request(model: "other-model"));
        await client.EvaluateAsync(DecisionsRequest.Create("Charged twice").Noul("refund", "Wants money back?")
            .Choice("team", "Team?", "billing", "other").Build("typesafe/jev-1.14"));
        Assert.Equal(4, recorder.Count);

        await provider.GetRequiredService<HybridCache>().RemoveByTagAsync(CachingDecisionsClient.CacheTag);
        await client.EvaluateAsync(Request());
        Assert.Equal(5, recorder.Count);
    }

    [Fact]
    public async Task CachingNeverStoresFailuresAsync()
    {
        var services = new ServiceCollection().AddHybridCache().Services.BuildServiceProvider();
        var flaky = new Flaky(Answers());
        var client = new DecisionsClientBuilder(flaky).UseCaching(cache: services.GetRequiredService<HybridCache>()).Build();

        await Assert.ThrowsAsync<TimeoutException>(() => client.EvaluateAsync(Request()));
        var response = await client.EvaluateAsync(Request());
        Assert.Equal(0.9, response.GetAnswer<NoulAnswer>("refund").Noul);
        Assert.Equal(2, flaky.Calls);
        Assert.StartsWith("decisions:v1:", CachingDecisionsClient.GetCacheKey(Request()));
        Assert.Equal(CachingDecisionsClient.GetCacheKey(Request()), CachingDecisionsClient.GetCacheKey(Request()));
    }

    [Fact]
    public void UseCachingWithoutACacheExplainsWhat()
    {
        var builder = new DecisionsClientBuilder(Answers()).UseCaching();
        var error = Assert.Throws<InvalidOperationException>(() => builder.Build(new ServiceCollection().BuildServiceProvider()));
        Assert.Contains("AddHybridCache", error.Message);
    }

    private static Dictionary<string, object?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var result = new Dictionary<string, object?>();
        foreach (var tag in tags) result[tag.Key] = tag.Value;
        return result;
    }

    private sealed class Tracing(IDecisionsClient inner, string name, List<string> order) : DelegatingDecisionsClient(inner)
    {
        public override Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
        {
            order.Add(name);
            return base.EvaluateAsync(request, cancellationToken);
        }
    }

    private sealed class Cancelling(CancellationTokenSource source) : IDecisionsClient
    {
        public Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
        {
            source.Cancel();
            throw new TaskCanceledException();
        }
    }

    private sealed class Flaky(IDecisionsClient inner) : IDecisionsClient
    {
        public int Calls { get; private set; }

        public Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
            => ++Calls == 1 ? throw new TimeoutException() : inner.EvaluateAsync(request, cancellationToken);
    }

    private sealed class WithUsage(IDecisionsClient inner) : IDecisionsClient
    {
        public async Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
            => await inner.EvaluateAsync(request, cancellationToken) with
            {
                Usage = new DecisionsUsage { InputTokens = 12, OutputTokens = 2, Cost = 0.002m },
            };
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Error);

    private sealed class CapturingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(new(logLevel, formatter(state, exception), exception));
    }

    private sealed class CapturingLoggerFactory(CapturingLogger logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => logger;
        public void Dispose() { }
    }
}
