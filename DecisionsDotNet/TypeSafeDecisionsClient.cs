using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DecisionsDotNet;

public sealed class TypeSafeDecisionsClientOptions : DecisionsClientOptions
{
    /// <summary>Absolute HTTP or HTTPS service root. Endpoint paths such as v1/systemone are appended to this URI.</summary>
    public Uri BaseUri { get; init; } = new("https://api.typesafe.ai/");
    /// <summary>Input modalities keyed by exact model ID. Overrides take precedence over known Jev capabilities.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ModelInputModalitiesOverrides { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>();
}

/// <summary>HTTP client for TypeSafe AI's System One and model-list endpoints.</summary>
public sealed class TypeSafeDecisionsClient : IDecisionsClient, IDisposable
{
    /// <summary>The alias for TypeSafe's current stable Jev model. Pass it as the request model.</summary>
    public static string DefaultModel => "jev-latest";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _apiKey;
    private readonly TypeSafeDecisionsClientOptions _options;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _modalityOverrides;
    private readonly RetryPolicy _retry;
    private static readonly IReadOnlySet<int> RetryableStatusCodes = new HashSet<int> { 429, 529 };

    public TypeSafeDecisionsClient(string apiKey, HttpClient? httpClient = null, TypeSafeDecisionsClientOptions? options = null)
    {
        _apiKey = !string.IsNullOrWhiteSpace(apiKey) ? apiKey : throw new ArgumentException("An API key is required.", nameof(apiKey));
        _options = options ?? new TypeSafeDecisionsClientOptions();
        _modalityOverrides = ModelInputModalities.Snapshot(_options.ModelInputModalitiesOverrides);
        if (_options.BaseUri is null || !_options.BaseUri.IsAbsoluteUri ||
            (_options.BaseUri.Scheme != Uri.UriSchemeHttps && _options.BaseUri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("BaseUri must be an absolute HTTP or HTTPS URI.", nameof(options));
        _retry = RetryPolicy.From(_options, RetryableStatusCodes);
        _retry.Validate(nameof(options));
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    public static TypeSafeDecisionsClient FromEnvironment(HttpClient? httpClient = null, TypeSafeDecisionsClientOptions? options = null)
        => new(Environment.GetEnvironmentVariable("TYPESAFE_API_KEY")
            ?? throw new InvalidOperationException("TYPESAFE_API_KEY is not set."), httpClient, options);

    public async Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var (state, hasImages) = ImageInput.ValidateRequest(request);
        var modalities = _modalityOverrides.TryGetValue(request.Model, out var configured)
            ? configured
            : request.Model is "jev-1.13.0" or "jev-1.14.0" or "jev-latest" or "jev-preview"
                ? new[] { "text", "image" } : null;
        string body;
        if (hasImages)
        {
            ImageInput.RequireImageSupport(modalities, request.Model);
            var assets = new Dictionary<string, object>(StringComparer.Ordinal);
            var wireParts = new List<object>();
            var original = JsonSerializer.Serialize(request, JsonOptions);
            foreach (var part in state.EnumerateArray())
            {
                if (!ImageInput.IsImage(part)) { wireParts.Add(part); continue; }
                var (mediaType, base64) = ImageInput.ReadImage(part);
                if (mediaType == "image/webp")
                    throw new NotSupportedException("Direct TypeSafe image requests support PNG and JPEG only. Convert WebP before sending.");
                string reference;
                do { reference = Guid.NewGuid().ToString(); }
                while (original.Contains(reference, StringComparison.Ordinal) || assets.ContainsKey(reference));
                assets.Add(reference, new { type = "image", format = mediaType == "image/png" ? "png" : "jpg", data_base64 = base64 });
                wireParts.Add(reference);
            }
            body = JsonSerializer.Serialize(new { state = wireParts, model = request.Model, questions = request.Questions, assets }, JsonOptions);
        }
        else
        {
            object wireState = modalities is null || ImageInput.IsMultimodal(modalities)
                ? state : ImageInput.AsJsonText(state);
            body = JsonSerializer.Serialize(new { state = wireState, model = request.Model, questions = request.Questions }, JsonOptions);
        }
        var enableImages = hasImages || request.Questions.Values.Any(question => question is BoxQuestion);
        var response = await SendAsync(HttpMethod.Post, "v1/systemone", body, cancellationToken, enableImages).ConfigureAwait(false);
        return DecisionsResponseValidator.DeserializeAndValidate(response, request.Questions);
    }

    public async Task<TypeSafeModelList> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var result = Deserialize<TypeSafeModelList>(
            await SendAsync(HttpMethod.Get, "v1/models", null, cancellationToken).ConfigureAwait(false));
        if (result.Models is null || result.Models.Any(model => model is null ||
            string.IsNullOrWhiteSpace(model.Name) || model.Description is null || model.ReleaseDate is null))
            throw new JsonException("TypeSafe returned an incomplete model list.");
        return result;
    }

    private Task<string> SendAsync(HttpMethod method, string path, string? body, CancellationToken cancellationToken,
        bool enableImages = false)
        => DecisionsHttp.SendAsync(_httpClient, () =>
        {
            var message = new HttpRequestMessage(method, new Uri(_options.BaseUri, path));
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            if (enableImages) message.Headers.Add("TypeSafe-Danger-Zone-Images", "true");
            if (body is not null)
                message.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return message;
        }, _retry, "TypeSafe", cancellationToken);

    private static T Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, JsonOptions)
           ?? throw new JsonException("TypeSafe returned an empty JSON response.");

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
