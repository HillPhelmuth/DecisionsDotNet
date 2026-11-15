using System.Net;
using DecisionsDotNet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public sealed class DependencyInjectionChecks
{
    private static readonly TypeSafeDecisionsClientOptions Localhost = new() { BaseUri = new Uri("http://localhost:1234/") };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypeSafeRegistrationUsesTheDefaultEndpointAsync(bool useConfiguration)
        => await CheckProviderAsync(useConfiguration, openRouter: false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypeSafeRegistrationHonorsACustomBaseUriAsync(bool useConfiguration)
        => await CheckProviderAsync(useConfiguration, openRouter: false, options: Localhost);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenRouterRegistrationUsesItsEndpointAsync(bool useConfiguration)
        => await CheckProviderAsync(useConfiguration, openRouter: true);

    [Fact]
    public async Task LocalRegistrationUsesAPlaceholderKeyAsync()
        => await CheckProviderAsync(useConfiguration: false, openRouter: false, options: Localhost, localRegistration: true);

    [Fact]
    public async Task LocalRegistrationHonorsASuppliedKeyAsync()
        => await CheckProviderAsync(useConfiguration: false, openRouter: false,
            options: new TypeSafeDecisionsClientOptions { BaseUri = new Uri("http://jev:1234/") }, localRegistration: true,
            localApiKey: "custom-local-key");

    [Fact]
    public void RegistrationsRejectInvalidArguments()
    {
        Assert.ThrowsAny<ArgumentException>(() => new ServiceCollection().AddLocalTypeSafeDecisionsClient(null!));
        Assert.ThrowsAny<ArgumentException>(() =>
            new ServiceCollection().AddLocalTypeSafeDecisionsClient(new Uri("http://localhost:1234/"), " "));
        Assert.ThrowsAny<ArgumentException>(() => new ServiceCollection().AddTypeSafeDecisionsClient(" "));
        Assert.ThrowsAny<ArgumentException>(() => new ServiceCollection().AddOpenRouterDecisionsClient(" "));
    }

    [Fact]
    public void RegistrationsNameTheMissingConfigurationKey()
    {
        var empty = new ConfigurationBuilder().Build();
        var typeSafe = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddTypeSafeDecisionsClient(empty));
        Assert.Contains("TypeSafe:ApiKey", typeSafe.Message);
        var openRouter = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddOpenRouterDecisionsClient(empty));
        Assert.Contains("OpenRouter:ApiKey", openRouter.Message);
    }

    private static async Task CheckProviderAsync(bool useConfiguration, bool openRouter, TypeSafeDecisionsClientOptions? options = null,
        bool localRegistration = false, string? localApiKey = null)
    {
        var services = new ServiceCollection();
        var expectedKey = localRegistration ? localApiKey ?? "local-key" : openRouter ? "router-key" : "direct-key";
        var expectedUrl = openRouter
            ? "https://openrouter.ai/api/alpha/decisions"
            : new Uri(options?.BaseUri ?? new Uri("https://api.typesafe.ai/"), "v1/systemone").ToString();
        var model = openRouter ? "typesafe/jev-1.13" : "jev-latest";
        var calls = 0;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [openRouter ? "OpenRouter:ApiKey" : "TypeSafe:ApiKey"] = expectedKey
            }).Build();
        var builder = localRegistration
            ? localApiKey is null
                ? services.AddLocalTypeSafeDecisionsClient(options!.BaseUri)
                : services.AddLocalTypeSafeDecisionsClient(options!.BaseUri, localApiKey)
            : openRouter
            ? useConfiguration
                ? services.AddOpenRouterDecisionsClient(configuration)
                : services.AddOpenRouterDecisionsClient(expectedKey)
            : useConfiguration
                ? services.AddTypeSafeDecisionsClient(configuration, options)
                : services.AddTypeSafeDecisionsClient(expectedKey, options);
        // The key is read at registration, so a later configuration change must not leak into requests.
        if (useConfiguration)
            configuration[openRouter ? "OpenRouter:ApiKey" : "TypeSafe:ApiKey"] = "changed-after-registration";

        builder.ConfigurePrimaryHttpMessageHandler(() => new StubHandler(message =>
        {
            if (message.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                    {"data":[{"id":"typesafe/jev-1.13","architecture":{"input_modalities":["text"]}}]}
                    """) };
            calls++;
            Assert.True(message.Method == HttpMethod.Post, "DI evaluation method");
            Assert.True(message.RequestUri?.ToString() == expectedUrl, "DI provider endpoint");
            Assert.True(message.Headers.Authorization?.ToString() == $"Bearer {expectedKey}", "DI bearer key");
            var body = message.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.True(body.Contains($"\"model\":\"{model}\""), "DI request model");
            Assert.True(body.Contains("\"type\":\"choice\""), "DI typed question");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"model":"jev-1.13.0","answers":{"move":{"type":"choice","choice":"right","probabilities":{"right":0.6,"down":0.4},"confidence":0.6}},"usage":{"input_tokens":12,"output_tokens":2}}
                    """)
            };
        }));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IDecisionsClient>();
        Assert.True(openRouter ? client is OpenRouterDecisionsClient : client is TypeSafeDecisionsClient, "DI provider selection");
        var request = new DecisionsRequest("A chase", new Dictionary<string, DecisionsQuestion>
        {
            ["move"] = new ChoiceQuestion("Which move?", new Dictionary<string, DecisionsValue?>
            {
                ["right"] = "Closer", ["down"] = "Farther"
            })
        }, model);
        var response = await client.EvaluateAsync(request, TestContext.Current.CancellationToken);
        Assert.True(response.GetAnswer<ChoiceAnswer>("move").Choice == "right", "DI typed answer");
        Assert.True(response.Usage.InputTokens == 12 && calls == 1, "DI response and single call");
    }
}
