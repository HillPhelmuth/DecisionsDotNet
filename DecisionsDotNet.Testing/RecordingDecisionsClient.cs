using System.Diagnostics.CodeAnalysis;

namespace DecisionsDotNet.Testing;

/// <summary>One call recorded by <see cref="RecordingDecisionsClient"/>.</summary>
/// <param name="Request">The request sent.</param>
/// <param name="CancellationToken">The token the caller passed.</param>
/// <param name="Response">The response, or null when the call failed.</param>
/// <param name="Error">The exception the call threw, or null when it succeeded.</param>
[Experimental(TestingDiagnostics.Conditions, UrlFormat = TestingDiagnostics.UrlFormat)]
public sealed record RecordedEvaluation(DecisionsRequest Request, CancellationToken CancellationToken,
    DecisionsResponse? Response, Exception? Error);

/// <summary>
/// Wraps any <see cref="IDecisionsClient"/>, real or fake, and records every call, so tests can assert how many requests
/// were sent and what they held.
/// </summary>
[Experimental(TestingDiagnostics.Conditions, UrlFormat = TestingDiagnostics.UrlFormat)]
public sealed class RecordingDecisionsClient : IDecisionsClient
{
    private readonly IDecisionsClient _inner;
    private readonly List<RecordedEvaluation> _evaluations = [];
    private readonly Lock _gate = new();

    /// <summary>Records calls to <paramref name="inner"/>.</summary>
    public RecordingDecisionsClient(IDecisionsClient inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>Every completed call, in the order it finished.</summary>
    public IReadOnlyList<RecordedEvaluation> Evaluations
    {
        get { lock (_gate) return _evaluations.ToArray(); }
    }

    /// <summary>The requests of every completed call, in the order they finished.</summary>
    public IReadOnlyList<DecisionsRequest> Requests => Evaluations.Select(evaluation => evaluation.Request).ToArray();

    /// <summary>The number of completed calls.</summary>
    public int Count
    {
        get { lock (_gate) return _evaluations.Count; }
    }

    /// <summary>Forgets the calls recorded so far.</summary>
    public void Clear()
    {
        lock (_gate) _evaluations.Clear();
    }

    /// <inheritdoc />
    public async Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _inner.EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
            Record(new(request, cancellationToken, response, null));
            return response;
        }
        catch (Exception error)
        {
            Record(new(request, cancellationToken, null, error));
            throw;
        }
    }

    private void Record(RecordedEvaluation evaluation)
    {
        lock (_gate) _evaluations.Add(evaluation);
    }
}
