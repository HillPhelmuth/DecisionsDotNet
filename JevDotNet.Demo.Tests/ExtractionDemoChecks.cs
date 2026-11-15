using System.Net;
using System.Text.Json;
using ExtractionsDotNet;
using JevDotNet.Demo.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

public sealed class ExtractionDemoChecks
{
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DispatchSendsEditedTextAndAllFieldTypes()
    {
        const string state = "🧭 North Ridge Survey updated dispatch";
        var service = CreateService(new Handler(async (message, token) =>
        {
            Assert.Equal("https://openrouter.ai/api/alpha/extractions", message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", message.Headers.Authorization?.Scheme);
            using var json = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            var body = json.RootElement;
            Assert.Equal("cloudflare/clairic", body.GetProperty("model").GetString());
            Assert.Equal(state, body.GetProperty("state").GetString());
            Assert.False(body.TryGetProperty("assets", out _));
            Assert.True(body.GetProperty("provider").GetProperty("options")
                .GetProperty("cloudflare").GetProperty("return_state_text").GetBoolean());

            var fields = body.GetProperty("fields");
            Assert.Equal(new[] { "date", "datetime", "duration", "estimate", "forecast", "list", "number", "text", "time" },
                fields.EnumerateObject().Select(field => field.Value.GetProperty("type").GetString()).Distinct().Order().ToArray());
            Assert.Equal(new[] { "date", "number", "text" }, fields.EnumerateObject()
                .Where(field => field.Value.GetProperty("type").ValueEquals("list"))
                .Select(field => field.Value.GetProperty("items").GetString()).Order().ToArray());
            Assert.Equal("day", fields.GetProperty("water_outlook").GetProperty("interval").GetString());
            Assert.Equal(3, fields.GetProperty("water_outlook").GetProperty("horizon").GetInt32());
            return NullAnswers(body);
        }));

        var result = await service.ExtractAsync(ExtractionDemoCatalog.Dispatch, state, null, TestCancellation);
        Assert.IsType<ExtractionForecastAnswer<double>>(result.Answers["water_outlook"]);
    }

    [Theory]
    [InlineData("png", "image", "cloudflare/clairic")]
    [InlineData("jpeg", "image", "cloudflare/clairic")]
    [InlineData("webp", "image", "cloudflare/clairic")]
    [InlineData("pdf", "document", "scaledown/extract")]
    public async Task AssetDemosSendOneFileWithCompatibleFields(string format, string type, string model)
    {
        var isPdf = format == "pdf";
        var demo = isPdf ? ExtractionDemoCatalog.Permit : ExtractionDemoCatalog.CargoTag;
        var sample = isPdf ? ExtractionDemoCatalog.PermitFile
            : Assert.Single(ExtractionDemoCatalog.CargoFormats, sample => sample.Format.ToString().Equals(format, StringComparison.OrdinalIgnoreCase));
        var path = Path.Combine(WebRoot(), sample.Path);
        var fileBytes = await File.ReadAllBytesAsync(path, TestCancellation);

        var service = CreateService(new Handler(async (message, token) =>
        {
            using var json = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            var body = json.RootElement;
            Assert.Equal(model, body.GetProperty("model").GetString());
            Assert.Equal(sample.Key, body.GetProperty("state").GetString());
            Assert.False(body.TryGetProperty("provider", out _));

            var asset = Assert.Single(body.GetProperty("assets").EnumerateObject());
            Assert.Equal(sample.Key, asset.Name);
            Assert.Equal(type, asset.Value.GetProperty("type").GetString());
            Assert.Equal(format, asset.Value.GetProperty("format").GetString());
            Assert.Equal(fileBytes, asset.Value.GetProperty("data_base64").GetBytesFromBase64());

            var fields = body.GetProperty("fields");
            if (isPdf)
            {
                Assert.Equal("text", fields.GetProperty("emergency_phone").GetProperty("type").GetString());
                Assert.All(fields.EnumerateObject(), field =>
                {
                    Assert.False(field.Value.TryGetProperty("null_threshold", out _));
                    if (field.Value.GetProperty("type").ValueEquals("list"))
                        Assert.Equal("text", field.Value.GetProperty("items").GetString());
                    else
                        Assert.Equal("text", field.Value.GetProperty("type").GetString());
                });
            }
            else
            {
                Assert.Equal("number", fields.GetProperty("gross_weight").GetProperty("type").GetString());
                Assert.Equal("date", fields.GetProperty("inspection_date").GetProperty("type").GetString());
            }

            return NullAnswers(body);
        }));

        var result = await service.ExtractAsync(demo, "Ignored for file-only requests", sample, TestCancellation);
        if (isPdf)
            Assert.Null(Assert.IsType<ExtractionAnswer<string?>>(result.Answers["emergency_phone"]).Value);
    }

    [Fact]
    public async Task CancelingTheDemoCancelsTheHttpRequest()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateService(new Handler(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Expected the request to be canceled.");
        }));

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        var request = service.ExtractAsync(ExtractionDemoCatalog.Dispatch, "Dispatch", null, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellation);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task ProviderErrorsAreAvailableToTheWorkspaceForRetry()
    {
        var calls = 0;
        var service = CreateService(new StubHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("Provider error") };
        }));

        var error = await Assert.ThrowsAsync<ExtractionsApiException>(() =>
            service.ExtractAsync(ExtractionDemoCatalog.Dispatch, "Dispatch", null, TestCancellation));
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(1, calls);
    }

    private static ExtractionDemoService CreateService(HttpMessageHandler handler)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["OpenRouter:ApiKey"] = "test-key" }).Build();
        return new ExtractionDemoService(config, new ClientFactory(handler), new WebHostEnvironment
        {
            WebRootFileProvider = new PhysicalFileProvider(WebRoot())
        });
    }

    private static string WebRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "JevDotNet.Demo", "wwwroot");
            if (Directory.Exists(path))
                return path;
        }
        throw new DirectoryNotFoundException("The demo web root was not found.");
    }

    private static HttpResponseMessage NullAnswers(JsonElement request)
    {
        var answers = request.GetProperty("fields").EnumerateObject().ToDictionary(field => field.Name,
            field => new { type = field.Value.GetProperty("type").GetString(), value = (object?)null });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = request.GetProperty("model").GetString(),
                answers,
                usage = new { input_tokens = 3, output_tokens = 0 }
            }))
        };
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(ExtractionDemoService.HttpClientName, name);
            return new HttpClient(handler);
        }
    }

    private sealed class WebHostEnvironment : IWebHostEnvironment
    {
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string ContentRootPath { get; set; } = "";
        public string ApplicationName { get; set; } = "";
        public string EnvironmentName { get; set; } = "";
    }
}
