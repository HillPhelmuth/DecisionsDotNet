using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace ExtractionsDotNet;

public sealed record ExtractionResponse(
    string Model,
    IReadOnlyDictionary<string, ExtractionAnswer> Answers,
    ExtractionUsage Usage)
{
    public string? Id { get; init; }
    public string? Provider { get; init; }
    public string? StateText { get; init; }
    public bool? Truncated { get; init; }

    /// <summary>Reads a field's answer as its concrete type, such as <c>ExtractionAnswer&lt;double?&gt;</c> for a number field.</summary>
    public TAnswer GetAnswer<TAnswer>(string field) where TAnswer : ExtractionAnswer
    {
        if (!Answers.TryGetValue(field, out var answer))
            throw new KeyNotFoundException($"No answer was returned for field '{field}'.");
        return answer as TAnswer ?? throw new InvalidOperationException(
            $"Answer '{field}' is {answer.GetType().Name}, not {typeof(TAnswer).Name}.");
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

public sealed record ExtractionUsage
{
    public required int InputTokens { get; init; }
    public required int OutputTokens { get; init; }
    public decimal? Cost { get; init; }
    public int? StateTokens { get; init; }
    public int? ImageTokens { get; init; }
    public int? SchemaTokens { get; init; }
    public int? Passes { get; init; }
}

public abstract record ExtractionAnswer
{
    public required string Type { get; init; }
    [JsonPropertyName("p_null")]
    public double? NullProbability { get; init; }
    public double? Confidence { get; init; }
    public string? Source { get; init; }
    public double? SpellConfidence { get; init; }
    public ExtractionSpan? Span { get; init; }
}

public record ExtractionAnswer<T> : ExtractionAnswer
{
    public required T Value { get; init; }
    public T? Guess { get; init; }
}

/// <summary>Zero-based, end-exclusive Unicode code point offsets into the state text.</summary>
public readonly record struct ExtractionSpan(int Start, int End);

/// <summary>A wall-clock date and time with an offset only when the answer includes one.</summary>
public readonly record struct ExtractionDateTime(DateTime LocalDateTime, TimeSpan? Offset);

public sealed record ExtractionDurationAnswer : ExtractionAnswer<double?>
{
    public string? Unit { get; init; }
}

public sealed record ExtractionListAnswer<T> : ExtractionAnswer<IReadOnlyList<T>?>
{
    public IReadOnlyList<ExtractionSpan?>? Spans { get; init; }
    public IReadOnlyList<double>? Confidences { get; init; }
}

public sealed record ExtractionEstimateAnswer<T> : ExtractionAnswer<T?> where T : struct
{
    public T? Mean { get; init; }
    public T? Median { get; init; }
    public T? P10 { get; init; }
    public T? P90 { get; init; }
    public double? PBelow { get; init; }
    public double? PAbove { get; init; }
    public IReadOnlyList<T>? Quantiles { get; init; }
}

public sealed record ExtractionForecastAnswer<T> : ExtractionAnswer<IReadOnlyList<T>?> where T : struct
{
    public IReadOnlyList<T>? Mean { get; init; }
    public IReadOnlyList<T>? Median { get; init; }
    public IReadOnlyList<T>? P10 { get; init; }
    public IReadOnlyList<T>? P90 { get; init; }
    public IReadOnlyList<ExtractionForecastStep<T>>? Steps { get; init; }
}

public sealed record ExtractionForecastStep<T> where T : struct
{
    public required T? Value { get; init; }
    public T? Mean { get; init; }
    public T? Median { get; init; }
    public T? P10 { get; init; }
    public T? P90 { get; init; }
    public double? PBelow { get; init; }
    public double? PAbove { get; init; }
    public IReadOnlyList<T>? Quantiles { get; init; }
}

public sealed record ExtractionModel(string Id, IReadOnlyList<string> InputModalities);
