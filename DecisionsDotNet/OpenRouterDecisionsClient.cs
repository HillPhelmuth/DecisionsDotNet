using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DecisionsDotNet;

/// <summary>The base URI, model capability overrides, and retry settings for <see cref="OpenRouterDecisionsClient"/>.</summary>
public sealed class OpenRouterDecisionsClientOptions : DecisionsClientOptions
{
    /// <summary>Absolute HTTPS origin serving OpenRouter's API routes.</summary>
    public Uri BaseUri { get; init; } = new("https://openrouter.ai/");
    /// <summary>Input modalities keyed by exact model ID. Overrides bypass catalog discovery for that model.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ModelInputModalitiesOverrides { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>();
}

/// <summary>HTTP client for OpenRouter's Decisions API.</summary>
public sealed class OpenRouterDecisionsClient : IDecisionsClient, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _apiKey;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly OpenRouterDecisionsClientOptions _options;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _modalityOverrides;
    private readonly RetryPolicy _retry;
    private static readonly IReadOnlySet<int> RetryableStatusCodes = new HashSet<int> { 429, 502, 503, 529 };

    /// <summary>Creates a client.</summary>
    /// <param name="apiKey">An OpenRouter API key.</param>
    /// <param name="httpClient">An optional caller-owned client. When omitted, the client creates and disposes its own.</param>
    /// <param name="options">The base URI, capability overrides, and retry settings. Defaults apply when omitted.</param>
    public OpenRouterDecisionsClient(
        string apiKey,
        HttpClient? httpClient = null,
        OpenRouterDecisionsClientOptions? options = null)
    {
        _apiKey = !string.IsNullOrWhiteSpace(apiKey)
            ? apiKey : throw new ArgumentException("An OpenRouter API key is required.", nameof(apiKey));
        _options = options ?? new OpenRouterDecisionsClientOptions();
        _modalityOverrides = ModelInputModalities.Snapshot(_options.ModelInputModalitiesOverrides);
        if (_options.BaseUri is null || !_options.BaseUri.IsAbsoluteUri || _options.BaseUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("BaseUri must be an absolute HTTPS URI.", nameof(options));
        _retry = RetryPolicy.From(_options, RetryableStatusCodes);
        _retry.Validate(nameof(options));
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    /// <summary>Creates a client using the <c>OPENROUTER_API_KEY</c> environment variable.</summary>
    public static OpenRouterDecisionsClient FromEnvironment(
        HttpClient? httpClient = null,
        OpenRouterDecisionsClientOptions? options = null)
        => new(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")
            ?? throw new InvalidOperationException("OPENROUTER_API_KEY is not set."), httpClient, options);

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The request has images that the model is not known to accept, or images outside the top-level state array.</exception>
    /// <exception cref="DecisionsApiException">OpenRouter returned an HTTP error that was not retried or outlasted its retries.</exception>
    /// <exception cref="System.Text.Json.JsonException">The response did not match the questions asked.</exception>
    public async Task<DecisionsResponse> EvaluateAsync(
        DecisionsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var (state, hasImages) = ImageInput.ValidateRequest(request);
        var modalities = _modalityOverrides.TryGetValue(request.Model, out var configured)
            ? configured
            : await OpenRouterModelCatalog.Shared.ResolveAsync(_httpClient, _options.BaseUri,
                request.Model, cancellationToken).ConfigureAwait(false);
        if (hasImages) ImageInput.RequireImageSupport(modalities, request.Model);
        object wireState = ImageInput.IsMultimodal(modalities) ? state : ImageInput.AsJsonText(state);
        var body = JsonSerializer.Serialize(new
        {
            state = wireState,
            model = request.Model,
            questions = request.Questions
        }, JsonOptions);

        var content = await DecisionsHttp.SendAsync(_httpClient, () =>
        {
            var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.BaseUri, "api/alpha/decisions"));
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            message.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return message;
        }, _retry, "OpenRouter", cancellationToken).ConfigureAwait(false);
        return DecisionsResponseValidator.DeserializeAndValidate(content, request.Questions);
    }

    /// <summary>Lists OpenRouter's Decisions models with their input modalities. The catalog is public; no API key is sent.</summary>
    public async Task<IReadOnlyList<OpenRouterDecisionsModel>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var content = await DecisionsHttp.SendAsync(_httpClient,
            () => new HttpRequestMessage(HttpMethod.Get, new Uri(_options.BaseUri, OpenRouterModelCatalog.Path)),
            _retry, "OpenRouter", cancellationToken).ConfigureAwait(false);
        return OpenRouterModelCatalog.Parse(content);
    }

    /// <summary>Lists OpenRouter's Decisions models without constructing a client or supplying an API key.</summary>
    public static async Task<IReadOnlyList<OpenRouterDecisionsModel>> ListModelsAsync(
        HttpClient httpClient, Uri? baseUri = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        baseUri ??= new OpenRouterDecisionsClientOptions().BaseUri;
        if (!baseUri.IsAbsoluteUri || baseUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("BaseUri must be an absolute HTTPS URI.", nameof(baseUri));
        var defaults = new OpenRouterDecisionsClientOptions();
        var content = await DecisionsHttp.SendAsync(httpClient,
            () => new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, OpenRouterModelCatalog.Path)),
            RetryPolicy.From(defaults, RetryableStatusCodes),
            "OpenRouter", cancellationToken).ConfigureAwait(false);
        return OpenRouterModelCatalog.Parse(content);
    }

    /// <summary>Disposes the HTTP client when this instance created it. A caller-supplied client is left alone.</summary>
    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
