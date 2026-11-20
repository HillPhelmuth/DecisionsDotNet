using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DecisionsDotNet.Pipeline;

/// <summary>
/// Writes a structured log entry for every evaluation: model, question IDs, elapsed time, tokens, refusals, and failures.
/// Answers are logged at Debug level. State and instructions are never logged, since they can hold personal data.
/// </summary>
[Experimental(DecisionsDiagnostics.Conditions, UrlFormat = DecisionsDiagnostics.UrlFormat)]
public sealed class LoggingDecisionsClient : DelegatingDecisionsClient
{
    private static readonly Action<ILogger, string, string, double, int, int, int, Exception?> Completed =
        LoggerMessage.Define<string, string, double, int, int, int>(LogLevel.Information, new EventId(1, "DecisionsEvaluated"),
            "Decisions evaluated by {Model} for questions {QuestionIds} in {ElapsedMs:F0} ms: {InputTokens} input and {OutputTokens} output tokens, {Refusals} refusals");

    private static readonly Action<ILogger, string, string, Exception?> Answers =
        LoggerMessage.Define<string, string>(LogLevel.Debug, new EventId(2, "DecisionsAnswers"),
            "Decisions answers from {Model}: {Answers}");

    private static readonly Action<ILogger, string, string, double, Exception?> Failed =
        LoggerMessage.Define<string, string, double>(LogLevel.Warning, new EventId(3, "DecisionsFailed"),
            "Decisions evaluation with {Model} for questions {QuestionIds} failed after {ElapsedMs:F0} ms");

    private readonly ILogger _logger;

    /// <summary>Logs calls to <paramref name="innerClient"/> through <paramref name="logger"/>.</summary>
    public LoggingDecisionsClient(IDecisionsClient innerClient, ILogger logger) : base(innerClient)
        => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public override async Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = Stopwatch.GetTimestamp();
        var ids = string.Join(",", request.Questions.Keys);
        try
        {
            var response = await InnerClient.EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (_logger.IsEnabled(LogLevel.Information))
                Completed(_logger, response.Model, ids, elapsed, response.Usage.InputTokens, response.Usage.OutputTokens,
                    response.Answers.Values.Count(answer => answer is RefusalAnswer), null);
            if (_logger.IsEnabled(LogLevel.Debug))
                Answers(_logger, response.Model, string.Join("; ", response.Answers.Select(a => $"{a.Key}={AnswerSummary.Describe(a.Value)}")), null);
            return response;
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Failed(_logger, request.Model, ids, Stopwatch.GetElapsedTime(started).TotalMilliseconds, error);
            throw;
        }
    }
}

/// <summary>
/// Records an <see cref="Activity"/> and metrics for every evaluation, so traces show which answers drove a branch.
/// Spans carry the model, question IDs, each answer, token usage, and cost; never the state or instructions.
/// </summary>
/// <remarks>
/// Subscribe with <c>AddSource("DecisionsDotNet")</c> and <c>AddMeter("DecisionsDotNet")</c>, or the source name you pass.
/// Metrics: <c>decisions.client.duration</c> (s), <c>decisions.client.tokens</c> (tagged <c>decisions.token.type</c>),
/// <c>decisions.client.cost</c>, and <c>decisions.client.refusals</c>.
/// </remarks>
[Experimental(DecisionsDiagnostics.Conditions, UrlFormat = DecisionsDiagnostics.UrlFormat)]
public sealed class OpenTelemetryDecisionsClient : DelegatingDecisionsClient
{
    /// <summary>The default <see cref="ActivitySource"/> and <see cref="Meter"/> name.</summary>
    public const string DefaultSourceName = "DecisionsDotNet";

    private static readonly ConcurrentDictionary<string, Instruments> Shared = new(StringComparer.Ordinal);
    private readonly Instruments _instruments;

    /// <summary>Instruments calls to <paramref name="innerClient"/>.</summary>
    /// <param name="innerClient">The client to instrument.</param>
    /// <param name="sourceName">The activity source and meter name. Defaults to <see cref="DefaultSourceName"/>.</param>
    public OpenTelemetryDecisionsClient(IDecisionsClient innerClient, string? sourceName = null) : base(innerClient)
    {
        var name = string.IsNullOrWhiteSpace(sourceName) ? DefaultSourceName : sourceName;
        // One source and meter per name for the process: the pipeline is often transient, and instruments must outlive it.
        _instruments = Shared.GetOrAdd(name, static n => new Instruments(n));
    }

