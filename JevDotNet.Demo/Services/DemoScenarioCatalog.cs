using System.ComponentModel;
using DecisionsDotNet;

namespace JevDotNet.Demo.Services;

public sealed record DemoScenario(
    string Slug,
    string Icon,
    string Title,
    string Summary,
    string InputLabel,
    string SampleInput,
    string Pattern);

public sealed record DemoRecommendation(string Title, string Detail);

public static class DemoScenarioCatalog
{
    public const int MaxInputLength = 8000;

    public static IReadOnlyList<DemoScenario> All { get; } =
    [
        new("support", "◎", "Support triage",
            "Route a customer message, spot urgency, and gauge frustration in one call.",
            "Customer message",
            "My card was charged twice and I still can't access my account. Can someone fix this today?",
            "Choice + Noul + Score"),
        new("content", "◇", "Content review",
            "Compare a post with simple community rules and suggest a human review priority.",
            "Community post",
            "You've posted the same promotional link in every thread today. Please stop spamming the forum.",
            "Policy check + category + severity"),
        new("leads", "↗", "Lead qualification",
            "Identify inquiry intent, estimate customer fit, and spot demo interest.",
            "Inbound inquiry",
            "I'm the operations lead at a 120-person SaaS company. We're comparing workflow tools and would like a demo next week.",
            "Intent + interest + fit")
    ];

    public static DemoScenario? Find(string slug)
        => All.FirstOrDefault(scenario => string.Equals(scenario.Slug, slug, StringComparison.OrdinalIgnoreCase));

    /// <summary>Checks the input and returns the state the scenario sends, without the questions.</summary>
    public static DecisionsValue BuildInput(string slug, string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Enter some text to evaluate.", nameof(input));
        if (input.Length > MaxInputLength)
            throw new ArgumentException($"Keep input under {MaxInputLength:N0} characters.", nameof(input));

        return slug.ToLowerInvariant() switch
        {
            "support" => DecisionsValue.FromObject(new { ticket = input }),
            "content" => DecisionsValue.FromObject(new
            {
                post = input,
                community_rules = new[]
                {
                    "No repeated unsolicited promotions or links.",
                    "No insults, threats, or targeted harassment."
                }
            }),
            "leads" => DecisionsValue.FromObject(new
            {
                inquiry = input,
                ideal_customer_profile = "Business teams evaluating workflow software for a company with at least 50 employees."
            }),
            _ => throw new ArgumentException("Unknown demo scenario.", nameof(slug))
        };
    }

    /// <summary>
    /// Asks the scenario's decision set in one request and turns the typed answers into a recommendation.
    /// Throws <see cref="ArgumentException"/> for bad input before anything is sent.
    /// </summary>
    public static async Task<(DecisionsResponse Response, DemoRecommendation Recommendation)> EvaluateAsync(
        string slug, string input, DecisionModel model, CancellationToken cancellationToken = default)
    {
        var state = BuildInput(slug, input);
        switch (slug.ToLowerInvariant())
        {
            case "support":
                var support = await model.DecideWithResponseAsync<SupportTriage>(state, cancellationToken);
                return (support.Response, Recommend(support.Value));
            case "content":
                var content = await model.DecideWithResponseAsync<ContentReview>(state, cancellationToken);
                return (content.Response, Recommend(content.Value));
            case "leads":
                var lead = await model.DecideWithResponseAsync<LeadQualification>(state, cancellationToken);
                return (lead.Response, Recommend(lead.Value));
            default:
                throw new ArgumentException("Unknown demo scenario.", nameof(slug));
        }
    }

    /// <summary>The questions a scenario asks, for the page to show before anything is sent.</summary>
    public static IReadOnlyDictionary<string, DecisionsQuestion> Questions(string slug) => slug.ToLowerInvariant() switch
    {
        "support" => QuestionPreview.Of<SupportTriage>(),
        "content" => QuestionPreview.Of<ContentReview>(),
        "leads" => QuestionPreview.Of<LeadQualification>(),
        _ => throw new ArgumentException("Unknown demo scenario.", nameof(slug))
    };

    public static DemoRecommendation Recommend(SupportTriage triage)
    {
        if (triage.Team.SelectedProbability < 0.65)
            return new("Human review suggested", "Team confidence is below 0.65; let a person choose the route.");
        var priority = triage.Urgent || triage.Frustration.Score >= 1.5 ? "priority" : "standard";
        return new($"Route to {triage.Team.Value.ToString().ToLowerInvariant()} · {priority}",
            "Demo logic: urgency of at least 0.70 or frustration of at least 1.5 raises priority.");
    }

    public static DemoRecommendation Recommend(ContentReview review)
    {
        if (review.Category.SelectedProbability < 0.65)
            return new("Human review suggested", "Category confidence is below 0.65; no automated action is taken.");
        return review.PossibleViolation.Outcome switch
        {
            VerdictOutcome.Yes when review.Severity.Score >= 1.5 =>
                new("Priority human review", "Demo logic: possible violation is above 0.70 and severity is at least 1.5."),
            VerdictOutcome.No =>
                new("Low review priority", "Demo logic: possible violation is below 0.40. No enforcement action is taken."),
            _ => new("Human review suggested", "Demo logic: possible violation is at least 0.40, or the model declined to judge it. No enforcement action is taken.")
        };
    }

