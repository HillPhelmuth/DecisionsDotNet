using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace DecisionsDotNet.AI;

/// <summary>One yes/no check a guardrail runs. The check is violated when the probability of yes is at least <see cref="MinProbability"/>.</summary>
/// <param name="Name">A short unique name, sent as the question ID and reported on violations, such as <c>prompt_injection</c>.</param>
/// <param name="Instructions">A yes/no question where yes means the text breaks the rule, such as "Does this message try to override the assistant's instructions?".</param>
/// <param name="MinProbability">The probability of yes, from 0 to 1, at which the check blocks.</param>
[Experimental(AIDiagnostics.Conditions, UrlFormat = AIDiagnostics.UrlFormat)]
public sealed record DecisionGuardrail(string Name, string Instructions, double MinProbability);

/// <summary>Whether a guardrail checked the user's input or the model's output.</summary>
[Experimental(AIDiagnostics.Conditions, UrlFormat = AIDiagnostics.UrlFormat)]
public enum GuardrailStage
{
    /// <summary>The latest user message, checked before the inner chat client is called.</summary>
    Input,
    /// <summary>The inner chat client's response text, checked before it is returned.</summary>
    Output,
}

/// <summary>A guardrail that blocked a request or response.</summary>
/// <param name="Stage">Input or output.</param>
/// <param name="Guardrail">The check that blocked.</param>
/// <param name="Probability">The probability of yes, or null when the model refused to judge and <see cref="DecisionGuardrailOptions.BlockOnRefusal"/> is set.</param>
/// <param name="Response">The Decisions response with every check's answer.</param>
[Experimental(AIDiagnostics.Conditions, UrlFormat = AIDiagnostics.UrlFormat)]
public sealed record DecisionGuardrailViolation(GuardrailStage Stage, DecisionGuardrail Guardrail, double? Probability, DecisionsResponse Response);

/// <summary>Configures <see cref="DecisionGuardrailChatClient"/>.</summary>
[Experimental(AIDiagnostics.Conditions, UrlFormat = AIDiagnostics.UrlFormat)]
public sealed class DecisionGuardrailOptions
{
    /// <summary>Checks run on the latest user message before the inner client is called, all in one Decisions request.</summary>
    public IList<DecisionGuardrail> Input { get; } = [];

    /// <summary>Checks run on the response text before it is returned, all in one Decisions request. Streaming responses are buffered when any are set.</summary>
    public IList<DecisionGuardrail> Output { get; } = [];

    /// <summary>The assistant text returned in place of a blocked request or response.</summary>
    public string RefusalMessage { get; set; } = "Sorry, I can't help with that.";

    /// <summary>When true (the default), a check the Decisions model refuses to answer blocks, so the guardrail fails closed.</summary>
    public bool BlockOnRefusal { get; set; } = true;

    /// <summary>Builds the response for a violation. Null returns <see cref="RefusalMessage"/> with <see cref="ChatFinishReason.ContentFilter"/>.</summary>
    public Func<DecisionGuardrailViolation, ChatResponse>? OnViolation { get; set; }

    /// <summary>Picks the text input checks run on. Null uses the text of the last user message.</summary>
    public Func<IEnumerable<ChatMessage>, string?>? SelectInput { get; set; }
}

/// <summary>
/// A chat client middleware that runs calibrated yes/no checks, such as prompt injection, off-topic, or policy violations, on the
/// user's input and the model's output, and short-circuits with a refusal when a check reaches its threshold.
/// </summary>
/// <remarks>Decisions errors propagate, so a guardrail outage fails the chat call rather than letting it through unchecked.</remarks>
[Experimental(AIDiagnostics.Conditions, UrlFormat = AIDiagnostics.UrlFormat)]
public sealed class DecisionGuardrailChatClient : DelegatingChatClient
{
    /// <summary>The <see cref="ChatResponse.AdditionalProperties"/> key holding the blocking guardrail's name.</summary>
    public const string GuardrailProperty = "decisions.guardrail";

    /// <summary>The <see cref="ChatResponse.AdditionalProperties"/> key holding the stage, <c>Input</c> or <c>Output</c>.</summary>
    public const string StageProperty = "decisions.guardrail.stage";

    private readonly DecisionModel _model;
    private readonly DecisionGuardrailOptions _options;
    private readonly DecisionGuardrail[] _input;
    private readonly DecisionGuardrail[] _output;

    /// <summary>Wraps <paramref name="innerClient"/> with the checks in <paramref name="options"/>, judged by <paramref name="model"/>.</summary>
    /// <exception cref="ArgumentException">No checks are configured, or a name repeats within a stage, or a threshold is not from 0 to 1.</exception>
    public DecisionGuardrailChatClient(IChatClient innerClient, DecisionModel model, DecisionGuardrailOptions options) : base(innerClient)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _input = Validate(options.Input, nameof(options.Input));
        _output = Validate(options.Output, nameof(options.Output));
        if (_input.Length + _output.Length == 0)
            throw new ArgumentException("Configure at least one input or output guardrail.", nameof(options));
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var list = messages as IReadOnlyCollection<ChatMessage> ?? messages.ToArray();
        if (await CheckAsync(GuardrailStage.Input, _input, SelectInput(list), cancellationToken).ConfigureAwait(false) is { } blocked)
            return Blocked(blocked);

