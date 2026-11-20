using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace DecisionsDotNet.Pipeline;

/// <summary>
/// Sends a request to a fallback client when the primary fails with a transient or provider error. Authentication, permission,
/// and bad-request errors, and the caller's own cancellation, always propagate.
/// </summary>
[Experimental(DecisionsDiagnostics.Conditions, UrlFormat = DecisionsDiagnostics.UrlFormat)]
public sealed class FallbackDecisionsClient : DelegatingDecisionsClient
{
    private readonly IDecisionsClient _fallback;
    private readonly Func<string, string> _mapModel;
    private readonly Func<Exception, bool> _shouldFallback;

    /// <summary>Wraps <paramref name="primary"/> with a fallback.</summary>
    /// <param name="primary">The client tried first.</param>
    /// <param name="fallback">The client tried when the primary fails.</param>
    /// <param name="mapModel">Translates the request's model ID for the fallback provider, since model IDs are provider specific. Null keeps it.</param>
    /// <param name="shouldFallback">Decides which failures fall back. Null uses <see cref="IsTransient"/>.</param>
    public FallbackDecisionsClient(IDecisionsClient primary, IDecisionsClient fallback,
        Func<string, string>? mapModel = null, Func<Exception, bool>? shouldFallback = null)
        : base(primary)
    {
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _mapModel = mapModel ?? (model => model);
        _shouldFallback = shouldFallback ?? IsTransient;
    }

    /// <inheritdoc />
    public override async Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return await InnerClient.EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && _shouldFallback(error))
        {
            var model = _mapModel(request.Model);
            var retry = model == request.Model ? request : new DecisionsRequest(request.State, request.Questions, model);
            return await _fallback.EvaluateAsync(retry, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// True for failures another provider might not share: HTTP 408, 429 and 5xx, network errors, timeouts, and malformed responses.
    /// False for 400, 401, 403, 404 and other client errors, which another provider would most likely reject too or which signal a configuration problem.
    /// </summary>
    public static bool IsTransient(Exception error) => error switch
    {
        DecisionsApiException api => api.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)api.StatusCode >= 500,
        HttpRequestException http => http.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            || (int)http.StatusCode >= 500,
        TimeoutException or TaskCanceledException or JsonException => true,
        _ => false,
    };
}

/// <summary>Adds the built-in decorators to a <see cref="DecisionsClientBuilder"/>.</summary>
[Experimental(DecisionsDiagnostics.Conditions, UrlFormat = DecisionsDiagnostics.UrlFormat)]
public static partial class DecisionsClientBuilderExtensions
{
    /// <summary>Falls back to the keyed <see cref="IDecisionsClient"/> registered under <paramref name="fallbackServiceKey"/> on transient or provider errors.</summary>
    /// <param name="builder">The pipeline.</param>
    /// <param name="fallbackServiceKey">The key of the fallback provider client.</param>
    /// <param name="mapModel">Translates model IDs for the fallback provider, such as <c>typesafe/jev-1.14</c> on OpenRouter to <c>jev-1.14.0</c> on TypeSafe.</param>
    /// <param name="shouldFallback">Decides which failures fall back. Null uses <see cref="FallbackDecisionsClient.IsTransient"/>.</param>
    public static DecisionsClientBuilder UseFallback(this DecisionsClientBuilder builder, object fallbackServiceKey,
        Func<string, string>? mapModel = null, Func<Exception, bool>? shouldFallback = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(fallbackServiceKey);
        return builder.Use((client, services) => new FallbackDecisionsClient(client,
            services.GetRequiredKeyedService<IDecisionsClient>(fallbackServiceKey), mapModel, shouldFallback));
    }

    /// <summary>Falls back to <paramref name="fallback"/> on transient or provider errors.</summary>
    public static DecisionsClientBuilder UseFallback(this DecisionsClientBuilder builder, IDecisionsClient fallback,
        Func<string, string>? mapModel = null, Func<Exception, bool>? shouldFallback = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(fallback);
        return builder.Use(client => new FallbackDecisionsClient(client, fallback, mapModel, shouldFallback));
    }
}