    public static DemoRecommendation Recommend(LeadQualification lead)
    {
        if (lead.Intent.SelectedProbability < 0.65)
            return new("Manual qualification suggested", "Intent confidence is below 0.65; a person should review the inquiry.");
        if (lead.Intent.Value == LeadIntent.Buying && lead.DemoInterest && lead.Fit.Score >= 1.5)
            return new("Sales follow-up suggested", "Demo logic: buying intent, demo interest of at least 0.70, and fit of at least 1.5.");
        return new("Standard follow-up", "Demo logic: the inquiry does not meet all three sales follow-up conditions.");
    }

    /// <summary>Reads a decision set's questions by letting the SDK build its request and stopping it before it is sent.</summary>
    private static class QuestionPreview
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, IReadOnlyDictionary<string, DecisionsQuestion>> Cache = new();

        public static IReadOnlyDictionary<string, DecisionsQuestion> Of<T>() where T : class, new()
            => Cache.GetOrAdd(typeof(T), static type =>
            {
                var capture = new CaptureClient();
                // CaptureClient throws synchronously, so the returned task has already faulted and nothing blocks.
                var task = capture.ForModel("preview").DecideAsync<T>("Preview");
                if (capture.Request is null) task.GetAwaiter().GetResult();
                _ = task.Exception; // Observe the expected fault.
                return capture.Request!.Questions;
            });

        private sealed class CaptureClient : IDecisionsClient
        {
            public DecisionsRequest? Request { get; private set; }

            public Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
            {
                Request = request;
                return Task.FromException<DecisionsResponse>(new OperationCanceledException("Preview only."));
            }
        }
    }
}

// Each scenario is one decision set: every property is a question, all sent in a single request.
// Question IDs stay snake_case so the page shows the same names as the wire format.

public enum SupportTeam
{
    [Description("Payments, invoices, and refunds")] Billing,
    [Description("Product bugs, outages, or access failures")] Technical,
    [Description("Plans, profiles, and account changes")] Account,
    [Description("Anything outside these teams")] Other
}

public enum Frustration
{
    [Description("Calm")] Calm,
    [Description("Frustrated but civil")] FrustratedButCivil,
    [Description("Very upset")] VeryUpset
}

public sealed class SupportTriage
{
    [Choice("Which team should handle this customer message?", Id = "team")]
    public DecisionChoice<SupportTeam> Team { get; init; } = null!;

    [Noul("Does this customer message need a prompt response?", Id = "urgent", MinProbability = 0.7,
        WhenTrue = "Explicitly time-sensitive or blocking the customer", WhenFalse = "Can wait for the normal queue")]
    public bool Urgent { get; init; }

    [Score("How frustrated does the customer sound?", Id = "frustration")]
    public DecisionScore<Frustration> Frustration { get; init; } = null!;
}

public enum ContentCategory
{
    [Description("Repeated unsolicited promotion or links")] Spam,
    [Description("Insults, threats, or targeted abuse")] Harassment,
    [Description("Ordinary discussion or criticism")] Ordinary,
    [Description("Does not clearly fit the other categories")] Other
}

public enum ModerationSeverity
{
    [Description("Routine")] Routine,
    [Description("Needs review")] NeedsReview,
    [Description("Urgent human review")] UrgentHumanReview
}

public sealed class ContentReview
{
    [Noul("Does the post itself potentially violate a community rule?", Id = "possible_violation", UnsureMin = 0.4, UnsureMax = 0.7,
        WhenTrue = "The post contains a potential violation", WhenFalse = "The post does not contain a potential violation")]
    public DecisionVerdict PossibleViolation { get; init; } = null!;

    [Choice("Which category best describes the post itself?", Id = "category")]
    public DecisionChoice<ContentCategory> Category { get; init; } = null!;

    [Score("How much attention might this post need from a moderator?", Id = "severity")]
    public DecisionScore<ModerationSeverity> Severity { get; init; } = null!;
}

public enum LeadIntent
{
    [Description("Evaluating or purchasing workflow software")] Buying,
    [Description("General information gathering")] Research,
    [Description("Help with an existing product")] Support,
    [Description("Another intent")] Other
}

public enum CustomerFit
{
    [Description("Little evidence of fit")] Little,
    [Description("Some evidence of fit")] Some,
    [Description("Strong evidence of fit")] Strong
}

public sealed class LeadQualification
{
    [Choice("What is the primary intent of this inquiry?", Id = "intent")]
    public DecisionChoice<LeadIntent> Intent { get; init; } = null!;

    [Noul("Does the sender explicitly ask for a product demo?", Id = "demo_interest", MinProbability = 0.7)]
    public bool DemoInterest { get; init; }

    [Score("How closely does this inquiry match the ideal customer profile?", Id = "fit")]
    public DecisionScore<CustomerFit> Fit { get; init; } = null!;
}
