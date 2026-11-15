using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DecisionsDotNet;

public sealed class OpenRouterChecks
{
    private static readonly Dictionary<string, DecisionsQuestion> Questions = new()
    {
        ["urgent"] = new NoulQuestion(DecisionsValue.FromObject(new { prompt = "Is this urgent?" }),
            new NoulCriteria(True: DecisionsValue.FromObject(new { description = "Time-sensitive" }))),
        ["team"] = new ChoiceQuestion("Which team?", new Dictionary<string, DecisionsValue?>
        {
            ["billing"] = DecisionsValue.FromObject(new { description = "Payments" }),
            ["technical"] = DecisionsValue.FromArray(new[] { "Bugs" })
        }),
        ["severity"] = new ScoreQuestion("How severe?", new DecisionsValue[] { "Low", "High" })
    };

    private static readonly OpenRouterDecisionsClientOptions NoDelay = new()
    {
        MaxAttempts = 5, InitialRetryDelay = TimeSpan.Zero, MaxRetryDelay = TimeSpan.Zero
    };

    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    private static DecisionsRequest Request() =>
        new(DecisionsValue.FromObject(new { message = "Help" }), Questions, "typesafe/jev-1.13");

    [Fact]
    public async Task RequestsAndResponsesFollowTheContractAsync()
    {
        var calls = 0;
        var request = Request();
        using (var client = new OpenRouterDecisionsClient("router-key", new HttpClient(new StubHandler(message =>
        {
            calls++;
            Assert.True(message.Method == HttpMethod.Post, "OpenRouter method");
            Assert.True(message.RequestUri?.ToString() == "https://openrouter.ai/api/alpha/decisions", "OpenRouter URL");
            Assert.True(message.Headers.Authorization?.ToString() == "Bearer router-key", "OpenRouter bearer key");
            Assert.True(message.Content?.Headers.ContentType?.MediaType == "application/json", "OpenRouter content type");
            using var body = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var root = body.RootElement;
            Assert.True(root.GetProperty("model").GetString() == "typesafe/jev-1.13", "OpenRouter model");
            Assert.True(root.GetProperty("state").ValueKind == JsonValueKind.String, "OpenRouter state is a string");
            using var state = JsonDocument.Parse(root.GetProperty("state").GetString()!);
            Assert.True(state.RootElement.GetProperty("message").GetString() == "Help", "OpenRouter object state contents");
            Assert.True(root.GetProperty("questions").GetProperty("urgent").GetProperty("type").GetString() == "noul", "OpenRouter noul");
            Assert.True(root.GetProperty("questions").GetProperty("team").GetProperty("type").GetString() == "choice", "OpenRouter choice");
            Assert.True(root.GetProperty("questions").GetProperty("severity").GetProperty("type").GetString() == "score", "OpenRouter score");
            var sentQuestions = root.GetProperty("questions");
            Assert.True(sentQuestions.GetProperty("urgent").GetProperty("instructions").GetProperty("prompt").GetString() == "Is this urgent?",
                "OpenRouter structured instructions preserved");
            Assert.True(sentQuestions.GetProperty("urgent").GetProperty("criteria").GetProperty("true").GetProperty("description").GetString() == "Time-sensitive",
                "OpenRouter structured noul criteria preserved");
            Assert.True(sentQuestions.GetProperty("team").GetProperty("criteria").GetProperty("billing").GetProperty("description").GetString() == "Payments" &&
                sentQuestions.GetProperty("team").GetProperty("criteria").GetProperty("technical")[0].GetString() == "Bugs",
                "OpenRouter structured choice criteria preserved");
            return Success();
        })), new OpenRouterDecisionsClientOptions
        {
            ModelInputModalitiesOverrides = new Dictionary<string, IReadOnlyList<string>>
            {
                ["typesafe/jev-1.13"] = ["text"]
            }
        }))
        {
            var result = await client.EvaluateAsync(request, TestCancellation);
            Assert.True(result.Model == "typesafe/jev-1.13-20260917", "Resolved OpenRouter model");
            Assert.True(result.Id == "gen-dec-test" && result.Provider == "TypeSafe", "OpenRouter response metadata");
            Assert.True(result.Usage.Cost == 0.000019992m && result.Usage.InputTokens == 476, "OpenRouter usage and cost");
            Assert.True(result.GetAnswer<NoulAnswer>("urgent").Noul == 0.96, "OpenRouter typed noul");
            Assert.True(result.GetAnswer<ChoiceAnswer>("team").Choice == "billing", "OpenRouter typed choice");
            Assert.True(result.GetAnswer<ScoreAnswer>("severity").Score == 1.5, "OpenRouter typed score");
        }
        Assert.True(calls == 1, "One OpenRouter call");
        Assert.True(request.State.ToJsonElement().ValueKind == JsonValueKind.Object &&
            request.State.ToJsonElement().GetProperty("message").GetString() == "Help", "Original object state preserved");
    }

