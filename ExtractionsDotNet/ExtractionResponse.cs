using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace ExtractionsDotNet;

/// <summary>The result of one extraction.</summary>
/// <param name="Model">The model that answered.</param>
/// <param name="Answers">One answer per requested field, keyed by field name.</param>
/// <param name="Usage">Token counts and cost for the request.</param>
public sealed record ExtractionResponse(
    string Model,
    IReadOnlyDictionary<string, ExtractionAnswer> Answers,
    ExtractionUsage Usage)
{
    /// <summary>OpenRouter's ID for the generation, when returned.</summary>
    public string? Id { get; init; }

    /// <summary>The provider that answered, when returned.</summary>
    public string? Provider { get; init; }

    /// <summary>The exact text that span offsets index into, when the provider returns it.</summary>
    public string? StateText { get; init; }

    /// <summary>True when the provider read only a prefix of the state.</summary>
    public bool? Truncated { get; init; }

    /// <summary>Reads a field's answer as its concrete type, such as <c>ExtractionAnswer&lt;double?&gt;</c> for a number field.</summary>
    public TAnswer GetAnswer<TAnswer>(string field) where TAnswer : ExtractionAnswer
    {
        if (!Answers.TryGetValue(field, out var answer))
            throw new KeyNotFoundException($"No answer was returned for field '{field}'.");
        return answer as TAnswer ?? throw new InvalidOperationException(
            $"Answer '{field}' is {TypeName(answer.GetType())}, not {TypeName(typeof(TAnswer))}.");
    }

    // Type.Name drops generic arguments, which would make a text answer and a number answer read the same.
    private static string TypeName(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return TypeName(underlying) + "?";
        if (!type.IsGenericType)
            return type.Name;
        var name = type.Name[..type.Name.IndexOf('`')];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>";
    }

    /// <summary>Reads a field's answer, or returns false when it is absent or of another type.</summary>
    public bool TryGetAnswer<TAnswer>(string field, [NotNullWhen(true)] out TAnswer? answer) where TAnswer : ExtractionAnswer
    {
        answer = Answers.TryGetValue(field, out var value) ? value as TAnswer : null;
        return answer is not null;
    }

    /// <summary>Reads a text field's answer.</summary>
    public ExtractionAnswer<string?> GetText(string field) => GetAnswer<ExtractionAnswer<string?>>(field);

    /// <summary>Reads a number field's answer.</summary>
    public ExtractionAnswer<double?> GetNumber(string field) => GetAnswer<ExtractionAnswer<double?>>(field);

    /// <summary>Reads a date field's answer.</summary>
    public ExtractionAnswer<DateOnly?> GetDate(string field) => GetAnswer<ExtractionAnswer<DateOnly?>>(field);

    /// <summary>Reads a time field's answer.</summary>
    public ExtractionAnswer<TimeOnly?> GetTime(string field) => GetAnswer<ExtractionAnswer<TimeOnly?>>(field);

    /// <summary>Reads a datetime field's answer.</summary>
    public ExtractionAnswer<ExtractionDateTime?> GetDateTime(string field) => GetAnswer<ExtractionAnswer<ExtractionDateTime?>>(field);

    /// <summary>Reads a duration field's answer, including the unit the model returned.</summary>
    public ExtractionDurationAnswer GetDuration(string field) => GetAnswer<ExtractionDurationAnswer>(field);

    /// <summary>Reads a list field's answer. <typeparamref name="T"/> is string, double, or DateOnly to match the field's item type.</summary>
    public ExtractionListAnswer<T> GetList<T>(string field) => GetAnswer<ExtractionListAnswer<T>>(field);

    /// <summary>Reads an estimate field's answer. <typeparamref name="T"/> is double or DateOnly to match the field's bounds.</summary>
    public ExtractionEstimateAnswer<T> GetEstimate<T>(string field) where T : struct => GetAnswer<ExtractionEstimateAnswer<T>>(field);

    /// <summary>Reads a forecast field's answer. <typeparamref name="T"/> is double or DateOnly to match the field's bounds.</summary>
    public ExtractionForecastAnswer<T> GetForecast<T>(string field) where T : struct => GetAnswer<ExtractionForecastAnswer<T>>(field);
}

/// <summary>Token counts and cost for an extraction. Optional values are null when the provider omits them.</summary>
public sealed record ExtractionUsage
{
    /// <summary>Input tokens billed.</summary>
    public required int InputTokens { get; init; }

    /// <summary>Output tokens billed.</summary>
    public required int OutputTokens { get; init; }

    /// <summary>The cost OpenRouter reports for the request.</summary>
    public decimal? Cost { get; init; }

    /// <summary>The API's <c>state_tokens</c> count.</summary>
    public int? StateTokens { get; init; }

    /// <summary>The API's <c>image_tokens</c> count.</summary>
    public int? ImageTokens { get; init; }

    /// <summary>The API's <c>schema_tokens</c> count.</summary>
    public int? SchemaTokens { get; init; }

    /// <summary>The number of upstream calls needed when the fields did not fit in one.</summary>
    public int? Passes { get; init; }
}

/// <summary>The properties every answer shares. Read the value through a concrete type such as <see cref="ExtractionAnswer{T}"/>.</summary>
public abstract record ExtractionAnswer
{
    /// <summary>The field type the answer echoes, such as <c>text</c> or <c>number</c>.</summary>
    public required string Type { get; init; }

    /// <summary>The probability that the input contains nothing for the field.</summary>
    [JsonPropertyName("p_null")]
    public double? NullProbability { get; init; }

    /// <summary>The model's confidence in the value.</summary>
    public double? Confidence { get; init; }

