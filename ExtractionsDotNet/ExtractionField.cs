using System.Text.Json.Serialization;

namespace ExtractionsDotNet;

/// <summary>The specification for one field to extract. Use a concrete type such as <see cref="TextField"/> or <see cref="NumberField"/>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextField), "text")]
[JsonDerivedType(typeof(NumberField), "number")]
[JsonDerivedType(typeof(DateField), "date")]
[JsonDerivedType(typeof(TimeField), "time")]
[JsonDerivedType(typeof(DateTimeField), "datetime")]
[JsonDerivedType(typeof(DurationField), "duration")]
[JsonDerivedType(typeof(ListField), "list")]
[JsonDerivedType(typeof(EstimateField), "estimate")]
[JsonDerivedType(typeof(ForecastField), "forecast")]
public abstract record ExtractionField
{
    /// <summary>What the field should contain. It can also reference an asset by its key.</summary>
    public string? Instructions { get; init; }

    /// <summary>The null probability above which the answer's value becomes null. The API default is 0.5; some models reject it.</summary>
    public double? NullThreshold { get; init; }
}

/// <summary>A text field.</summary>
public sealed record TextField : ExtractionField
{
    /// <summary>The API's optional <c>unit</c> key for this field.</summary>
    public string? Unit { get; init; }
}

/// <summary>A numeric field.</summary>
public sealed record NumberField : ExtractionField
{
    /// <summary>The API's optional <c>unit</c> key, such as a currency code.</summary>
    public string? Unit { get; init; }

    /// <summary>The API's optional <c>integer</c> key, for values expected to be whole numbers.</summary>
    public bool? Integer { get; init; }

    /// <summary>Decimal places to keep in the value, from 0 to 6.</summary>
    public int? Decimals { get; init; }
}

/// <summary>A calendar date field.</summary>
public sealed record DateField : ExtractionField
{
    /// <summary>The API's optional <c>unit</c> key for this field.</summary>
    public string? Unit { get; init; }
}

/// <summary>A time of day field.</summary>
public sealed record TimeField : ExtractionField
{
    /// <summary>The API's optional <c>unit</c> key for this field.</summary>
    public string? Unit { get; init; }
}

/// <summary>A date and time field. Answers keep the offset only when the model returns one.</summary>
public sealed record DateTimeField : ExtractionField
{
    /// <summary>The API's optional <c>unit</c> key for this field.</summary>
    public string? Unit { get; init; }
}

/// <summary>The unit a <see cref="DurationField"/> is expressed in.</summary>
public enum DurationUnit
{
    /// <summary>Seconds.</summary>
    Seconds,
    /// <summary>Minutes.</summary>
    Minutes,
    /// <summary>Hours.</summary>
    Hours,
    /// <summary>Days.</summary>
    Days,
    /// <summary>Weeks.</summary>
    Weeks,
    /// <summary>Months.</summary>
    Months,
    /// <summary>Years.</summary>
    Years
}

/// <summary>A length of time, such as a payment period.</summary>
public sealed record DurationField : ExtractionField
{
    /// <summary>The unit to express the duration in.</summary>
    public DurationUnit? Unit { get; init; }

    /// <summary>The API's optional <c>integer</c> key, for values expected to be whole numbers.</summary>
    public bool? Integer { get; init; }

    /// <summary>Decimal places to keep in the value, from 0 to 6.</summary>
    public int? Decimals { get; init; }
}

/// <summary>The item type of a <see cref="ListField"/>.</summary>
public enum ListItemType
{
    /// <summary>Text items, read as <c>ExtractionListAnswer&lt;string&gt;</c>.</summary>
    Text,
    /// <summary>Numeric items, read as <c>ExtractionListAnswer&lt;double&gt;</c>.</summary>
    Number,
    /// <summary>Date items, read as <c>ExtractionListAnswer&lt;DateOnly&gt;</c>.</summary>
    Date
}

/// <summary>A list of text, number, or date items.</summary>
/// <param name="Items">The type of each item.</param>
public sealed record ListField(ListItemType Items) : ExtractionField
{
    /// <summary>The API's optional <c>unit</c> key for the items.</summary>
    public string? Unit { get; init; }

    /// <summary>The API's optional <c>integer</c> key, for numeric items expected to be whole numbers.</summary>
    public bool? Integer { get; init; }

    /// <summary>The API's optional <c>decimals</c> key for numeric items.</summary>
    public int? Decimals { get; init; }
}

