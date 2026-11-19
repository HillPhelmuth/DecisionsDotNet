// Touches the main entry points of each package without making a network call.
using DecisionsDotNet;
using DecisionsDotNet.AI;
using DecisionsDotNet.Caching;
using DecisionsDotNet.Pipeline;
using DecisionsDotNet.Testing;
using ExtractionsDotNet;
using Microsoft.Extensions.DependencyInjection;

var request = DecisionsRequest.Create("Charged twice")
    .Noul("refund", "Wants money back?")
    .Choice("team", "Which team?", "billing", "other")
    .Build(TypeSafeDecisionsClient.DefaultModel);

var services = new ServiceCollection();
services.AddTypeSafeDecisionsClient("placeholder-key");
services.AddKeyedOpenRouterDecisionsClient("router", "placeholder-key");
services.AddOpenRouterExtractionsClient("placeholder-key");
using var provider = services.BuildServiceProvider();

var decisions = provider.GetRequiredService<IDecisionsClient>();
var router = provider.GetRequiredKeyedService<IDecisionsClient>("router");
var extractions = provider.GetRequiredService<IExtractionsClient>();
var tool = decisions.AsAIFunction("triage_ticket", "Scores a ticket.", request.Questions, request.Model);
var extraction = new ExtractionRequest("cloudflare/clairic",
    new Dictionary<string, ExtractionField> { ["total"] = new NumberField() }) { State = "Total 12.50" };

Check(decisions is TypeSafeDecisionsClient, "DecisionsDotNet resolves its client");
Check(router is OpenRouterDecisionsClient, "DecisionsDotNet resolves a keyed client");
Check(extractions is OpenRouterExtractionsClient, "ExtractionsDotNet resolves its client");
Check(tool.Name == "triage_ticket", "DecisionsDotNet.AI builds a tool");
var fake = new FakeDecisionsClient().AnswerNoul(0.9);
Check(await fake.ForModel("smoke-model").MatchesAsync("Charged twice", "Wants money back?", minProbability: 0.5),
    "DecisionsDotNet.Testing answers a conditional verb");
var pipelineServices = new ServiceCollection();
pipelineServices.AddHybridCache();
pipelineServices.AddKeyedSingleton<IDecisionsClient>("fake", fake);
pipelineServices.AddDecisionsClient(sp => sp.GetRequiredKeyedService<IDecisionsClient>("fake")).UseCaching().UseLogging().UseOpenTelemetry();
using var pipelineProvider = pipelineServices.BuildServiceProvider();
var piped = await pipelineProvider.GetRequiredService<IDecisionsClient>().ForModel("smoke-model").ProbabilityAsync("x", "Q?");
Check(piped == 0.9, "DecisionsDotNet.Caching runs in a pipeline");
Check(request.Questions.Count == 2 && extraction.Fields.Count == 1, "Requests build");

foreach (var type in new[] { typeof(IDecisionsClient), typeof(DecisionsAIFunctions), typeof(FakeDecisionsClient), typeof(CachingDecisionsClient), typeof(IExtractionsClient) })
    Console.WriteLine($"{type.Assembly.GetName().Name} {type.Assembly.GetName().Version}");
Console.WriteLine("Package smoke check passed.");

static void Check(bool condition, string what)
{
    if (!condition) throw new InvalidOperationException($"Package smoke check failed: {what}");
}