    /// <summary><c>image</c> or <c>document</c> when the answer came from an asset rather than the state text.</summary>
    public string? Source { get; init; }

    /// <summary>How sure the model is of the spelling, reported when the answer came from an asset.</summary>
    public double? SpellConfidence { get; init; }

    /// <summary>Where the value was found in the state text.</summary>
    public ExtractionSpan? Span { get; init; }
}

/// <summary>An answer with a typed value.</summary>
/// <typeparam name="T">The value type, nullable for scalar fields because the model may find nothing.</typeparam>
public record ExtractionAnswer<T> : ExtractionAnswer
{
    /// <summary>The answer, or null when the null probability crossed the field's threshold.</summary>
    public required T Value { get; init; }

    /// <summary>The most likely value, which can be present even when <see cref="Value"/> is null.</summary>
    public T? Guess { get; init; }
}

/// <summary>Zero-based, end-exclusive Unicode code point offsets into the state text.</summary>
/// <param name="Start">The offset of the first code point.</param>
/// <param name="End">The offset just past the last code point.</param>
public readonly record struct ExtractionSpan(int Start, int End);

/// <summary>A wall-clock date and time with an offset only when the answer includes one.</summary>
/// <param name="LocalDateTime">The date and time as written, without conversion.</param>
/// <param name="Offset">The UTC offset, or null when the answer has none.</param>
public readonly record struct ExtractionDateTime(DateTime LocalDateTime, TimeSpan? Offset);

/// <summary>The answer to a <see cref="DurationField"/>.</summary>
public sealed record ExtractionDurationAnswer : ExtractionAnswer<double?>
{
    /// <summary>The unit the value is expressed in, as returned by the model.</summary>
    public string? Unit { get; init; }
}

/// <summary>The answer to a <see cref="ListField"/>.</summary>
/// <typeparam name="T">string, double, or DateOnly, matching the field's item type.</typeparam>
public sealed record ExtractionListAnswer<T> : ExtractionAnswer<IReadOnlyList<T>?>
{
    /// <summary>One span per item. An entry is null when that item has no span.</summary>
    public IReadOnlyList<ExtractionSpan?>? Spans { get; init; }

    /// <summary>One confidence per item.</summary>
    public IReadOnlyList<double>? Confidences { get; init; }
}

/// <summary>The answer to an <see cref="EstimateField"/>: a value with its distribution.</summary>
/// <typeparam name="T">double or DateOnly, matching the field's bounds.</typeparam>
public sealed record ExtractionEstimateAnswer<T> : ExtractionAnswer<T?> where T : struct
{
    /// <summary>The mean of the distribution.</summary>
    public T? Mean { get; init; }

    /// <summary>The median of the distribution.</summary>
    public T? Median { get; init; }

    /// <summary>The API's <c>p10</c> value of the distribution.</summary>
    public T? P10 { get; init; }

    /// <summary>The API's <c>p90</c> value of the distribution.</summary>
    public T? P90 { get; init; }

    /// <summary>The probability mass below the field's minimum.</summary>
    public double? PBelow { get; init; }

    /// <summary>The probability mass above the field's maximum.</summary>
    public double? PAbove { get; init; }

    /// <summary>The distribution's quantiles, returned when the provider is asked for them.</summary>
    public IReadOnlyList<T>? Quantiles { get; init; }
}

/// <summary>The answer to a <see cref="ForecastField"/>: one value per step, with each step's distribution.</summary>
/// <typeparam name="T">double or DateOnly, matching the field's bounds.</typeparam>
public sealed record ExtractionForecastAnswer<T> : ExtractionAnswer<IReadOnlyList<T>?> where T : struct
{
    /// <summary>The mean for each step.</summary>
    public IReadOnlyList<T>? Mean { get; init; }

    /// <summary>The median for each step.</summary>
    public IReadOnlyList<T>? Median { get; init; }

    /// <summary>The API's <c>p10</c> value for each step.</summary>
    public IReadOnlyList<T>? P10 { get; init; }

    /// <summary>The API's <c>p90</c> value for each step.</summary>
    public IReadOnlyList<T>? P90 { get; init; }

    /// <summary>Per-step detail, when the provider returns it.</summary>
    public IReadOnlyList<ExtractionForecastStep<T>>? Steps { get; init; }
}

/// <summary>One step of a forecast.</summary>
/// <typeparam name="T">double or DateOnly, matching the field's bounds.</typeparam>
public sealed record ExtractionForecastStep<T> where T : struct
{
    /// <summary>The step's value, or null when the model returns none.</summary>
    public required T? Value { get; init; }

    /// <summary>The mean of the step's distribution.</summary>
    public T? Mean { get; init; }

    /// <summary>The median of the step's distribution.</summary>
    public T? Median { get; init; }

    /// <summary>The API's <c>p10</c> value of the step's distribution.</summary>
    public T? P10 { get; init; }

    /// <summary>The API's <c>p90</c> value of the step's distribution.</summary>
    public T? P90 { get; init; }

    /// <summary>The probability mass below the field's minimum.</summary>
    public double? PBelow { get; init; }

    /// <summary>The probability mass above the field's maximum.</summary>
    public double? PAbove { get; init; }

    /// <summary>The step's quantiles, returned when the provider is asked for them.</summary>
    public IReadOnlyList<T>? Quantiles { get; init; }
}

/// <summary>A model that serves the Extractions route.</summary>
/// <param name="Id">The model ID to pass as <see cref="ExtractionRequest.Model"/>.</param>
/// <param name="InputModalities">The inputs the model accepts, such as text, image, and file.</param>
public sealed record ExtractionModel(string Id, IReadOnlyList<string> InputModalities);
