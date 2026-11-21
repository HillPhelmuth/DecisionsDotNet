using System.Net.Http.Headers;
using Azure.Core;

namespace DecisionsDotNet;

/// <summary>
/// Signs Azure requests with either an <c>api-key</c> header or a Microsoft Entra bearer token. Tokens are cached and
/// refreshed five minutes before they expire, so every attempt of a retried request carries a valid one.
/// </summary>
internal sealed class AzureAuthentication
{
    /// <summary>The scope Azure AI services accept for Microsoft Entra tokens.</summary>
    internal const string DefaultTokenScope = "https://cognitiveservices.azure.com/.default";

    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);
    private readonly string? _apiKey;
    private readonly TokenCredential? _credential;
    private readonly TokenRequestContext _context;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AccessToken? _token;

    private AzureAuthentication(string? apiKey, TokenCredential? credential, string scope, TimeProvider clock)
    {
        _apiKey = apiKey;
        _credential = credential;
        _context = new TokenRequestContext([scope]);
        _clock = clock;
    }

    internal static AzureAuthentication FromApiKey(string apiKey, string service)
        => new(!string.IsNullOrWhiteSpace(apiKey) ? apiKey
            : throw new ArgumentException($"A {service} API key is required.", nameof(apiKey)), null, DefaultTokenScope, TimeProvider.System);

    internal static AzureAuthentication FromCredential(TokenCredential credential, string scope, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (string.IsNullOrWhiteSpace(scope))
            throw new ArgumentException("TokenScope is required when authenticating with a TokenCredential.", "options");
        return new(null, credential, scope, clock);
    }

    /// <summary>Returns a function that applies the credential to a request message.</summary>
    internal async ValueTask<Action<HttpRequestMessage>> GetApplierAsync(CancellationToken cancellationToken)
    {
        if (_apiKey is { } apiKey)
            return message => message.Headers.Add("api-key", apiKey);
        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        return message => message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is { } cached && cached.ExpiresOn - RefreshMargin > _clock.GetUtcNow())
            return cached.Token;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token is { } current && current.ExpiresOn - RefreshMargin > _clock.GetUtcNow())
                return current.Token;
            var token = await _credential!.GetTokenAsync(_context, cancellationToken).ConfigureAwait(false);
            _token = token;
            return token.Token;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Endpoint checks shared by the Azure clients.</summary>
internal static class AzureEndpoint
{
    internal static Uri Require(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("The endpoint must be an absolute HTTPS URI.", nameof(endpoint));
        return endpoint;
    }

    /// <summary>Appends <paramref name="relativePath"/> to the endpoint's path, whether or not it ends with a slash.</summary>
    internal static Uri Combine(Uri endpoint, string relativePath)
        => new(endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/" + relativePath);

    internal static string RequireDeployment(string deploymentName)
        => !string.IsNullOrWhiteSpace(deploymentName) ? deploymentName
            : throw new ArgumentException("A deployment name is required.", nameof(deploymentName));
}
