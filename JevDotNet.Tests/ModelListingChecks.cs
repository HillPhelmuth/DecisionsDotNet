using System.Net;
using DecisionsDotNet;

public sealed class ModelListingChecks
{
    private const string Catalog = """
        {"data":[
          {"id":"typesafe/jev-1.14","canonical_slug":"typesafe/jev-1.14-20260915","name":"Jev 1.14","description":"Vision","created":1789430400,"architecture":{"input_modalities":["text","image"]}},
          {"id":"text/model","name":"Text","architecture":{"input_modalities":["text"]}},
          {"id":"unknown/model","created":null},
          {"id":"far/future","created":999999999999999}
        ]}
        """;

    [Fact]
    public async Task ListingRetriesAndReadsModelMetadataAsync()
    {
        var calls = 0;
        var handler = new StubHandler(request =>
        {
            calls++;
            Assert.True(request.Method == HttpMethod.Get, "Listing method");
            Assert.True(request.RequestUri?.ToString() == "https://openrouter.ai/api/v1/models?output_modalities=decisions", "Listing URL");
            Assert.True(request.Headers.Authorization is null, "Listing sends no API key");
            return calls == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("busy") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Catalog) };
        });
        using var client = new OpenRouterDecisionsClient("router-key", new HttpClient(handler),
            new OpenRouterDecisionsClientOptions { InitialRetryDelay = TimeSpan.Zero, MaxRetryDelay = TimeSpan.Zero });

        var models = await client.ListModelsAsync(TestContext.Current.CancellationToken);

        Assert.True(calls == 2, "Listing retries 503");
        Assert.True(models.Count == 4 && models[0].Id == "typesafe/jev-1.14" && models[0].Name == "Jev 1.14", "Listing models");
        Assert.True(models[0].CanonicalSlug == "typesafe/jev-1.14-20260915" && models[0].Created?.Year == 2026, "Listing metadata");
        Assert.True(models[0].SupportsImageInput && !models[1].SupportsImageInput, "Listing image support");
        Assert.True(models[2].InputModalities.Count == 0 && !models[2].SupportsImageInput, "Missing modalities stay empty");
        Assert.True(models[2].Created is null && models[3].Created is null, "Null or out-of-range timestamps are absent");
    }

    [Fact]
    public async Task StaticListingNeedsNoApiKeyAsync()
    {
        using var http = new HttpClient(new StubHandler(request =>
        {
            Assert.True(request.Headers.Authorization is null, "Listing sends no API key");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Catalog) };
        }));

        var models = await OpenRouterDecisionsClient.ListModelsAsync(http, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(4, models.Count);
    }

    [Fact]
    public async Task ListingErrorsCarryTheProviderDetailAsync()
    {
        using var failing = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("nope") }));

        var error = await Assert.ThrowsAsync<DecisionsApiException>(() =>
            OpenRouterDecisionsClient.ListModelsAsync(failing, cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(error.StatusCode == HttpStatusCode.Unauthorized && error.Service == "OpenRouter", "Listing error details");
    }
}
