using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace DecisionsDotNet.Testing;

/// <summary>One question in a request the fake is answering, passed to its match and answer functions.</summary>
/// <param name="Id">The question ID the answer is returned under.</param>
/// <param name="Question">The question.</param>
/// <param name="Request">The whole request, with its state and model.</param>
[Experimental(TestingDiagnostics.Conditions, UrlFormat = TestingDiagnostics.UrlFormat)]
public sealed record FakeQuestion(string Id, DecisionsQuestion Question, DecisionsRequest Request)
{
    /// <summary>The question's instructions when they are plain text, otherwise their JSON.</summary>
    public string Instructions => TextOrJson(Question.Instructions);

    /// <summary>The request state when it is plain text, otherwise its JSON.</summary>
    public string State => TextOrJson(Request.State);

    internal static string TextOrJson(DecisionsValue value)
    {
        var json = value.ToJsonElement();
        return json.ValueKind == JsonValueKind.String ? json.GetString()! : json.GetRawText();
    }
}

/// <summary>
/// An <see cref="IDecisionsClient"/> that answers from scripted rules instead of a provider, for unit tests.
/// Rules are checked newest first, so add broad defaults before specific overrides. A question no rule matches fails the call.
/// </summary>
/// <example>
/// <code>
/// var fake = new FakeDecisionsClient()
///     .AnswerNoul(0.1)                               // every yes/no question: unlikely
///     .AnswerNoul("urgent", 0.95)                     // except this one
///     .AnswerChoice(Team.Billing, probability: 0.8);  // every choice question
/// var model = fake.ForModel("test-model");
/// </code>
/// </example>
[Experimental(TestingDiagnostics.Conditions, UrlFormat = TestingDiagnostics.UrlFormat)]
public sealed class FakeDecisionsClient : IDecisionsClient
{
    private readonly List<(Func<FakeQuestion, bool> Match, Func<FakeQuestion, DecisionsAnswer> Answer)> _rules = [];
    private readonly List<(Func<DecisionsRequest, bool> Match, Func<DecisionsRequest, Exception> Error)> _failures = [];
    private readonly List<DecisionsRequest> _requests = [];
    private readonly Lock _gate = new();

    /// <summary>The provider name reported on every response.</summary>
    public string Provider { get; init; } = "fake";

    /// <summary>Every request received, in order, including ones that failed.</summary>
    public IReadOnlyList<DecisionsRequest> Requests
    {
        get { lock (_gate) return _requests.ToArray(); }
    }

