using System.Diagnostics;
using System.Net;
using System.Text.Json;
using DecisionsDotNet;

namespace JevDotNet.Demo.Services.MothGarden;

/// <summary>A moth's last answered decision, and whether it arrived before the moth reached the junction.</summary>
public sealed record MothDecisionRecord(MothDecisionPoint Point, DecisionChoice<Heading> Choice, bool InTime, TimeSpan Latency);

/// <summary>
/// Asks a decision model which way each moth should turn at its next junction, at most one request per moth at a time.
/// The game never waits: a moth that reaches its junction before the answer turns on reflex, and the late answer is dropped.
/// </summary>
/// <param name="game">The game the moths belong to.</param>
/// <param name="apply">Runs a result on the game's thread. The page passes InvokeAsync so answers land between ticks.</param>
/// <param name="maxRequests">Stops asking after this many requests, to bound the cost of one game.</param>
public sealed class MothDirector(MothGardenGame game, Func<Action, Task> apply, int maxRequests = MothDirector.DefaultMaxRequests)
{
    public const int DefaultMaxRequests = 600;

    private readonly Dictionary<Moth, int> _askedTicket = [];
    private readonly HashSet<Moth> _inFlight = [];
    private readonly Dictionary<Moth, MothDecisionRecord> _last = [];
    private double _latencyMs;

    public int MaxRequests => maxRequests;
    public int Requests { get; private set; }
    public int Answered { get; private set; }
    public int InTime { get; private set; }
    public int Late => Answered - InTime;
    public int Failures { get; private set; }
    public int InputTokens { get; private set; }
    public int OutputTokens { get; private set; }
    public decimal? Cost { get; private set; }
    public double AverageLatencyMs => Answered == 0 ? 0 : _latencyMs / Answered;
    public string? LastError { get; private set; }

    /// <summary>True once the provider refused the key or credits, or the request cap was reached. The moths keep their reflexes.</summary>
    public bool Halted { get; private set; }

    public bool IsDeciding(Moth moth) => _inFlight.Contains(moth);
    public MothDecisionRecord? LastDecision(Moth moth) => _last.GetValueOrDefault(moth);

    /// <summary>Starts a request for every idle moth whose next junction has not been asked about yet. Call it once per tick.</summary>
    public IReadOnlyList<Task> Dispatch(DecisionModel model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        var started = new List<Task>();
        foreach (var moth in game.Moths)
        {
            if (Halted) break;
            if (_inFlight.Contains(moth) || _askedTicket.GetValueOrDefault(moth, -1) == moth.Ticket) continue;
            var point = game.NextDecision(moth);
            if (point is null) continue;
            if (Requests >= maxRequests)
            {
                Halted = true;
                LastError = $"This game reached its {maxRequests}-request limit. The moths fly on reflex for the rest of it.";
                break;
            }
            _askedTicket[moth] = point.Ticket;
            _inFlight.Add(moth);
            Requests++;
            started.Add(DecideAsync(model, point, cancellationToken));
        }
        return started;
    }

    /// <summary>One moth's question: its next junction as the state, and only the turns open there as options.</summary>
    public static Task<DecisionChoice<Heading>> ChooseAsync(DecisionModel model, MothDecisionPoint point, CancellationToken cancellationToken = default)
        => model.ChooseAsync(point.State, point.Question,
            point.Options.ToDictionary(option => option.Key, option => (DecisionsValue?)option.Value), cancellationToken);

    private async Task DecideAsync(DecisionModel model, MothDecisionPoint point, CancellationToken cancellationToken)
    {
        var moth = point.Moth;
        var started = Stopwatch.GetTimestamp();
        try
        {
            var choice = await ChooseAsync(model, point, cancellationToken);
            var latency = Stopwatch.GetElapsedTime(started);
            await apply(() =>
            {
                _inFlight.Remove(moth);
                if (cancellationToken.IsCancellationRequested) return;
                var inTime = game.SubmitTurn(moth, point.Ticket, choice.Value);
                Answered++;
                if (inTime) InTime++;
                _latencyMs += latency.TotalMilliseconds;
                InputTokens += choice.Response.Usage.InputTokens;
                OutputTokens += choice.Response.Usage.OutputTokens;
                if (choice.Response.Usage.Cost is { } cost) Cost = (Cost ?? 0) + cost;
                _last[moth] = new MothDecisionRecord(point, choice, inTime, latency);
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await apply(() => _inFlight.Remove(moth));
        }
        catch (Exception error) when (error is DecisionsApiException or DecisionRefusedException or HttpRequestException
            or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            await apply(() =>
            {
                _inFlight.Remove(moth);
                Failures++;
                LastError = Describe(error);
                if (error is DecisionsApiException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired }) Halted = true;
            });
        }
    }

    private static string Describe(Exception error) => error switch
    {
        DecisionsApiException { StatusCode: HttpStatusCode.Unauthorized } => "OpenRouter rejected the API key. The moths fly on reflex.",
        DecisionsApiException { StatusCode: HttpStatusCode.PaymentRequired } => "OpenRouter reports insufficient credits. The moths fly on reflex.",
        DecisionsApiException { StatusCode: HttpStatusCode.TooManyRequests } => "OpenRouter's rate limit was reached. Moths fly on reflex until requests succeed again.",
        DecisionsApiException api => $"OpenRouter returned HTTP {(int)api.StatusCode}. That moth turned on reflex.",
        DecisionRefusedException => "The model declined to pick a turn. That moth turned on reflex.",
        _ => $"{error.Message} That moth turned on reflex."
    };
}
