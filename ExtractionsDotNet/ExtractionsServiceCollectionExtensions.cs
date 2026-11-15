using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ExtractionsDotNet;

/// <summary>Registers an Extractions provider behind <see cref="IExtractionsClient"/>.</summary>
public static class ExtractionsServiceCollectionExtensions
{
    /// <summary>Registers the OpenRouter client as <see cref="IExtractionsClient"/> on its own named HTTP pipeline.</summary>
    public static IHttpClientBuilder AddOpenRouterExtractionsClient(
        this IServiceCollection services,
        string apiKey,
        OpenRouterExtractionsClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("An OpenRouter API key is required.", nameof(apiKey));

        return services.AddHttpClient("ExtractionsDotNet.OpenRouter")
            .AddTypedClient<IExtractionsClient>((httpClient, _) => new OpenRouterExtractionsClient(apiKey, httpClient, options));
    }

    /// <summary>Registers the OpenRouter client using the <c>OpenRouter:ApiKey</c> configuration value.</summary>
    public static IHttpClientBuilder AddOpenRouterExtractionsClient(
        this IServiceCollection services,
        IConfiguration configuration,
        OpenRouterExtractionsClientOptions? options = null)
        => services.AddOpenRouterExtractionsClient(ApiKey(configuration), options);

    /// <summary>Registers the OpenRouter client as a keyed <see cref="IExtractionsClient"/>, so differently configured clients can sit side by side.</summary>
    public static IHttpClientBuilder AddKeyedOpenRouterExtractionsClient(
        this IServiceCollection services,
        object serviceKey,
        string apiKey,
        OpenRouterExtractionsClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("An OpenRouter API key is required.", nameof(apiKey));

        // A unique name per registration keeps keys with equal ToString() values on separate HTTP pipelines.
        var name = $"ExtractionsDotNet.OpenRouter.{Guid.NewGuid():N}";
        services.AddKeyedTransient<IExtractionsClient>(serviceKey, (provider, _) =>
            new OpenRouterExtractionsClient(apiKey, provider.GetRequiredService<IHttpClientFactory>().CreateClient(name), options));
        return services.AddHttpClient(name);
    }

    /// <summary>Registers a keyed OpenRouter client using the <c>OpenRouter:ApiKey</c> configuration value.</summary>
    public static IHttpClientBuilder AddKeyedOpenRouterExtractionsClient(
        this IServiceCollection services,
        object serviceKey,
        IConfiguration configuration,
        OpenRouterExtractionsClientOptions? options = null)
        => services.AddKeyedOpenRouterExtractionsClient(serviceKey, ApiKey(configuration), options);

    private static string ApiKey(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var apiKey = configuration["OpenRouter:ApiKey"];
        return !string.IsNullOrWhiteSpace(apiKey) ? apiKey
            : throw new InvalidOperationException("Configure OpenRouter:ApiKey before registering the OpenRouter Extractions client.");
    }
}
