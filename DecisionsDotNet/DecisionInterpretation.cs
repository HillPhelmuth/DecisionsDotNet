using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace DecisionsDotNet;

#pragma warning disable DECISIONS002 // The conditional layer uses its own experimental types.

/// <summary>Argument checks shared by the conditional verbs.</summary>
internal static class DecisionGuard
{
    public static void Probability(double value, string name)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1)
            throw new ArgumentOutOfRangeException(name, value, "A probability threshold must be a finite value from 0 to 1.");
    }

    public static void UnsureBand(double low, double high, string name = "unsureBand")
    {
        if (!double.IsFinite(low) || !double.IsFinite(high) || low < 0 || high > 1 || low > high)
            throw new ArgumentOutOfRangeException(name, (low, high), "The unsure band must satisfy 0 <= low <= high <= 1.");
    }
}

/// <summary>Enum members as Decisions options: names are the wire labels, <see cref="DescriptionAttribute"/> text is the criterion.</summary>
internal static class DecisionEnum<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum>
    where TEnum : struct, Enum
{
    private static readonly Lazy<Metadata> Cached = new(Load);

    /// <summary>The members, ordered by underlying value, with their labels and descriptions.</summary>
    public static IReadOnlyList<(TEnum Value, string Label, string? Description)> Members => Cached.Value.Members;

    public static string Label(TEnum value)
        => Cached.Value.Labels.TryGetValue(value, out var label) ? label
            : throw new ArgumentException($"{value} is not a defined member of {typeof(TEnum).Name}.", nameof(value));

    public static bool TryParse(string label, out TEnum value) => Cached.Value.ByLabel.TryGetValue(label, out value);

    private sealed record Metadata(
        IReadOnlyList<(TEnum Value, string Label, string? Description)> Members,
        IReadOnlyDictionary<TEnum, string> Labels,
        IReadOnlyDictionary<string, TEnum> ByLabel);

    private static Metadata Load()
    {
        var members = new List<(TEnum, string, string?)>();
        var labels = new Dictionary<TEnum, string>();
        var byLabel = new Dictionary<string, TEnum>(StringComparer.Ordinal);
        foreach (var field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var value = (TEnum)field.GetValue(null)!;
            if (!labels.TryAdd(value, field.Name))
                throw new InvalidOperationException(
                    $"{typeof(TEnum).Name}.{field.Name} has the same value as {typeof(TEnum).Name}.{labels[value]}. " +
                    "Each option needs a distinct value so the answer maps back to one member.");
            byLabel.Add(field.Name, value);
            members.Add((value, field.Name, field.GetCustomAttribute<DescriptionAttribute>()?.Description));
        }
        if (members.Count == 0)
            throw new InvalidOperationException($"{typeof(TEnum).Name} has no members to offer as options.");
        members.Sort((a, b) => Comparer<TEnum>.Default.Compare(a.Item1, b.Item1));
        return new(members, labels, byLabel);
    }
}

/// <summary>Turns validated provider answers into the conditional result types.</summary>
internal static class DecisionInterpretation
{
    public static NoulAnswer? NoulOrRefusal(DecisionsResponse response, string questionId)
    {
        var answer = Answer(response, questionId);
        if (answer is RefusalAnswer) return null;
        if (answer is not NoulAnswer noul)
            throw new JsonException($"Answer '{questionId}' is {answer.GetType().Name}, not {nameof(NoulAnswer)}.");
        if (!IsProbability(noul.Noul))
            throw new JsonException($"Answer '{questionId}' has probability {noul.Noul}, which is not a finite value from 0 to 1.");
        return noul;
    }

    public static NoulAnswer Noul(DecisionsResponse response, string questionId)
        => NoulOrRefusal(response, questionId) ?? throw new DecisionRefusedException(questionId, response);

