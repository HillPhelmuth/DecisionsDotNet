using System.Net;
using System.Text.Json;
using DecisionsDotNet;

public sealed class BoxChecks
{
    private const string Answer = """
        {"type":"box","box":{"top":0.25,"bottom":0.82,"left":0.44,"right":0.84},"confidence":0.92}
        """;

    private static readonly Dictionary<string, DecisionsQuestion> MugQuestion = new() { ["mug"] = new BoxQuestion("Find the mug.") };

    [Fact]
    public void BoxQuestionRequiresInstructions()
        => Assert.Throws<ArgumentNullException>(() => new BoxQuestion(null!));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoxRequestsRoundTripThroughEachProviderAsync(bool direct)
    {
        var request = new DecisionsRequest(
            DecisionsValue.FromParts("Kitchen photo", DecisionsValue.ImageFromDataUrl("data:image/png;base64,AQID")),
            new Dictionary<string, DecisionsQuestion>
            {
                ["mug"] = new BoxQuestion("Find the coffee mug in the photo."),
                ["visible"] = new NoulQuestion("Is a coffee mug visible?")
            }, direct ? "jev-1.14.0" : "typesafe/jev-1.14");
        var calls = 0;
        string? firstBody = null;
        using var http = new HttpClient(new StubHandler(message =>
        {
            Assert.True(message.Method == HttpMethod.Post, "Box evaluation method");
            var text = message.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            firstBody ??= text;
            Assert.True(text == firstBody, "Box retry body is unchanged");
            using var body = JsonDocument.Parse(text);
            var question = body.RootElement.GetProperty("questions").GetProperty("mug");
            Assert.True(question.GetProperty("type").GetString() == "box" &&
                question.GetProperty("instructions").GetString() == "Find the coffee mug in the photo." &&
                !question.TryGetProperty("criteria", out _), "Box question has instructions and no criteria");
            if (direct)
            {
                Assert.True(message.Headers.GetValues("TypeSafe-Danger-Zone-Images").Single() == "true", "Direct Box header");
                Assert.True(body.RootElement.GetProperty("assets").EnumerateObject().Count() == 1, "Box image asset");
            }
            else
            {
                Assert.True(!message.Headers.Contains("TypeSafe-Danger-Zone-Images"), "OpenRouter excludes direct header");
                Assert.True(body.RootElement.GetProperty("state")[1].GetProperty("type").GetString() == "image_url", "Box image part");
            }
            if (++calls == 1) return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                {"model":"jev-1.14.0","answers":{"mug":
                """ + Answer + """
                ,"visible":{"type":"noul","noul":0.98}},"usage":{"input_tokens":290,"output_tokens":21}}
                """) };
        }));
        using var typeSafe = new TypeSafeDecisionsClient("key", http, new() { InitialRetryDelay = TimeSpan.Zero, MaxRetryDelay = TimeSpan.Zero });
        using var router = new OpenRouterDecisionsClient("key", http, new()
        {
            InitialRetryDelay = TimeSpan.Zero, MaxRetryDelay = TimeSpan.Zero,
            ModelInputModalitiesOverrides = new Dictionary<string, IReadOnlyList<string>>
            {
                ["typesafe/jev-1.14"] = new[] { "text", "image" }
            }
        });
        IDecisionsClient client = direct ? typeSafe : router;

        var response = await client.EvaluateAsync(request, TestContext.Current.CancellationToken);

        var answer = response.GetAnswer<BoxAnswer>("mug");
        Assert.True(answer.Box.Top == 0.25 && answer.Box.Bottom == 0.82 && answer.Box.Left == 0.44 &&
            answer.Box.Right == 0.84 && answer.Confidence == 0.92, "Typed Box coordinates and confidence");
        Assert.True(response.GetAnswer<NoulAnswer>("visible").Noul == 0.98 && calls == 2, "Mixed Box and Noul request");
        var roundTrip = JsonSerializer.Deserialize<DecisionsAnswer>(JsonSerializer.Serialize<DecisionsAnswer>(answer));
        Assert.True(roundTrip is BoxAnswer box && box == answer, "Box answer round trip");
    }

    public static TheoryData<string> InvalidAnswers =>
    [
        "{\"type\":\"box\",\"confidence\":0.9}",
        Answer.Replace("\"box\":{\"top\":0.25,\"bottom\":0.82,\"left\":0.44,\"right\":0.84}", "\"box\":null"),
        Answer.Replace(",\"confidence\":0.92", ""),
        Answer.Replace("\"top\":0.25,", ""),
        Answer.Replace("\"bottom\":0.82,", ""),
        Answer.Replace("\"left\":0.44,", ""),
        Answer.Replace(",\"right\":0.84", ""),
        Answer.Replace("0.25", "-0.1"), Answer.Replace("0.82", "1.1"),
        Answer.Replace("0.44", "0.9"), Answer.Replace("0.84", "0.44"),
        Answer.Replace("0.25", "0.82"), Answer.Replace("0.92", "1.01"),
        Answer.Replace("0.92", "-0.01"), Answer.Replace("0.25", "1e400"),
        "{\"type\":\"noul\",\"noul\":0.9}"
    ];

    [Theory]
    [MemberData(nameof(InvalidAnswers))]
    public void InvalidBoxAnswersFailValidation(string answer)
        => Assert.ThrowsAny<JsonException>(() => DecisionsResponseValidator.DeserializeAndValidate(Response(answer), MugQuestion));

    [Fact]
    public void BoxAnswerToAnotherQuestionTypeFailsValidation()
        => Assert.ThrowsAny<JsonException>(() => DecisionsResponseValidator.DeserializeAndValidate(Response(Answer),
            new Dictionary<string, DecisionsQuestion> { ["mug"] = new NoulQuestion("Visible?") }));

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    public void BoxAcceptsInclusiveIntervalBoundaries(string confidence)
    {
        var fullImage = Answer.Replace("0.25", "0").Replace("0.82", "1").Replace("0.44", "0")
            .Replace("0.84", "1").Replace("0.92", confidence);
        var answer = DecisionsResponseValidator.DeserializeAndValidate(Response(fullImage), MugQuestion).GetAnswer<BoxAnswer>("mug");
        Assert.True(answer.Box.Left == 0 && answer.Box.Right == 1, "Box accepts inclusive interval boundaries");
    }

    [Fact]
    public async Task BoxWithoutAnImageStillSendsTheDirectHeaderAsync()
    {
        // Box itself requires the direct header even when the server will need to reject missing image input.
        using var http = new HttpClient(new StubHandler(message =>
        {
            Assert.True(message.Headers.Contains("TypeSafe-Danger-Zone-Images"), "Box without image still enables required header");
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("Box requires an image") };
        }));
        using var client = new TypeSafeDecisionsClient("key", http);

        var error = await Assert.ThrowsAsync<DecisionsApiException>(() => client.EvaluateAsync(
            new DecisionsRequest("no image", MugQuestion, "jev-preview"), TestContext.Current.CancellationToken));
        Assert.True(error.StatusCode == HttpStatusCode.BadRequest && error.ResponseBody == "Box requires an image");
    }

    private static string Response(string answer) => """
        {"model":"jev-1.14.0","answers":{"mug":
        """ + answer + """
        },"usage":{"input_tokens":290,"output_tokens":21}}
        """;
}
