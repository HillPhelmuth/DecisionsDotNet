namespace DecisionsDotNet;

/// <summary>Evaluates typed Decisions requests through a configured provider.</summary>
public interface IDecisionsClient
{
    /// <summary>Sends the request and returns one typed answer per question.</summary>
    Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default);
}