    public static DecisionVerdict Verdict(DecisionsResponse response, string questionId, double low, double high)
    {
        var answer = NoulOrRefusal(response, questionId);
        return new(answer is null ? VerdictOutcome.Refused : DecisionVerdict.Classify(answer.Noul, low, high), answer, response);
    }

    public static DecisionChoice<TEnum> Choice<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum>(DecisionsResponse response, string questionId, IReadOnlyCollection<TEnum> offered)
        where TEnum : struct, Enum
    {
        var answer = Typed<ChoiceAnswer>(response, questionId);
        var probabilities = new Dictionary<TEnum, double>();
        foreach (var (label, probability) in answer.Probabilities)
        {
            if (!TryOffered(label, offered, out var member))
                throw new JsonException($"Answer '{questionId}' gives a probability for '{label}', which was not offered.");
            if (!IsProbability(probability))
                throw new JsonException($"Answer '{questionId}' gives '{label}' probability {probability}, which is not a finite value from 0 to 1.");
            probabilities[member] = probability;
        }
        if (!TryOffered(answer.Choice, offered, out var chosen))
            throw new JsonException($"Answer '{questionId}' chose '{answer.Choice}', which was not offered.");
        if (!probabilities.TryGetValue(chosen, out var selected))
            throw new JsonException($"Answer '{questionId}' chose '{answer.Choice}' but gave no probability for it.");
        return new(chosen, selected, probabilities, answer, response);
    }

    public static DecisionScore<TEnum> Score<TEnum>(DecisionsResponse response, string questionId, IReadOnlyList<TEnum> levels)
        where TEnum : struct, Enum
    {
        var answer = Typed<ScoreAnswer>(response, questionId);
        return new(levels[NearestLevel(answer, questionId, levels.Count)], levels, answer, response);
    }

    public static DecisionScore Score(DecisionsResponse response, string questionId, int levelCount)
    {
        var answer = Typed<ScoreAnswer>(response, questionId);
        return new(NearestLevel(answer, questionId, levelCount), levelCount, answer, response);
    }

    private static int NearestLevel(ScoreAnswer answer, string questionId, int levelCount)
    {
        const double tolerance = 1e-6;
        if (!double.IsFinite(answer.Score) || answer.Score < -tolerance || answer.Score > levelCount - 1 + tolerance)
            throw new JsonException($"Answer '{questionId}' has score {answer.Score}, outside the {levelCount} levels offered.");
        foreach (var (level, probability) in answer.Probabilities)
        {
            if (!int.TryParse(level, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index >= levelCount ||
                !string.Equals(level, index.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                throw new JsonException($"Answer '{questionId}' gives a probability for level '{level}', which was not offered.");
            if (!IsProbability(probability))
                throw new JsonException($"Answer '{questionId}' gives level '{level}' probability {probability}, which is not a finite value from 0 to 1.");
        }
        return Math.Clamp((int)Math.Round(answer.Score, MidpointRounding.AwayFromZero), 0, levelCount - 1);
    }

    private static TAnswer Typed<TAnswer>(DecisionsResponse response, string questionId) where TAnswer : DecisionsAnswer
    {
        var answer = Answer(response, questionId);
        if (answer is RefusalAnswer) throw new DecisionRefusedException(questionId, response);
        return answer as TAnswer
            ?? throw new JsonException($"Answer '{questionId}' is {answer.GetType().Name}, not {typeof(TAnswer).Name}.");
    }

    private static DecisionsAnswer Answer(DecisionsResponse response, string questionId)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.Answers is not null && response.Answers.TryGetValue(questionId, out var answer) && answer is not null
            ? answer
            : throw new JsonException($"No answer was returned for question '{questionId}'.");
    }

    private static bool TryOffered<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum>(
        string label, IReadOnlyCollection<TEnum> offered, out TEnum member)
        where TEnum : struct, Enum
        => DecisionEnum<TEnum>.TryParse(label, out member) && offered.Contains(member);

    private static bool IsProbability(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
}
