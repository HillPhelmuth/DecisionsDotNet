using ExtractionsDotNet;

namespace JevDotNet.Demo.Services;

public sealed class ExtractionDemoService(
    IConfiguration configuration,
    IHttpClientFactory clients,
    IWebHostEnvironment environment)
{
    public const string HttpClientName = "OpenRouterExtractions";
    private string? ApiKey => configuration["OpenRouter:ApiKey"];
    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    public async Task<ExtractionResponse> ExtractAsync(
        ExtractionDemo demo, string state, ExtractionDemoAsset? sample, CancellationToken cancellationToken)
    {
        var key = ApiKey;
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("An OpenRouter API key is required.");

        var request = new ExtractionRequest(demo.Model, demo.Fields) { State = state };
        if (sample is not null)
        {
            // Use the same bundled bytes that the browser previews.
            var file = environment.WebRootFileProvider.GetFileInfo(sample.Path);
            await using var stream = file.CreateReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            request = request with
            {
                State = "",
                Assets = new Dictionary<string, ExtractionAsset>
                {
                    [sample.Key] = new(sample.Format, buffer.ToArray())
                }
            };
        }
        else if (demo.Model == ExtractionDemoCatalog.Dispatch.Model)
        {
            request = request with
            {
                Provider = new ExtractionProviderOptions
                {
                    Options = new Dictionary<string, object?>
                    {
                        ["cloudflare"] = new { return_state_text = true }
                    }
                }
            };
        }

        using var http = clients.CreateClient(HttpClientName);
        using var client = new OpenRouterExtractionsClient(key, http);
        return await client.ExtractAsync(request, cancellationToken);
    }
}
