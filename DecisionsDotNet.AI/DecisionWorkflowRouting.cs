using System.Diagnostics.CodeAnalysis;

namespace DecisionsDotNet.AI;

/// <summary>A workflow message routed by a calibrated choice. Use <see cref="When"/> as an edge condition.</summary>
/// <typeparam name="TMessage">The message being routed.</typeparam>
/// <typeparam name="TEnum">The routes.</typeparam>
/// <param name="Message">The original message, to pass on to the next step.</param>
/// <param name="Route">The route to take: the choice, or the low-confidence route when the choice was not confident enough.</param>
/// <param name="Choice">The choice and its evidence.</param>
[Experimental(AIDiagnostics.Conditions, UrlFormat = AIDiagnostics.UrlFormat)]
public sealed record DecisionRoute<TMessage, TEnum>(TMessage Message, TEnum Route, DecisionChoice<TEnum> Choice)
    where TEnum : struct, Enum
{
    /// <summary>An edge condition that is true for messages routed to <paramref name="route"/>, such as for Agent Framework's <c>AddEdge</c>.</summary>
    public static Func<DecisionRoute<TMessage, TEnum>?, bool> When(TEnum route)
        => routed => routed is not null && EqualityComparer<TEnum>.Default.Equals(routed.Route, route);
}

/// <summary>A workflow message gated by a three-valued verdict. Use <see cref="When"/> as an edge condition.</summary>
/// <typeparam name="TMessage">The message being gated.</typeparam>
/// <param name="Message">The original message, to pass on to the next step.</param>
/// <param name="Verdict">The verdict and its evidence.</param>
[Experimental(AIDiagnostics.Conditions, UrlFormat = AIDiagnostics.UrlFormat)]
public sealed record DecisionGate<TMessage>(TMessage Message, DecisionVerdict Verdict)
{
    /// <summary>An edge condition that is true for messages whose verdict is <paramref name="outcome"/>.</summary>
    public static Func<DecisionGate<TMessage>?, bool> When(VerdictOutcome outcome)
        => gated => gated is not null && gated.Verdict.Outcome == outcome;
}

/// <summary>
/// Turns <see cref="DecisionModel"/> verbs into workflow steps. Workflow edge conditions are synchronous, so the model call runs
/// once in a step that emits a <see cref="DecisionRoute{TMessage, TEnum}"/> or <see cref="DecisionGate{TMessage}"/>, and the edges
/// branch on it with <c>When</c>. The branch follows a calibrated probability rather than a routing word in generated text.
/// </summary>
/// <example>
/// <code>
/// var route = model.Router&lt;Ticket, Team&gt;(t => t.Body, "Which team should handle this?", minProbability: 0.6, lowConfidenceRoute: Team.Other);
/// var classify = new FunctionExecutor&lt;Ticket, DecisionRoute&lt;Ticket, Team&gt;&gt;("classify", (ticket, _, ct) => route(ticket, ct));
/// builder.AddEdge(classify, billing, DecisionRoute&lt;Ticket, Team&gt;.When(Team.Billing))
///        .AddEdge(classify, triage, DecisionRoute&lt;Ticket, Team&gt;.When(Team.Other));
/// </code>
/// </example>
[Experimental(AIDiagnostics.Conditions, UrlFormat = AIDiagnostics.UrlFormat)]
public static class DecisionWorkflowExtensions
{
    /// <summary>
    /// Creates a step that chooses a member of <typeparamref name="TEnum"/> for each message, and routes to
    /// <paramref name="lowConfidenceRoute"/> when the chosen option's probability is below <paramref name="minProbability"/>.
    /// </summary>
    public static Func<TMessage, CancellationToken, ValueTask<DecisionRoute<TMessage, TEnum>>> Router<TMessage,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum>(this DecisionModel model,
        Func<TMessage, DecisionsValue> selector, DecisionsValue instructions, double minProbability, TEnum lowConfidenceRoute)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(instructions);
        if (!double.IsFinite(minProbability) || minProbability is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(minProbability), minProbability, "A probability threshold must be a finite value from 0 to 1.");
        return async (message, cancellationToken) =>
        {
            var choice = await model.ChooseAsync<TEnum>(selector(message), instructions, cancellationToken).ConfigureAwait(false);
            return new(message, choice.SelectedProbability >= minProbability ? choice.Value : lowConfidenceRoute, choice);
        };
    }

    /// <summary>Creates a step that answers a yes/no question about each message as Yes, No, Unsure, or Refused.</summary>
    public static Func<TMessage, CancellationToken, ValueTask<DecisionGate<TMessage>>> Gate<TMessage>(this DecisionModel model,
        Func<TMessage, DecisionsValue> selector, DecisionsValue instructions, (double Low, double High) unsureBand)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(instructions);
        if (!double.IsFinite(unsureBand.Low) || !double.IsFinite(unsureBand.High) || unsureBand.Low < 0 || unsureBand.High > 1 ||
            unsureBand.Low > unsureBand.High)
            throw new ArgumentOutOfRangeException(nameof(unsureBand), unsureBand, "The unsure band must satisfy 0 <= low <= high <= 1.");
        return async (message, cancellationToken) =>
            new(message, await model.VerdictAsync(selector(message), instructions, unsureBand, cancellationToken: cancellationToken)
                .ConfigureAwait(false));
    }
}
