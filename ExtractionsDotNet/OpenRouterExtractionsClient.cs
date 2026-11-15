using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ExtractionsDotNet;

/// <summary>Extracts typed fields from text, images, and documents through a configured provider.</summary>
public interface IExtractionsClient
{
    /// <summary>Sends the request and returns one typed answer per requested field.</summary>
    Task<ExtractionResponse> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The base URI and retry settings for <see cref="OpenRouterExtractionsClient"/>.</summary>
public sealed class OpenRouterExtractionsClientOptions
{
    /// <summary>Absolute HTTPS origin serving OpenRouter's API routes.</summary>
    public Uri BaseUri { get; init; } = new("https://openrouter.ai/");

    /// <summary>Total attempts per request, including the first. Must be at least 1; set 1 to disable retries.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>The delay before the first retry. Later retries double it, up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest delay between attempts, including delays requested by a Retry-After header.</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The clock behind retry delays. Tests replace it to observe delays without waiting.</summary>
    internal TimeProvider Clock { get; init; } = TimeProvider.System;
}

/// <summary>Client for OpenRouter's alpha Extractions API. Retries HTTP 429, 502, 503, and 529 with bounded backoff.</summary>
public sealed class OpenRouterExtractionsClient : IExtractionsClient, IDisposable
{
    private const string ExtractionsPath = "/api/alpha/extractions";
    private const string ModelsPath = "/api/v1/models?endpoints=/api/alpha/extractions";
    private static readonly HashSet<int> RetryableStatusCodes = [429, 502, 503, 529];

    private readonly AuthenticationHeaderValue _authorization;
    private readonly HttpClient _httpClient;
    private readonly OpenRouterExtractionsClientOptions _options;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    /// <summary>Creates a client.</summary>
    /// <param name="apiKey">An OpenRouter API key.</param>
    /// <param name="httpClient">An optional caller-owned client.</param>
    /// <param name="options">The base URI and retry settings. Defaults apply when omitted.</param>
    public OpenRouterExtractionsClient(string apiKey, HttpClient? httpClient = null, OpenRouterExtractionsClientOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _options = Validate(options ?? new OpenRouterExtractionsClientOptions());
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    /// <summary>Creates a client using the <c>OPENROUTER_API_KEY</c> environment variable.</summary>
    public static OpenRouterExtractionsClient FromEnvironment(
        HttpClient? httpClient = null, OpenRouterExtractionsClientOptions? options = null)
    {
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("OPENROUTER_API_KEY is not set.");

        return new OpenRouterExtractionsClient(key, httpClient, options);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">The request has no fields, or has neither state text nor a single asset.</exception>
    /// <exception cref="ExtractionsApiException">OpenRouter returned an HTTP error that was not retried or outlasted its retries.</exception>
    /// <exception cref="System.Text.Json.JsonException">The response did not match the requested fields.</exception>
    public async Task<ExtractionResponse> ExtractAsync(
        ExtractionRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentNullException.ThrowIfNull(request.State);
        ArgumentNullException.ThrowIfNull(request.Fields);

        // Use the same field definitions for serialization and decoding after the HTTP call.
        var fields = new Dictionary<string, ExtractionField>(request.Fields, StringComparer.Ordinal);
        ValidateFields(fields);

        if (request.Assets is { } assets)
        {
            foreach (var (name, asset) in assets)
            {
                if (string.IsNullOrWhiteSpace(name) || asset is null)
                    throw new ArgumentException("Each asset must have a nonempty name and a value.", nameof(request));
            }
        }

        var state = request.State;
        if (state.Length == 0)
        {
            if (request.Assets is not { Count: > 0 })
                throw new ArgumentException("Supply state text or at least one asset.", nameof(request));
            if (request.Assets.Count != 1)
                throw new ArgumentException("Supply state text that references each asset when sending multiple assets.", nameof(request));

            state = request.Assets.Keys.Single();
        }

        // Serialize once so every attempt sends the same body.
        var body = JsonSerializer.Serialize(request with { State = state, Fields = fields }, ExtractionJson.Options);
        var content = await SendAsync(_httpClient, () =>
        {
            var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.BaseUri, ExtractionsPath));
            message.Headers.Authorization = _authorization;
            message.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return message;
        }, _options, cancellationToken).ConfigureAwait(false);
        return ExtractionJson.ReadResponse(content, fields);
    }