    /// <inheritdoc />
    public override async Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var activity = _instruments.Source.StartActivity("decisions.evaluate", ActivityKind.Client);
        activity?.SetTag("decisions.request.model", request.Model);
        activity?.SetTag("decisions.question.count", request.Questions.Count);
        activity?.SetTag("decisions.question.ids", string.Join(",", request.Questions.Keys));
        var started = Stopwatch.GetTimestamp();
        var tags = new TagList { { "decisions.request.model", request.Model } };
        try
        {
            var response = await InnerClient.EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
            tags.Add("decisions.response.model", response.Model);
            tags.Add("decisions.outcome", "success");
            var refusals = response.Answers.Values.Count(answer => answer is RefusalAnswer);
            if (activity is not null)
            {
                activity.SetTag("decisions.response.model", response.Model);
                activity.SetTag("decisions.provider", response.Provider);
                activity.SetTag("decisions.response.id", response.Id);
                activity.SetTag("decisions.usage.input_tokens", response.Usage.InputTokens);
                activity.SetTag("decisions.usage.output_tokens", response.Usage.OutputTokens);
                if (response.Usage.Cost is { } cost) activity.SetTag("decisions.usage.cost", (double)cost);
                activity.SetTag("decisions.refusals", refusals);
                foreach (var (id, answer) in response.Answers)
                    activity.SetTag($"decisions.answer.{id}", AnswerSummary.Describe(answer));
            }
            _instruments.Tokens.Add(response.Usage.InputTokens, Append(tags, "decisions.token.type", "input"));
            _instruments.Tokens.Add(response.Usage.OutputTokens, Append(tags, "decisions.token.type", "output"));
            if (response.Usage.Cost is { } spent) _instruments.Cost.Add((double)spent, tags);
            if (refusals > 0) _instruments.Refusals.Add(refusals, tags);
            return response;
        }
        catch (Exception error)
        {
            tags.Add("decisions.outcome", error is OperationCanceledException ? "canceled" : "error");
            tags.Add("error.type", error.GetType().FullName);
            activity?.SetStatus(ActivityStatusCode.Error, error.Message);
            activity?.SetTag("error.type", error.GetType().FullName);
            throw;
        }
        finally
        {
            _instruments.Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
        }
    }

    private sealed class Instruments
    {
        public Instruments(string name)
        {
            Source = new ActivitySource(name);
            var meter = new Meter(name);
            Duration = meter.CreateHistogram<double>("decisions.client.duration", "s", "Duration of Decisions evaluations.");
            Tokens = meter.CreateCounter<long>("decisions.client.tokens", "{token}", "Tokens billed for Decisions evaluations.");
            Cost = meter.CreateCounter<double>("decisions.client.cost", null, "Cost reported by the provider for Decisions evaluations.");
            Refusals = meter.CreateCounter<long>("decisions.client.refusals", "{answer}", "Questions the model refused to answer.");
        }

        public ActivitySource Source { get; }
        public Histogram<double> Duration { get; }
        public Counter<long> Tokens { get; }
        public Counter<double> Cost { get; }
        public Counter<long> Refusals { get; }
    }

    private static TagList Append(TagList tags, string key, object value)
    {
        var copy = tags;
        copy.Add(key, value);
        return copy;
    }
}

/// <summary>Compact, culture-invariant descriptions of answers for logs and span tags.</summary>
internal static class AnswerSummary
{
    public static string Describe(DecisionsAnswer answer) => answer switch
    {
        NoulAnswer noul => $"noul:{Format(noul.Noul)}",
        ChoiceAnswer choice => $"choice:{choice.Choice}@{Format(choice.Probabilities.GetValueOrDefault(choice.Choice, choice.Confidence))}",
        ScoreAnswer score => $"score:{Format(score.Score)}",
        BoxAnswer box => $"box:{Format(box.Confidence)}",
        RefusalAnswer => "refusal",
        _ => answer.GetType().Name,
    };

    private static string Format(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}

public static partial class DecisionsClientBuilderExtensions
{
    /// <summary>Logs every evaluation. Uses the <see cref="ILoggerFactory"/> from the service provider unless one is passed.</summary>
    public static DecisionsClientBuilder UseLogging(this DecisionsClientBuilder builder, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Use((client, services) =>
        {
            var factory = loggerFactory ?? services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
            return new LoggingDecisionsClient(client, factory.CreateLogger(typeof(LoggingDecisionsClient)));
        });
    }

    /// <summary>Records traces and metrics for every evaluation under <paramref name="sourceName"/>, by default <c>DecisionsDotNet</c>.</summary>
    public static DecisionsClientBuilder UseOpenTelemetry(this DecisionsClientBuilder builder, string? sourceName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Use(client => new OpenTelemetryDecisionsClient(client, sourceName));
    }
}