    // Indexes rather than values, so each case shows up by a stable name in the test list.
    private static readonly DecisionsValue[] States =
    [
        DecisionsValue.FromArray(new object[] { "Help", new { nested = new[] { 1, 2 } }, true, 3 }),
        DecisionsValue.Text("Help"), DecisionsValue.Text(""),
        DecisionsValue.Text("Quotes: \"help\"; path: C:\\temp\\file\nNext line\r\nUnicode: café 日本語 😀"),
        DecisionsValue.Text("{\"message\":\"Help\"}")
    ];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task EveryStateIsSentAsAStringAndLeftUnchangedAsync(int index)
    {
        var input = States[index];
        var original = input.ToJsonElement();
        using var client = new OpenRouterDecisionsClient("router-key", new HttpClient(new StubHandler(message =>
        {
            using var body = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var sentState = body.RootElement.GetProperty("state");
            Assert.True(sentState.ValueKind == JsonValueKind.String, "Every OpenRouter state is a string");
            if (original.ValueKind == JsonValueKind.String)
                Assert.True(sentState.GetString() == original.GetString(), "OpenRouter text preserved exactly");
            else
            {
                using var decoded = JsonDocument.Parse(sentState.GetString()!);
                Assert.True(JsonElement.DeepEquals(decoded.RootElement, original), "OpenRouter array contents preserved");
            }
            return Success();
        })));

        await client.EvaluateAsync(new DecisionsRequest(input, Questions, "test/string-only-model"), TestCancellation);

        Assert.True(JsonElement.DeepEquals(input.ToJsonElement(), original), "Original state preserved");
    }

    [Fact]
    public async Task TransientErrorsAreRetriedWithTheSameBodyAsync()
    {
        var retries = 0;
        string? firstRetryBody = null;
        var statuses = new[] { 429, 502, 503, 529 };
        using (var client = new OpenRouterDecisionsClient("router-key", new HttpClient(new StubHandler(message =>
        {
            var retryBody = message.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            firstRetryBody ??= retryBody;
            Assert.True(retryBody == firstRetryBody, "OpenRouter retry bodies are identical");
            using var body = JsonDocument.Parse(retryBody);
            Assert.True(body.RootElement.GetProperty("state").ValueKind == JsonValueKind.String, "OpenRouter retry state is a string");
            var status = retries++;
            if (status >= statuses.Length) return Success();
            var response = new HttpResponseMessage((HttpStatusCode)statuses[status])
            { Content = new StringContent("temporary") };
            if (status == 0) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return response;
        })), NoDelay))
        {
            await client.EvaluateAsync(Request(), TestCancellation);
            Assert.True(retries == 5, "OpenRouter transient retries");
        }
    }

    [Theory]
    [InlineData(402)]
    [InlineData(524)]
    public async Task OtherErrorsAreNotRetriedAndCarryTheProviderDetailAsync(int status)
    {
        var attempts = 0;
        using var client = new OpenRouterDecisionsClient("router-key", new HttpClient(new StubHandler(_ =>
        {
            attempts++;
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("error detail") };
        })), NoDelay);

        var error = await Assert.ThrowsAsync<DecisionsApiException>(() => client.EvaluateAsync(Request(), TestCancellation));

        Assert.True((int)error.StatusCode == status && error.ResponseBody == "error detail" &&
            error.Service == "OpenRouter" && error.Message.Contains("OpenRouter"),
            $"OpenRouter HTTP {status} error detail");
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExhaustedRetriesPreserveTheLastErrorAsync()
    {
        var exhaustedAttempts = 0;
        using (var client = new OpenRouterDecisionsClient("router-key", new HttpClient(new StubHandler(_ =>
        {
            exhaustedAttempts++;
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            { Content = new StringContent("still rate limited") };
        })), new OpenRouterDecisionsClientOptions
        {
            MaxAttempts = 3, InitialRetryDelay = TimeSpan.Zero, MaxRetryDelay = TimeSpan.Zero
        }))
        {
            var error = await Assert.ThrowsAsync<DecisionsApiException>(() => client.EvaluateAsync(Request(), TestCancellation));
            Assert.True(exhaustedAttempts == 3 && error.StatusCode == HttpStatusCode.TooManyRequests &&
                error.ResponseBody == "still rate limited" && error.Service == "OpenRouter",
                "OpenRouter exhausted retries preserve error detail");
        }
    }

    [Fact]
    public async Task AnswerOfTheWrongTypeIsAProtocolErrorAsync()
    {
        using (var client = new OpenRouterDecisionsClient("router-key", new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"model\":\"typesafe/jev-1.13\",\"answers\":{\"urgent\":{\"type\":\"choice\",\"choice\":\"billing\",\"probabilities\":{\"billing\":1},\"confidence\":1}},\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}")
            }))))
        {
            await Assert.ThrowsAnyAsync<JsonException>(() => client.EvaluateAsync(
                new DecisionsRequest("test", new Dictionary<string, DecisionsQuestion>
                {
                    ["urgent"] = new NoulQuestion("Urgent?")
                }, "typesafe/jev-1.13"), TestCancellation));
        }
    }

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""
            {"answers":{"urgent":{"type":"noul","noul":0.96},"team":{"type":"choice","choice":"billing","probabilities":{"billing":0.8,"technical":0.2},"confidence":0.75},"severity":{"type":"score","score":1.5,"legend":{"0":"Low","1":"High"},"probabilities":{"0":0.25,"1":0.75},"confidence":0.8}},"id":"gen-dec-test","model":"typesafe/jev-1.13-20260917","provider":"TypeSafe","usage":{"cost":0.000019992,"input_tokens":476,"output_tokens":70}}
            """)
    };


    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(request.Method == HttpMethod.Get
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                    {"data":[{"id":"typesafe/jev-1.13","architecture":{"input_modalities":["text"]}}]}
                    """) }
                : respond(request));
    }
}
