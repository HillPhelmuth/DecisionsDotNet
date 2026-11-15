// Touches the main entry points of each package without making a network call.
using DecisionsDotNet;
using DecisionsDotNet.AI;
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
Check(request.Questions.Count == 2 && extraction.Fields.Count == 1, "Requests build");

foreach (var type in new[] { typeof(IDecisionsClient), typeof(DecisionsAIFunctions), typeof(IExtractionsClient) })
    Console.WriteLine($"{type.Assembly.GetName().Name} {type.Assembly.GetName().Version}");
Console.WriteLine("Package smoke check passed.");

static void Check(bool condition, string what)
{
    if (!condition) throw new InvalidOperationException($"Package smoke check failed: {what}");
}
