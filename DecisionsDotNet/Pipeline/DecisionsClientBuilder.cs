using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace DecisionsDotNet.Pipeline;

/// <summary>An <see cref="IDecisionsClient"/> that forwards to another one. Derive from it to add behavior around every call.</summary>
[Experimental(DecisionsDiagnostics.Conditions, UrlFormat = DecisionsDiagnostics.UrlFormat)]
public class DelegatingDecisionsClient : IDecisionsClient, IDisposable
{
    /// <summary>Wraps <paramref name="innerClient"/>.</summary>
    protected DelegatingDecisionsClient(IDecisionsClient innerClient)
        => InnerClient = innerClient ?? throw new ArgumentNullException(nameof(innerClient));

    /// <summary>The client calls are forwarded to.</summary>
    protected IDecisionsClient InnerClient { get; }

    /// <inheritdoc />
    public virtual Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
        => InnerClient.EvaluateAsync(request, cancellationToken);

    /// <summary>Disposes the inner client when it is disposable.</summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Disposes the inner client when <paramref name="disposing"/> is true and it is disposable.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing) (InnerClient as IDisposable)?.Dispose();
    }
}

/// <summary>
/// Composes an <see cref="IDecisionsClient"/> from an inner provider client and decorators such as caching, fallback, logging,
/// and OpenTelemetry, in the style of Microsoft.Extensions.AI's <c>ChatClientBuilder</c>. The first <c>Use</c> call is the outermost.
/// </summary>
/// <example>
/// <code>
/// services.AddDecisionsClient(sp => sp.GetRequiredKeyedService&lt;IDecisionsClient&gt;("openrouter"))
///     .UseFallback("typesafe", model => model.Replace("typesafe/jev-1.14", "jev-1.14.0"))
///     .UseLogging()
///     .UseOpenTelemetry();
/// </code>
/// </example>
[Experimental(DecisionsDiagnostics.Conditions, UrlFormat = DecisionsDiagnostics.UrlFormat)]
public sealed class DecisionsClientBuilder
{
    private readonly Func<IServiceProvider, IDecisionsClient> _inner;
    private readonly List<Func<IDecisionsClient, IServiceProvider, IDecisionsClient>> _decorators = [];

    /// <summary>Starts a pipeline around an existing client.</summary>
    public DecisionsClientBuilder(IDecisionsClient innerClient)
    {
        ArgumentNullException.ThrowIfNull(innerClient);
        _inner = _ => innerClient;
    }

    /// <summary>Starts a pipeline around a client created from the service provider when the pipeline is built.</summary>
    public DecisionsClientBuilder(Func<IServiceProvider, IDecisionsClient> innerClientFactory)
        => _inner = innerClientFactory ?? throw new ArgumentNullException(nameof(innerClientFactory));

    /// <summary>Adds a decorator. Decorators added first run first, wrapping those added after them.</summary>
    public DecisionsClientBuilder Use(Func<IDecisionsClient, IDecisionsClient> decorator)
    {
        ArgumentNullException.ThrowIfNull(decorator);
        return Use((client, _) => decorator(client));
    }

    /// <summary>Adds a decorator that can resolve services. Decorators added first run first.</summary>
    public DecisionsClientBuilder Use(Func<IDecisionsClient, IServiceProvider, IDecisionsClient> decorator)
    {
        ArgumentNullException.ThrowIfNull(decorator);
        _decorators.Add(decorator);
        return this;
    }

    /// <summary>Builds the pipeline. Pass the service provider when a decorator or the inner factory resolves services.</summary>
    public IDecisionsClient Build(IServiceProvider? services = null)
    {
        services ??= EmptyServiceProvider.Instance;
        var client = _inner(services) ?? throw new InvalidOperationException("The inner client factory returned null.");
        for (var i = _decorators.Count - 1; i >= 0; i--)
            client = _decorators[i](client, services)
                ?? throw new InvalidOperationException($"Decorator {i} returned null.");
        return client;
    }

    private sealed class EmptyServiceProvider : IServiceProvider, IKeyedServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();
        public object? GetService(Type serviceType) => null;
        public object? GetKeyedService(Type serviceType, object? serviceKey) => null;
        public object GetRequiredKeyedService(Type serviceType, object? serviceKey)
            => throw new InvalidOperationException($"No service provider was passed to Build, so {serviceType.Name} cannot be resolved.");
    }
}