/// <summary>How the range between a <see cref="RangeField"/>'s bounds is divided.</summary>
public enum ExtractionScale
{
    /// <summary>A linear scale.</summary>
    Linear,
    /// <summary>A logarithmic scale.</summary>
    Log
}

/// <summary>A calendar-sized step for date-valued estimates and forecasts.</summary>
public enum CalendarStep
{
    /// <summary>Steps of one month.</summary>
    Month,
    /// <summary>Steps of one year.</summary>
    Year
}

/// <summary>The time between consecutive steps of a <see cref="ForecastField"/>.</summary>
public enum ForecastInterval
{
    /// <summary>The API's <c>step</c> interval.</summary>
    Step,
    /// <summary>One minute.</summary>
    Minute,
    /// <summary>One hour.</summary>
    Hour,
    /// <summary>One day.</summary>
    Day,
    /// <summary>One week.</summary>
    Week,
    /// <summary>One month.</summary>
    Month,
    /// <summary>One quarter.</summary>
    Quarter,
    /// <summary>One year.</summary>
    Year
}

/// <summary>The shared shape of <see cref="EstimateField"/> and <see cref="ForecastField"/>: a value distributed between two bounds.</summary>
public abstract record RangeField : ExtractionField
{
    private protected RangeField(object min, object max)
    {
        Min = min;
        Max = max;
    }

    // The concrete constructors constrain both bounds to the same supported type.
    /// <summary>The lower bound: a <see cref="double"/> or a <see cref="DateOnly"/>, matching the constructor used.</summary>
    public object Min { get; }

    /// <summary>The upper bound: a <see cref="double"/> or a <see cref="DateOnly"/>, matching the constructor used.</summary>
    public object Max { get; }

    /// <summary>The API's optional <c>unit</c> key for the value.</summary>
    public string? Unit { get; init; }

    /// <summary>Whether the range is linear or logarithmic.</summary>
    public ExtractionScale? Scale { get; init; }

    /// <summary>A positive numeric step, in days for date-valued fields. Set this or <see cref="CalendarStep"/>, not both.</summary>
    [JsonIgnore]
    public double? Step { get; init; }

    /// <summary>A month or year step for date-valued fields. Set this or <see cref="Step"/>, not both.</summary>
    [JsonIgnore]
    public CalendarStep? CalendarStep { get; init; }

    [JsonInclude, JsonPropertyName("step")]
    internal object? WireStep => CalendarStep is { } calendarStep ? calendarStep : Step;
}

/// <summary>An estimate of one number or date as a distribution between two bounds.</summary>
public sealed record EstimateField : RangeField
{
    /// <summary>Creates a numeric estimate, read as <c>ExtractionEstimateAnswer&lt;double&gt;</c>.</summary>
    public EstimateField(double min, double max) : base(min, max) { }

    /// <summary>Creates a date estimate, read as <c>ExtractionEstimateAnswer&lt;DateOnly&gt;</c>.</summary>
    public EstimateField(DateOnly min, DateOnly max) : base(min, max) { }
}

/// <summary>A forecast of a number or date over several future steps.</summary>
public sealed record ForecastField : RangeField
{
    /// <summary>Creates a numeric forecast, read as <c>ExtractionForecastAnswer&lt;double&gt;</c>.</summary>
    /// <param name="min">The lower bound of each step's value.</param>
    /// <param name="max">The upper bound of each step's value.</param>
    /// <param name="horizon">The number of steps to forecast, from 1 to 64.</param>
    /// <param name="interval">The time between steps.</param>
    public ForecastField(double min, double max, int horizon, ForecastInterval interval) : base(min, max)
    {
        Horizon = horizon;
        Interval = interval;
    }

    /// <summary>Creates a date forecast, read as <c>ExtractionForecastAnswer&lt;DateOnly&gt;</c>.</summary>
    /// <param name="min">The lower bound of each step's value.</param>
    /// <param name="max">The upper bound of each step's value.</param>
    /// <param name="horizon">The number of steps to forecast, from 1 to 64.</param>
    /// <param name="interval">The time between steps.</param>
    public ForecastField(DateOnly min, DateOnly max, int horizon, ForecastInterval interval) : base(min, max)
    {
        Horizon = horizon;
        Interval = interval;
    }

    /// <summary>The number of steps to forecast, from 1 to 64.</summary>
    public int Horizon { get; }

    /// <summary>The time between steps.</summary>
    public ForecastInterval Interval { get; }
}
