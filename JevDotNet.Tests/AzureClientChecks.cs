using System.Net;
using System.Text.Json;
using Azure.Core;
using DecisionsDotNet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public sealed class AzureClientChecks
{
    private const string Png = "iVBORw0KGgo=";
    private static readonly Uri FoundryEndpoint = new("https://my-project.services.ai.azure.com/providers/microsoft");
    private static readonly Uri AzureOpenAIEndpoint = new("https://my-resource.openai.azure.com/");

    private const string SystemOneAnswer = """
        {"answers":{"urgent":{"type":"noul","noul":0.96},"team":{"type":"choice","choice":"billing","probabilities":{"billing":0.8,"technical":0.2},"confidence":0.75}},
         "model":"microsoft-decision-1.1","usage":{"input_tokens":120,"output_tokens":0}}
        """;

    private const string AzureOpenAIAnswer = """
        {"model":"gpt-6-luna","answers":[{"type":"predicate","name":"urgent","probability":0.9}],"usage":{"input_tokens":30,"output_tokens":0}}
        """;

    [Fact]
    public async Task FoundrySendsTheSystemOneShapeToTheDeploymentWithAnApiKeyAsync()
    {
        JsonElement sent = default;
        var calls = 0;
        var handler = new StubHandler(message =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("https://my-project.services.ai.azure.com/providers/microsoft/v1/systemone", message.RequestUri?.ToString());
            Assert.Equal("foundry-key", message.Headers.GetValues("api-key").Single());
            Assert.Null(message.Headers.Authorization);
            if (calls == 1) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("busy") };
            sent = Body(message);
            return Ok(SystemOneAnswer);
        });
        using var client = new FoundryDecisionsClient(FoundryEndpoint, "pi-decision-1.1", "foundry-key", new HttpClient(handler),
            new FoundryDecisionsClientOptions { InitialRetryDelay = TimeSpan.Zero, MaxRetryDelay = TimeSpan.Zero });

        var response = await client.EvaluateAsync(DecisionsRequest.Create(DecisionsValue.FromObject(new { message = "API returns 500" }))
            .Noul("urgent", "Is this urgent?", out var urgent, "Blocking", "Can wait")
            .Choice("team", "Which team?", out var team, ("billing", "Payments"), ("technical", "Bugs"))
            .Build("ignored-model"), TestCancellation);

        Assert.Equal(2, calls);
        Assert.Equal("pi-decision-1.1", sent.GetProperty("model").GetString());
        Assert.Equal("API returns 500", sent.GetProperty("state").GetProperty("message").GetString());
        Assert.Equal("noul", sent.GetProperty("questions").GetProperty("urgent").GetProperty("type").GetString());
        Assert.Equal("Payments", sent.GetProperty("questions").GetProperty("team").GetProperty("criteria").GetProperty("billing").GetString());
        Assert.False(sent.TryGetProperty("assets", out _));
        Assert.Equal("Foundry", response.Provider);
        Assert.Equal("microsoft-decision-1.1", response.Model);
        Assert.Equal(0.96, response.Get(urgent).Noul);
        Assert.Equal("billing", response.Get(team).Choice);
    }

    [Theory]
    [InlineData("https://my-project.services.ai.azure.com", "https://my-project.services.ai.azure.com/providers/microsoft/v1/systemone")]
    [InlineData("https://my-project.services.ai.azure.com/", "https://my-project.services.ai.azure.com/providers/microsoft/v1/systemone")]
    [InlineData("https://my-project.services.ai.azure.com/providers/microsoft/", "https://my-project.services.ai.azure.com/providers/microsoft/v1/systemone")]
    public void FoundryAcceptsTheResourceOrProviderEndpoint(string endpoint, string expected)
        => Assert.Equal(expected, FoundryDecisionsClient.SystemOneUri(new Uri(endpoint)).ToString());

    [Fact]
    public async Task FoundryMovesImagesIntoAssetsAsync()
    {
        JsonElement sent = default;
        using var client = new FoundryDecisionsClient(FoundryEndpoint, "pi-decision-1.1", "k", new HttpClient(new StubHandler(message =>
        {
            sent = Body(message);
            return Ok("""{"answers":{"damaged":{"type":"noul","noul":0.2}},"model":"m","usage":{"input_tokens":1,"output_tokens":0}}""");
        })));

        await client.EvaluateAsync(DecisionsRequest.Create(
                DecisionsValue.FromParts("Customer photo of the delivery:", DecisionsValue.ImageFromDataUrl($"data:image/png;base64,{Png}")))
            .Noul("damaged", "Does the photo show damage?").Build("m"), TestCancellation);

        Assert.Equal("Customer photo of the delivery:\nimage:image-1.png", sent.GetProperty("state").GetString());
        var asset = sent.GetProperty("assets").GetProperty("image-1.png");
        Assert.Equal("image", asset.GetProperty("type").GetString());
        Assert.Equal("png", asset.GetProperty("format").GetString());
        Assert.Equal(Png, asset.GetProperty("data_base64").GetString());
    }

    [Fact]
    public async Task FoundryRejectsBoxQuestionsBeforeSendingAsync()
    {
        using var client = new FoundryDecisionsClient(FoundryEndpoint, "d", "k",
            new HttpClient(new StubHandler(_ => throw new InvalidOperationException("No request expected."))));
        var request = new DecisionsRequest("text", new Dictionary<string, DecisionsQuestion> { ["where"] = new BoxQuestion("Where is it?") }, "m");

        await Assert.ThrowsAsync<NotSupportedException>(() => client.EvaluateAsync(request, TestCancellation));
    }

    [Fact]
    public async Task FoundryCachesEntraTokensAndRequestsTheCognitiveServicesScopeAsync()
    {
        var credential = new CountingCredential(TimeSpan.FromHours(1));
        var handler = new StubHandler(message =>
        {
            Assert.Equal("Bearer token-1", message.Headers.Authorization?.ToString());
            Assert.False(message.Headers.Contains("api-key"));
            return Ok(SystemOneAnswer);
        });
        using var client = new FoundryDecisionsClient(FoundryEndpoint, "d", credential, new HttpClient(handler));

        await client.EvaluateAsync(TwoQuestions(), TestCancellation);
        await client.EvaluateAsync(TwoQuestions(), TestCancellation);

        Assert.Equal(1, credential.Calls);
        Assert.Equal(["https://cognitiveservices.azure.com/.default"], credential.Scopes);
    }

    [Fact]
    public async Task EntraTokensNearExpiryAreRefreshedAsync()
    {
        var credential = new CountingCredential(TimeSpan.FromMinutes(2));
        string? lastHeader = null;
        using var client = new AzureOpenAIDecisionsClient(AzureOpenAIEndpoint, "luna", credential, new HttpClient(new StubHandler(message =>
        {
            lastHeader = message.Headers.Authorization?.ToString();
            return Ok(AzureOpenAIAnswer);
        })), new AzureOpenAIDecisionsClientOptions { TokenScope = "https://ai.azure.com/.default" });

        await client.EvaluateAsync(OneQuestion(), TestCancellation);
        await client.EvaluateAsync(OneQuestion(), TestCancellation);

        Assert.Equal(2, credential.Calls);
        Assert.Equal("Bearer token-2", lastHeader);
        Assert.Equal(["https://ai.azure.com/.default"], credential.Scopes);
    }

    [Fact]
    public async Task EachRetryAttemptIsSignedWithACurrentTokenAsync()
    {
        var credential = new CountingCredential(TimeSpan.FromMinutes(2));
        var headers = new List<string?>();
        using var client = new FoundryDecisionsClient(FoundryEndpoint, "d", credential, new HttpClient(new StubHandler(message =>
        {
            headers.Add(message.Headers.Authorization?.ToString());
            return headers.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("slow down") }
                : Ok(SystemOneAnswer);
        })), new FoundryDecisionsClientOptions { InitialRetryDelay = TimeSpan.Zero, MaxRetryDelay = TimeSpan.Zero });

        await client.EvaluateAsync(TwoQuestions(), TestCancellation);

        Assert.Equal(["Bearer token-1", "Bearer token-2"], headers);
    }

    [Fact]
    public async Task AzureOpenAISendsTheOpenAIShapeToTheDeploymentWithAnApiKeyAsync()
    {
        JsonElement sent = default;
        using var client = new AzureOpenAIDecisionsClient(AzureOpenAIEndpoint, "my-luna", "aoai-key", new HttpClient(new StubHandler(message =>
        {
            Assert.Equal("https://my-resource.openai.azure.com/openai/v1/decisions", message.RequestUri?.ToString());
            Assert.Equal("aoai-key", message.Headers.GetValues("api-key").Single());
            Assert.Null(message.Headers.Authorization);
            sent = Body(message);
            return Ok(AzureOpenAIAnswer);
        })));

        var response = await client.EvaluateAsync(OneQuestion(), TestCancellation);

        Assert.Equal("my-luna", sent.GetProperty("model").GetString());
        Assert.Equal("I was charged twice.", sent.GetProperty("input").GetString());
        var question = sent.GetProperty("questions")[0];
        Assert.Equal("predicate", question.GetProperty("type").GetString());
        Assert.Equal("urgent", question.GetProperty("name").GetString());
        Assert.Equal("Azure OpenAI", response.Provider);
        Assert.Equal(0.9, response.GetAnswer<NoulAnswer>("urgent").Noul);
    }

    [Theory]
    [InlineData("https://my-resource.openai.azure.com")]
    [InlineData("https://my-resource.openai.azure.com/openai")]
    [InlineData("https://my-resource.openai.azure.com/openai/v1/")]
    public void AzureOpenAIAcceptsTheResourceOrSdkBaseUrl(string endpoint)
        => Assert.Equal("https://my-resource.openai.azure.com/openai/v1/decisions",
            AzureOpenAIDecisionsClient.DecisionsUri(new Uri(endpoint)).ToString());

    [Fact]
    public void ConstructorsValidateTheirArguments()
    {
        Assert.Throws<ArgumentException>(() => new FoundryDecisionsClient(new Uri("http://insecure.example/"), "d", "k"));
        Assert.Throws<ArgumentException>(() => new FoundryDecisionsClient(FoundryEndpoint, " ", "k"));
        Assert.Throws<ArgumentException>(() => new AzureOpenAIDecisionsClient(AzureOpenAIEndpoint, "d", ""));
        Assert.Throws<ArgumentNullException>(() => new AzureOpenAIDecisionsClient(AzureOpenAIEndpoint, "d", (TokenCredential)null!));
    }

    [Fact]
    public async Task ConfigurationRegistrationsPreferTheApiKeyAndFallBackToTheCredentialAsync()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Foundry:Endpoint"] = FoundryEndpoint.ToString(),
            ["Foundry:DeploymentName"] = "pi-decision-1.1",
            ["Foundry:ApiKey"] = "foundry-key",
            ["AzureOpenAI:Endpoint"] = AzureOpenAIEndpoint.ToString(),
            ["AzureOpenAI:DeploymentName"] = "luna"
        }).Build();
        var credential = new CountingCredential(TimeSpan.FromHours(1));
        var services = new ServiceCollection();
        services.AddFoundryDecisionsClient(configuration, credential)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(message =>
            {
                Assert.Equal("foundry-key", message.Headers.GetValues("api-key").Single());
                return Ok(SystemOneAnswer);
            }));
        services.AddKeyedAzureOpenAIDecisionsClient("aoai", configuration, credential)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(message =>
            {
                Assert.Equal("Bearer token-1", message.Headers.Authorization?.ToString());
                return Ok(AzureOpenAIAnswer);
            }));
        using var provider = services.BuildServiceProvider();

        Assert.IsType<FoundryDecisionsClient>(provider.GetRequiredService<IDecisionsClient>());
        await provider.GetRequiredService<IDecisionsClient>().EvaluateAsync(TwoQuestions(), TestCancellation);
        var keyed = provider.GetRequiredKeyedService<IDecisionsClient>("aoai");
        Assert.Equal("luna", Assert.IsType<AzureOpenAIDecisionsClient>(keyed).DeploymentName);
        await keyed.EvaluateAsync(OneQuestion(), TestCancellation);
        Assert.Equal(1, credential.Calls);

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAzureOpenAIDecisionsClient(configuration));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddFoundryDecisionsClient(new ConfigurationBuilder().Build()));
    }

    private static DecisionsRequest OneQuestion()
        => DecisionsRequest.Create("I was charged twice.").Noul("urgent", "Is this urgent?").Build("ignored-model");

    private static DecisionsRequest TwoQuestions()
        => DecisionsRequest.Create("The API returns 500.").Noul("urgent", "Is this urgent?")
            .Choice("team", "Which team?", ("billing", "Payments"), ("technical", "Bugs")).Build("ignored-model");

    private static JsonElement Body(HttpRequestMessage message)
        => JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();

    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    /// <summary>Issues numbered tokens that expire after a fixed lifetime and records what it was asked for.</summary>
    private sealed class CountingCredential(TimeSpan lifetime) : TokenCredential
    {
        public int Calls { get; private set; }
        public string[] Scopes { get; private set; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Calls++;
            Scopes = requestContext.Scopes;
            return new AccessToken($"token-{Calls}", DateTimeOffset.UtcNow + lifetime);
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(GetToken(requestContext, cancellationToken));
    }
}