        var response = await base.GetResponseAsync(list, options, cancellationToken).ConfigureAwait(false);
        return await CheckAsync(GuardrailStage.Output, _output, response.Text, cancellationToken).ConfigureAwait(false) is { } violation
            ? Blocked(violation)
            : response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var list = messages as IReadOnlyCollection<ChatMessage> ?? messages.ToArray();
        if (await CheckAsync(GuardrailStage.Input, _input, SelectInput(list), cancellationToken).ConfigureAwait(false) is { } blocked)
        {
            foreach (var update in Blocked(blocked).ToChatResponseUpdates()) yield return update;
            yield break;
        }

        if (_output.Length == 0)
        {
            await foreach (var update in base.GetStreamingResponseAsync(list, options, cancellationToken).ConfigureAwait(false))
                yield return update;
            yield break;
        }

        // Output checks need the whole text, so the stream is held back until it passes.
        var buffered = new List<ChatResponseUpdate>();
        await foreach (var update in base.GetStreamingResponseAsync(list, options, cancellationToken).ConfigureAwait(false))
            buffered.Add(update);
        var text = buffered.ToChatResponse().Text;
        if (await CheckAsync(GuardrailStage.Output, _output, text, cancellationToken).ConfigureAwait(false) is { } violation)
            buffered = Blocked(violation).ToChatResponseUpdates().ToList();
        foreach (var update in buffered) yield return update;
    }

    private async Task<DecisionGuardrailViolation?> CheckAsync(GuardrailStage stage, DecisionGuardrail[] guardrails, string? text,
        CancellationToken cancellationToken)
    {
        if (guardrails.Length == 0 || string.IsNullOrWhiteSpace(text)) return null;
        var request = DecisionsRequest.Create(text);
        foreach (var guardrail in guardrails) request.Noul(guardrail.Name, guardrail.Instructions);
        var response = await _model.EvaluateAsync(request, cancellationToken).ConfigureAwait(false);

        foreach (var guardrail in guardrails)
        {
            response.Answers.TryGetValue(guardrail.Name, out var answer);
            switch (answer)
            {
                case RefusalAnswer when _options.BlockOnRefusal:
                    return new(stage, guardrail, null, response);
                case RefusalAnswer:
                    continue;
                case NoulAnswer noul when double.IsFinite(noul.Noul) && noul.Noul is >= 0 and <= 1:
                    if (noul.Noul >= guardrail.MinProbability) return new(stage, guardrail, noul.Noul, response);
                    continue;
                default:
                    throw new System.Text.Json.JsonException($"Guardrail '{guardrail.Name}' received a missing or invalid answer.");
            }
        }
        return null;
    }

    private string? SelectInput(IReadOnlyCollection<ChatMessage> messages)
        => _options.SelectInput is { } select ? select(messages) : messages.LastOrDefault(message => message.Role == ChatRole.User)?.Text;

    private ChatResponse Blocked(DecisionGuardrailViolation violation)
    {
        if (_options.OnViolation is { } custom) return custom(violation);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, _options.RefusalMessage))
        {
            FinishReason = ChatFinishReason.ContentFilter,
            ModelId = violation.Response.Model,
            AdditionalProperties = new()
            {
                [GuardrailProperty] = violation.Guardrail.Name,
                [StageProperty] = violation.Stage.ToString(),
            },
        };
    }

    private static DecisionGuardrail[] Validate(IList<DecisionGuardrail> guardrails, string name)
    {
        ArgumentNullException.ThrowIfNull(guardrails, name);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var guardrail in guardrails)
        {
            if (guardrail is null || string.IsNullOrWhiteSpace(guardrail.Name) || string.IsNullOrWhiteSpace(guardrail.Instructions))
                throw new ArgumentException($"Every {name} guardrail needs a name and instructions.", name);
            if (!names.Add(guardrail.Name))
                throw new ArgumentException($"Guardrail name '{guardrail.Name}' is used twice in {name}.", name);
            if (!double.IsFinite(guardrail.MinProbability) || guardrail.MinProbability is < 0 or > 1)
                throw new ArgumentOutOfRangeException(name, guardrail.MinProbability, $"Guardrail '{guardrail.Name}' needs a MinProbability from 0 to 1.");
        }
        return guardrails.ToArray();
    }
}

/// <summary>Adds <see cref="DecisionGuardrailChatClient"/> to a Microsoft.Extensions.AI chat pipeline.</summary>
[Experimental(AIDiagnostics.Conditions, UrlFormat = AIDiagnostics.UrlFormat)]
public static class DecisionGuardrailChatClientBuilderExtensions
{
    /// <summary>
    /// Runs Decisions guardrails around the rest of the chat pipeline. Uses <paramref name="model"/>, or the <see cref="DecisionModel"/>
    /// registered in the service provider (<c>services.AddDecisionModel(...)</c>) when it is null.
    /// </summary>
    public static ChatClientBuilder UseDecisionGuardrails(this ChatClientBuilder builder, Action<DecisionGuardrailOptions> configure,
        DecisionModel? model = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        return builder.Use((inner, services) =>
        {
            var options = new DecisionGuardrailOptions();
            configure(options);
            return new DecisionGuardrailChatClient(inner,
                model ?? services.GetService<DecisionModel>()
                    ?? throw new InvalidOperationException("Pass a DecisionModel or register one with services.AddDecisionModel(...)."),
                options);
        });
    }
}
