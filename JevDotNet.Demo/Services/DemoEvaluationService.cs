using DecisionsDotNet;
using DecisionsDotNet.Pipeline;

namespace JevDotNet.Demo.Services;

public sealed class DemoEvaluationService(IConfiguration configuration, IDecisionsClient? decisionsClient = null)
{
    public bool HasApiKey => decisionsClient is not null &&
        !string.IsNullOrWhiteSpace(configuration["OpenRouter:ApiKey"]);

    public Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
        => RequireClient().EvaluateAsync(request, cancellationToken);

    /// <summary>
    /// A <see cref="DecisionModel"/> for one of the available models. The pages show the wire request next to the answer,
    /// so <paramref name="onRequest"/> sees each request the verbs build before it is sent.
    /// </summary>
    public DecisionModel ForModel(string modelId, Action<DecisionsRequest>? onRequest = null)
    {
        var client = RequireClient();
        modelId = DemoDecisionModels.RequireSupported(modelId);
        return (onRequest is null ? client : new RequestObserver(client, onRequest)).ForModel(modelId);
    }

    private IDecisionsClient RequireClient() => HasApiKey ? decisionsClient!
        : throw new InvalidOperationException("Configure the OpenRouter:ApiKey user secret before evaluating.");

    private sealed class RequestObserver(IDecisionsClient inner, Action<DecisionsRequest> onRequest) : DelegatingDecisionsClient(inner)
    {
        public override Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
        {
            onRequest(request);
            return base.EvaluateAsync(request, cancellationToken);
        }
    }
}
