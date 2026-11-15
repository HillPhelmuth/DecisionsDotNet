using System.Text.Json.Serialization;

namespace DecisionsDotNet;

/// <summary>A question to ask about the request's state. Use <see cref="NoulQuestion"/>, <see cref="ChoiceQuestion"/>, <see cref="ScoreQuestion"/>, or <see cref="BoxQuestion"/>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NoulQuestion), "noul")]
[JsonDerivedType(typeof(ChoiceQuestion), "choice")]
[JsonDerivedType(typeof(ScoreQuestion), "score")]
[JsonDerivedType(typeof(BoxQuestion), "box")]
public abstract record DecisionsQuestion
{
    /// <summary>Sets the instructions shared by every question type.</summary>
    protected DecisionsQuestion(DecisionsValue instructions) => Instructions = instructions ?? throw new ArgumentNullException(nameof(instructions));

    /// <summary>What to decide, phrased as a question.</summary>
    [JsonPropertyName("instructions")]
    public DecisionsValue Instructions { get; }
}

/// <summary>A yes/no question, answered with a probability from zero to one.</summary>
public sealed record NoulQuestion : DecisionsQuestion
{
    /// <summary>Creates a yes/no question.</summary>
    /// <param name="instructions">What to decide, phrased as a question.</param>
    /// <param name="criteria">Optional descriptions of what counts as true and as false.</param>
    public NoulQuestion(DecisionsValue instructions, NoulCriteria? criteria = null) : base(instructions) => Criteria = criteria;

    /// <summary>Optional descriptions of what counts as true and as false.</summary>
    [JsonPropertyName("criteria")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NoulCriteria? Criteria { get; }
}

/// <summary>Locates one target in an image. Requires a model supporting Box questions, such as Jev 1.14.</summary>
public sealed record BoxQuestion : DecisionsQuestion
{
    /// <summary>Creates a bounding-box question from a description of the target.</summary>
    public BoxQuestion(DecisionsValue instructions) : base(instructions) { }
}

/// <summary>Optional guidance for a <see cref="NoulQuestion"/>.</summary>
/// <param name="True">What counts as a yes.</param>
/// <param name="False">What counts as a no.</param>
public sealed record NoulCriteria(
    [property: JsonPropertyName("true"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DecisionsValue? True = null,
    [property: JsonPropertyName("false"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DecisionsValue? False = null);

/// <summary>A choice of one option from a fixed set.</summary>
public sealed record ChoiceQuestion : DecisionsQuestion
{
    /// <summary>Creates a choice question.</summary>
    /// <param name="instructions">What to decide, phrased as a question.</param>
    /// <param name="criteria">From 1 to 255 options, keyed by option name. A value describes when the option applies, or is null when the name is enough.</param>
    public ChoiceQuestion(DecisionsValue instructions, IReadOnlyDictionary<string, DecisionsValue?> criteria) : base(instructions)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        if (criteria.Count is < 1 or > 255 || criteria.Keys.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Choice criteria must contain 1 to 255 named options.", nameof(criteria));
        Criteria = new Dictionary<string, DecisionsValue?>(criteria);
    }

    /// <summary>The options, keyed by name, each with an optional description.</summary>
    [JsonPropertyName("criteria")]
    public IReadOnlyDictionary<string, DecisionsValue?> Criteria { get; }
}

/// <summary>A rating on ordered levels.</summary>
public sealed record ScoreQuestion : DecisionsQuestion
{
    /// <summary>Creates a score question.</summary>
    /// <param name="instructions">What to rate, phrased as a question.</param>
    /// <param name="criteria">From 2 to 10 level descriptions, lowest first.</param>
    public ScoreQuestion(DecisionsValue instructions, IReadOnlyList<DecisionsValue> criteria) : base(instructions)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        if (criteria.Count is < 2 or > 10 || criteria.Any(value => value is null))
            throw new ArgumentException("Score criteria must contain 2 to 10 levels.", nameof(criteria));
        Criteria = criteria.ToArray();
    }

    /// <summary>The level descriptions, lowest first.</summary>
    [JsonPropertyName("criteria")]
    public IReadOnlyList<DecisionsValue> Criteria { get; }
}
