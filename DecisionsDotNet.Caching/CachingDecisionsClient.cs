using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using DecisionsDotNet.Pipeline;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace DecisionsDotNet.Caching;

/// <summary>
/// Caches successful evaluations in a <see cref="HybridCache"/>, keyed by a SHA-256 hash of the request's state, questions,
/// criteria, and model. Identical requests are answered from the cache, and concurrent identical requests share one call.
/// </summary>
/// <remarks>
/// A cached response is returned as it was first received, including its <see cref="DecisionsResponse.Usage"/>, so usage on a
/// cache hit does not mean tokens were billed again. Failures are never cached.
/// </remarks>
[Experimental("DECISIONS002", UrlFormat = "https://github.com/HillPhelmuth/JevDotNet/blob/main/CHANGELOG.md#experimental-apis")]
public sealed class CachingDecisionsClient : DelegatingDecisionsClient
{
    /// <summary>The tag added to every cache entry, for <see cref="HybridCache.RemoveByTagAsync(string, CancellationToken)"/>.</summary>
    public const string CacheTag = "decisions";

    private const string KeyPrefix = "decisions:v1:";
    private static readonly string[] Tags = [CacheTag];
    private readonly HybridCache _cache;
    private readonly HybridCacheEntryOptions? _entryOptions;

    /// <summary>Caches calls to <paramref name="innerClient"/> in <paramref name="cache"/>.</summary>
    /// <param name="innerClient">The client to cache.</param>
    /// <param name="cache">The cache.</param>
    /// <param name="expiration">How long entries live. Null uses the cache's default.</param>
    public CachingDecisionsClient(IDecisionsClient innerClient, HybridCache cache, TimeSpan? expiration = null) : base(innerClient)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        if (expiration is { } value && value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(expiration), value, "The expiration must be positive.");
        _entryOptions = expiration is { } lifetime ? new HybridCacheEntryOptions { Expiration = lifetime, LocalCacheExpiration = lifetime } : null;
    }

    /// <summary>Returns the cache key for a request. Requests with the same state, questions in the same order, and model share a key.</summary>
    public static string GetCacheKey(DecisionsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return KeyPrefix + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
    }

    /// <inheritdoc />
    public override async Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var json = await _cache.GetOrCreateAsync(GetCacheKey(request), (InnerClient, request),
            static async (state, token) =>
                JsonSerializer.Serialize(await state.InnerClient.EvaluateAsync(state.request, token).ConfigureAwait(false)),
            _entryOptions, Tags, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<DecisionsResponse>(json)
            ?? throw new JsonException("The cached Decisions response is empty.");
    }
}

/// <summary>Adds <see cref="CachingDecisionsClient"/> to a <see cref="DecisionsClientBuilder"/>.</summary>
[Experimental("DECISIONS002", UrlFormat = "https://github.com/HillPhelmuth/JevDotNet/blob/main/CHANGELOG.md#experimental-apis")]
public static class DecisionsCachingExtensions
{
    /// <summary>
    /// Caches evaluations in the <see cref="HybridCache"/> registered in the service provider (call <c>services.AddHybridCache()</c>),
    /// or in <paramref name="cache"/> when one is passed.
    /// </summary>
    public static DecisionsClientBuilder UseCaching(this DecisionsClientBuilder builder, TimeSpan? expiration = null, HybridCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Use((client, services) => new CachingDecisionsClient(client,
            cache ?? services.GetService<HybridCache>()
                ?? throw new InvalidOperationException("Register a HybridCache with services.AddHybridCache() before UseCaching, or pass one."),
            expiration));
    }
}