    /// <summary>Lists models serving the Extractions route, using this client's base URI and retry settings.</summary>
    public Task<IReadOnlyList<ExtractionModel>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ListModelsAsync(_httpClient, _options, cancellationToken);
    }

    /// <summary>Lists models serving the Extractions route. The SDK adds no authorization header.</summary>
    public static async Task<IReadOnlyList<ExtractionModel>> ListModelsAsync(
        HttpClient httpClient, Uri? baseUri = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        var options = Validate(baseUri is null
            ? new OpenRouterExtractionsClientOptions() : new OpenRouterExtractionsClientOptions { BaseUri = baseUri }, nameof(baseUri));
        return await ListModelsAsync(httpClient, options, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ExtractionModel>> ListModelsAsync(
        HttpClient httpClient, OpenRouterExtractionsClientOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var content = await SendAsync(httpClient,
            () => new HttpRequestMessage(HttpMethod.Get, new Uri(options.BaseUri, ModelsPath)),
            options, cancellationToken).ConfigureAwait(false);
        return ExtractionJson.ReadModels(content);
    }

    /// <summary>Disposes the HTTP client when this instance created it. A caller-supplied client is left alone.</summary>
    public void Dispose()
    {
        _disposed = true;
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    private static OpenRouterExtractionsClientOptions Validate(
        OpenRouterExtractionsClientOptions options, string paramName = "options")
    {
        if (options.BaseUri is null || !options.BaseUri.IsAbsoluteUri || options.BaseUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("The base URI must be an absolute HTTPS URI.", paramName);
        if (options.MaxAttempts < 1 || options.InitialRetryDelay < TimeSpan.Zero || options.MaxRetryDelay < options.InitialRetryDelay)
            throw new ArgumentException("Invalid retry settings.", paramName);

        return options;
    }

    private static void ValidateFields(IReadOnlyDictionary<string, ExtractionField> fields)
    {
        if (fields.Count == 0)
            throw new ArgumentException("Supply at least one extraction field.", nameof(fields));

        foreach (var (name, field) in fields)
        {
            if (string.IsNullOrWhiteSpace(name) || field is null)
                throw new ArgumentException("Each field must have a nonempty name and a specification.", nameof(fields));

            if (field is RangeField { Step: not null, CalendarStep: not null })
                throw new ArgumentException($"Field '{name}' must use either a numeric step or a calendar step.", nameof(fields));
        }
    }

    /// <summary>Sends a request built fresh for each attempt and returns the successful response body.</summary>
    private static async Task<string> SendAsync(HttpClient client, Func<HttpRequestMessage> createMessage,
        OpenRouterExtractionsClientOptions options, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var message = createMessage();
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return content;

            if (attempt >= options.MaxAttempts || !RetryableStatusCodes.Contains((int)response.StatusCode))
                throw new ExtractionsApiException(response.StatusCode, content);

            var backoff = TimeSpan.FromMilliseconds(Math.Min(
                options.MaxRetryDelay.TotalMilliseconds,
                options.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1)));
            var retryAfter = response.Headers.RetryAfter;
            var delay = retryAfter?.Delta ??
                (retryAfter?.Date is { } date ? date - options.Clock.GetUtcNow() : backoff);
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            if (delay > options.MaxRetryDelay) delay = options.MaxRetryDelay;
            await Task.Delay(delay, options.Clock, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>The Extractions provider answered with an HTTP error that was not retried, or that outlasted its retries.</summary>
public sealed class ExtractionsApiException : HttpRequestException
{
    /// <summary>Creates the exception from the provider's status and response body.</summary>
    public ExtractionsApiException(HttpStatusCode statusCode, string responseBody, string service = "OpenRouter")
        : base($"{service} Extractions returned HTTP {(int)statusCode} ({statusCode}).", null, statusCode)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
        Service = service;
    }

    /// <summary>The HTTP status the provider returned.</summary>
    public new HttpStatusCode StatusCode { get; }

    /// <summary>The raw response body, which usually carries the provider's error detail.</summary>
    public string ResponseBody { get; }

    /// <summary>The provider that returned the error.</summary>
    public string Service { get; }
}
