using System.Net;
using DecisionsDotNet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public sealed class KeyedRegistrationChecks
{
    private static readonly Dictionary<string, DecisionsQuestion> Questions = new() { ["ok"] = new NoulQuestion("OK?") };

    [Fact]
    public async Task KeyedClientsKeepSeparateKeysAndEndpointsAsync()
    {
        var services = new ServiceCollection();
        var seen = new List<string>();
        HttpMessageHandler Stub() => new StubHandler(message =>
        {
            if (message.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                    {"data":[{"id":"typesafe/jev-1.13","architecture":{"input_modalities":["text"]}}]}
                    """) };
            seen.Add($"{message.RequestUri} {message.Headers.Authorization}");
            return Answer();
        });

        services.AddKeyedTypeSafeDecisionsClient("direct", "direct-key").ConfigurePrimaryHttpMessageHandler(Stub);
        services.AddKeyedOpenRouterDecisionsClient("router", new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["OpenRouter:ApiKey"] = "router-key" }).Build())
            .ConfigurePrimaryHttpMessageHandler(Stub);
        services.AddKeyedTypeSafeDecisionsClient("local", "local-key", new TypeSafeDecisionsClientOptions { BaseUri = new Uri("http://jev:1234/") })
            .ConfigurePrimaryHttpMessageHandler(Stub);

        using var provider = services.BuildServiceProvider();
        Assert.True(provider.GetService<IDecisionsClient>() is null, "Keyed registrations add no default client");
        var direct = provider.GetRequiredKeyedService<IDecisionsClient>("direct");
        var router = provider.GetRequiredKeyedService<IDecisionsClient>("router");
        var local = provider.GetRequiredKeyedService<IDecisionsClient>("local");
        Assert.True(direct is TypeSafeDecisionsClient && router is OpenRouterDecisionsClient && local is TypeSafeDecisionsClient, "Keyed provider types");

        await direct.EvaluateAsync(Request(TypeSafeDecisionsClient.DefaultModel), TestCancellation);
        await router.EvaluateAsync(Request("typesafe/jev-1.13"), TestCancellation);
        await local.EvaluateAsync(Request(TypeSafeDecisionsClient.DefaultModel), TestCancellation);
        Assert.Equal(
        [
            "https://api.typesafe.ai/v1/systemone Bearer direct-key",
            "https://openrouter.ai/api/alpha/decisions Bearer router-key",
            "http://jev:1234/v1/systemone Bearer local-key"
        ], seen);
    }

    [Fact]
    public async Task KeysWithTheSameStringFormUseSeparatePipelinesAsync()
    {
        var services = new ServiceCollection();
        var hits = new List<string>();
        HttpMessageHandler Tagged(string tag) => new StubHandler(_ =>
        {
            hits.Add(tag);
            return Answer();
        });
        services.AddKeyedTypeSafeDecisionsClient(1, "int-key").ConfigurePrimaryHttpMessageHandler(() => Tagged("int"));
        services.AddKeyedTypeSafeDecisionsClient("1", "string-key").ConfigurePrimaryHttpMessageHandler(() => Tagged("string"));

        using var provider = services.BuildServiceProvider();
        await provider.GetRequiredKeyedService<IDecisionsClient>(1)
            .EvaluateAsync(Request(TypeSafeDecisionsClient.DefaultModel), TestCancellation);
        await provider.GetRequiredKeyedService<IDecisionsClient>("1")
            .EvaluateAsync(Request(TypeSafeDecisionsClient.DefaultModel), TestCancellation);

        Assert.Equal(["int", "string"], hits);
    }

    [Fact]
    public void KeyedRegistrationsRejectInvalidArguments()
    {
        Assert.ThrowsAny<ArgumentException>(() => new ServiceCollection().AddKeyedOpenRouterDecisionsClient("k", " "));
        Assert.ThrowsAny<ArgumentException>(() => new ServiceCollection().AddKeyedTypeSafeDecisionsClient(null!, "key"));
    }

    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    private static DecisionsRequest Request(string model) => new("x", Questions, model);

    private static HttpResponseMessage Answer() => new(HttpStatusCode.OK) { Content = new StringContent("""
        {"model":"jev-1.13.0","answers":{"ok":{"type":"noul","noul":0.9}},"usage":{"input_tokens":1,"output_tokens":1}}
        """) };
}