    /// <summary>Answers questions that <paramref name="match"/> accepts with whatever <paramref name="answer"/> returns.</summary>
    public FakeDecisionsClient Answer(Func<FakeQuestion, bool> match, Func<FakeQuestion, DecisionsAnswer> answer)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(answer);
        lock (_gate) _rules.Add((match, answer));
        return this;
    }

    /// <summary>Answers yes/no questions with the given probability of yes, from zero to one.</summary>
    /// <param name="probability">The probability to return. Values outside 0 to 1 are allowed, to test validation.</param>
    /// <param name="when">Limits the rule to matching questions. Null matches every yes/no question.</param>
    public FakeDecisionsClient AnswerNoul(double probability, Func<FakeQuestion, bool>? when = null)
        => Answer(q => q.Question is NoulQuestion && (when?.Invoke(q) ?? true), _ => new NoulAnswer { Noul = probability });

    /// <summary>Answers the yes/no question with the given ID.</summary>
    public FakeDecisionsClient AnswerNoul(string questionId, double probability)
        => AnswerNoul(probability, ById(questionId));

    /// <summary>
    /// Answers choice questions with <paramref name="option"/> at <paramref name="probability"/>, sharing the rest evenly across
    /// the other options offered. An option that was not offered is returned as is, to test validation.
    /// </summary>
    /// <param name="option">The wire label to choose.</param>
    /// <param name="probability">The probability and confidence of the chosen option.</param>
    /// <param name="when">Limits the rule to matching questions. Null matches every choice question.</param>
    public FakeDecisionsClient AnswerChoice(string option, double probability = 1, Func<FakeQuestion, bool>? when = null)
    {
        ArgumentNullException.ThrowIfNull(option);
        return Answer(q => q.Question is ChoiceQuestion && (when?.Invoke(q) ?? true),
            q => Choice(((ChoiceQuestion)q.Question).Criteria.Keys, option, probability));
    }

    /// <summary>Answers the choice question with the given ID.</summary>
    public FakeDecisionsClient AnswerChoice(string questionId, string option, double probability = 1)
        => AnswerChoice(option, probability, ById(questionId));

    /// <summary>Answers choice questions with an enum member, whose name is the wire label <see cref="DecisionModel.ChooseAsync{TEnum}(DecisionsValue, DecisionsValue, CancellationToken)"/> sends.</summary>
    public FakeDecisionsClient AnswerChoice<TEnum>(TEnum option, double probability = 1, Func<FakeQuestion, bool>? when = null)
        where TEnum : struct, Enum
        => AnswerChoice(Enum.GetName(option) ?? throw new ArgumentException($"{option} is not a named member.", nameof(option)),
            probability, when);

    /// <summary>
    /// Answers score questions with the given probability-weighted level, counted from zero. The probabilities are split
    /// between the two nearest levels so they average to <paramref name="score"/>.
    /// </summary>
    /// <param name="score">The score to return. Values outside the levels are allowed, to test validation.</param>
    /// <param name="when">Limits the rule to matching questions. Null matches every score question.</param>
    public FakeDecisionsClient AnswerScore(double score, Func<FakeQuestion, bool>? when = null)
        => Answer(q => q.Question is ScoreQuestion && (when?.Invoke(q) ?? true), q => Score((ScoreQuestion)q.Question, score));

    /// <summary>Answers the score question with the given ID.</summary>
    public FakeDecisionsClient AnswerScore(string questionId, double score) => AnswerScore(score, ById(questionId));

    /// <summary>Refuses matching questions, as the OpenAI Decisions API can.</summary>
    /// <param name="when">Limits the rule to matching questions. Null matches every question.</param>
    public FakeDecisionsClient Refuse(Func<FakeQuestion, bool>? when = null)
        => Answer(q => when?.Invoke(q) ?? true, _ => new RefusalAnswer());

    /// <summary>Refuses the question with the given ID.</summary>
    public FakeDecisionsClient Refuse(string questionId) => Refuse(ById(questionId));

    /// <summary>Fails matching requests with the given exception, as a provider or network failure would.</summary>
    /// <param name="error">The exception to throw.</param>
    /// <param name="when">Limits the failure to matching requests. Null fails every request.</param>
    public FakeDecisionsClient Throw(Exception error, Func<DecisionsRequest, bool>? when = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (_gate) _failures.Add((request => when?.Invoke(request) ?? true, _ => error));
        return this;
    }

    /// <inheritdoc />
    public Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        (Func<FakeQuestion, bool> Match, Func<FakeQuestion, DecisionsAnswer> Answer)[] rules;
        int number;
        lock (_gate)
        {
            _requests.Add(request);
            number = _requests.Count;
            for (var i = _failures.Count - 1; i >= 0; i--)
                if (_failures[i].Match(request)) return Task.FromException<DecisionsResponse>(_failures[i].Error(request));
            rules = _rules.ToArray();
        }

        var answers = new Dictionary<string, DecisionsAnswer>(StringComparer.Ordinal);
        foreach (var (id, question) in request.Questions)
        {
            var fake = new FakeQuestion(id, question, request);
            var rule = Array.FindLast(rules, candidate => candidate.Match(fake));
            if (rule.Answer is null)
                return Task.FromException<DecisionsResponse>(new InvalidOperationException(
                    $"FakeDecisionsClient has no answer for {question.GetType().Name} '{id}': \"{fake.Instructions}\". " +
                    "Add an Answer*, Refuse, or Throw rule that matches it."));
            answers[id] = rule.Answer(fake);
        }

        return Task.FromResult(new DecisionsResponse
        {
            Id = $"fake-{number.ToString(CultureInfo.InvariantCulture)}",
            Provider = Provider,
            Model = request.Model,
            Answers = answers,
            Usage = new DecisionsUsage { InputTokens = 0, OutputTokens = 0, Cost = 0 },
        });
    }

    private static Func<FakeQuestion, bool> ById(string questionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(questionId);
        return q => string.Equals(q.Id, questionId, StringComparison.Ordinal);
    }

    private static ChoiceAnswer Choice(IEnumerable<string> offered, string option, double probability)
    {
        var others = offered.Where(label => !string.Equals(label, option, StringComparison.Ordinal)).ToArray();
        var probabilities = others.ToDictionary(label => label, _ => others.Length == 0 ? 0 : (1 - probability) / others.Length,
            StringComparer.Ordinal);
        probabilities[option] = probability;
        return new ChoiceAnswer { Choice = option, Probabilities = probabilities, Confidence = probability };
    }

    private static ScoreAnswer Score(ScoreQuestion question, double score)
    {
        var count = question.Criteria.Count;
        var clamped = double.IsFinite(score) ? Math.Clamp(score, 0, count - 1) : 0;
        var lower = (int)Math.Floor(clamped);
        var upper = Math.Min(lower + 1, count - 1);
        var probabilities = Enumerable.Range(0, count).ToDictionary(i => i.ToString(CultureInfo.InvariantCulture), _ => 0.0);
        probabilities[upper.ToString(CultureInfo.InvariantCulture)] += clamped - lower;
        probabilities[lower.ToString(CultureInfo.InvariantCulture)] += 1 - (clamped - lower);
        return new ScoreAnswer
        {
            Score = score,
            Legend = Enumerable.Range(0, count).ToDictionary(i => i.ToString(CultureInfo.InvariantCulture),
                i => FakeQuestion.TextOrJson(question.Criteria[i])),
            Probabilities = probabilities,
            Confidence = probabilities.Values.Max(),
        };
    }
}
