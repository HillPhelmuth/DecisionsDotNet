using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace DecisionsDotNet;

#pragma warning disable DECISIONS002 // The conditional layer uses its own experimental types.

/// <summary>Base for the attributes that turn a property of a decision set into a question. See <see cref="DecisionModel.DecideAsync{T}"/>.</summary>
[Experimental(DecisionsDiagnostics.Conditions, UrlFormat = DecisionsDiagnostics.UrlFormat)]
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public abstract class DecisionQuestionAttribute : Attribute
{
    /// <summary>Sets the question's instructions.</summary>
    protected DecisionQuestionAttribute(string instructions)
        => Instructions = !string.IsNullOrWhiteSpace(instructions) ? instructions
            : throw new ArgumentException("Instructions are required.", nameof(instructions));

    /// <summary>What to decide, phrased as a question.</summary>
    public string Instructions { get; }

    /// <summary>The question ID sent on the wire. Defaults to the property name.</summary>
    public string? Id { get; set; }
}

/// <summary>
/// Asks a yes/no question. The property can be a <see cref="DecisionVerdict"/> (set <see cref="UnsureMin"/> and <see cref="UnsureMax"/>),
/// a <see cref="bool"/> (set <see cref="MinProbability"/>), a <see cref="double"/> probability, or a <see cref="NoulAnswer"/>.
/// </summary>
[Experimental(DecisionsDiagnostics.Conditions, UrlFormat = DecisionsDiagnostics.UrlFormat)]
public sealed class NoulAttribute(string instructions) : DecisionQuestionAttribute(instructions)
{
    /// <summary>The lower edge of the unsure band, for <see cref="DecisionVerdict"/> properties.</summary>
    public double UnsureMin { get; set; } = double.NaN;

    /// <summary>The upper edge of the unsure band, for <see cref="DecisionVerdict"/> properties.</summary>
    public double UnsureMax { get; set; } = double.NaN;

    /// <summary>The threshold for <see cref="bool"/> properties: true when the probability is at least this.</summary>
    public double MinProbability { get; set; } = double.NaN;

    /// <summary>Optional description of what counts as yes.</summary>
    public string? WhenTrue { get; set; }

    /// <summary>Optional description of what counts as no.</summary>
    public string? WhenFalse { get; set; }
}

/// <summary>
/// Asks a choice among the members of an enum, named by the property type: <see cref="DecisionChoice{TEnum}"/> or the enum itself.
/// Member names are the labels and <see cref="System.ComponentModel.DescriptionAttribute"/> text is the criteria.
/// </summary>
[Experimental(DecisionsDiagnostics.Conditions, UrlFormat = DecisionsDiagnostics.UrlFormat)]
public sealed class ChoiceAttribute(string instructions) : DecisionQuestionAttribute(instructions);

/// <summary>
/// Asks for a rating. With no levels, the property is <see cref="DecisionScore{TEnum}"/> or an enum whose members are the levels.
/// With levels, the property is <see cref="DecisionScore"/>, a <see cref="double"/> score, or an <see cref="int"/> nearest level.
/// </summary>
[Experimental(DecisionsDiagnostics.Conditions, UrlFormat = DecisionsDiagnostics.UrlFormat)]
public sealed class ScoreAttribute(string instructions, params string[] levels) : DecisionQuestionAttribute(instructions)
{
    /// <summary>Level descriptions, lowest first, when the property is not an enum.</summary>
    public IReadOnlyList<string> Levels { get; } = levels ?? [];
}

/// <summary>A decision set's values together with the response that produced them.</summary>
/// <typeparam name="T">The decision set type.</typeparam>
/// <param name="Value">The populated decision set.</param>
/// <param name="Response">The full response, with model, usage, and every raw answer.</param>
[Experimental(DecisionsDiagnostics.Conditions, UrlFormat = DecisionsDiagnostics.UrlFormat)]
public sealed record DecisionSetResult<T>(T Value, DecisionsResponse Response);

/// <summary>Reads a decision set type once and caches its questions and how to read each answer back.</summary>
internal sealed class DecisionSetPlan
{
    private static readonly ConcurrentDictionary<Type, DecisionSetPlan> Cache = new();

    private readonly Func<object> _create;
    private readonly (PropertyInfo Property, string Id, Func<DecisionsResponse, object?> Read)[] _properties;

    private DecisionSetPlan(Func<object> create, IReadOnlyDictionary<string, DecisionsQuestion> questions,
        (PropertyInfo, string, Func<DecisionsResponse, object?>)[] properties)
    {
        _create = create;
        Questions = questions;
        _properties = properties;
    }

    public IReadOnlyDictionary<string, DecisionsQuestion> Questions { get; }

    [RequiresUnreferencedCode(DecisionSets.TrimMessage)]
    [RequiresDynamicCode(DecisionSets.AotMessage)]
    public static DecisionSetPlan For([DynamicallyAccessedMembers(DecisionSets.Members)] Type type)
        => Cache.TryGetValue(type, out var plan) ? plan : Cache.GetOrAdd(type, Build(type));

    public object Read(DecisionsResponse response)
    {
        var value = _create();
        foreach (var (property, _, read) in _properties)
            property.SetValue(value, read(response));
        return value;
    }

    [RequiresUnreferencedCode(DecisionSets.TrimMessage)]
    [RequiresDynamicCode(DecisionSets.AotMessage)]
    private static DecisionSetPlan Build([DynamicallyAccessedMembers(DecisionSets.Members)] Type type)
    {
        if (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is not { } constructor)
            throw new InvalidOperationException($"{type.Name} needs a public parameterless constructor to be a decision set.");

        var questions = new Dictionary<string, DecisionsQuestion>(StringComparer.Ordinal);
        var properties = new List<(PropertyInfo, string, Func<DecisionsResponse, object?>)>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var attribute = property.GetCustomAttribute<DecisionQuestionAttribute>(inherit: true);
            if (attribute is null) continue;
            if (property.SetMethod is not { IsPublic: true })
                throw new InvalidOperationException($"{type.Name}.{property.Name} needs a public set or init accessor.");

            var id = attribute.Id ?? property.Name;
            var (question, read) = attribute switch
            {
                NoulAttribute noul => Noul(type, property, noul, id),
                ChoiceAttribute => Enum(type, property, id, nameof(Choice)),
                ScoreAttribute { Levels.Count: > 0 } score => TextScore(type, property, score, id),
                ScoreAttribute => Enum(type, property, id, nameof(EnumScore)),
                _ => throw new InvalidOperationException($"{attribute.GetType().Name} is not a supported question attribute."),
            };
            if (!questions.TryAdd(id, question))
                throw new InvalidOperationException($"{type.Name} has two questions with ID '{id}'.");
            properties.Add((property, id, read));
        }
        if (questions.Count == 0)
            throw new InvalidOperationException($"{type.Name} has no [Noul], [Choice], or [Score] properties.");

        return new(() => constructor.Invoke(null), questions, properties.ToArray());
    }

    private static (DecisionsQuestion, Func<DecisionsResponse, object?>) Noul(Type owner, PropertyInfo property, NoulAttribute noul, string id)
    {
        var criteria = noul.WhenTrue is null && noul.WhenFalse is null ? null
            : new NoulCriteria(noul.WhenTrue is null ? null : DecisionsValue.Text(noul.WhenTrue),
                noul.WhenFalse is null ? null : DecisionsValue.Text(noul.WhenFalse));
        var question = new NoulQuestion(noul.Instructions, criteria);
        var type = property.PropertyType;

        if (type == typeof(DecisionVerdict))
        {
            if (double.IsNaN(noul.UnsureMin) || double.IsNaN(noul.UnsureMax))
                throw new InvalidOperationException($"{owner.Name}.{property.Name} is a DecisionVerdict, so [Noul] needs UnsureMin and UnsureMax.");
            DecisionGuard.UnsureBand(noul.UnsureMin, noul.UnsureMax, $"{owner.Name}.{property.Name}");
            return (question, response => DecisionInterpretation.Verdict(response, id, noul.UnsureMin, noul.UnsureMax));
        }
        if (type == typeof(bool))
        {
            if (double.IsNaN(noul.MinProbability))
                throw new InvalidOperationException($"{owner.Name}.{property.Name} is a bool, so [Noul] needs MinProbability.");
            DecisionGuard.Probability(noul.MinProbability, $"{owner.Name}.{property.Name}");
            return (question, response => DecisionInterpretation.Noul(response, id).Noul >= noul.MinProbability);
        }
        if (type == typeof(double))
            return (question, response => DecisionInterpretation.Noul(response, id).Noul);
        if (type == typeof(NoulAnswer))
            return (question, response => DecisionInterpretation.Noul(response, id));
        throw Unsupported(owner, property, "[Noul]", "DecisionVerdict, bool, double, or NoulAnswer");
    }

    private static (DecisionsQuestion, Func<DecisionsResponse, object?>) TextScore(Type owner, PropertyInfo property, ScoreAttribute score, string id)
    {
        var question = new ScoreQuestion(score.Instructions, score.Levels.Select(DecisionsValue.Text).ToArray());
        var count = question.Criteria.Count;
        var type = property.PropertyType;
        if (type == typeof(DecisionScore)) return (question, response => DecisionInterpretation.Score(response, id, count));
        if (type == typeof(double)) return (question, response => DecisionInterpretation.Score(response, id, count).Score);
        if (type == typeof(int)) return (question, response => DecisionInterpretation.Score(response, id, count).Level);
        throw Unsupported(owner, property, "[Score] with levels", "DecisionScore, double, or int");
    }

    [RequiresUnreferencedCode(DecisionSets.TrimMessage)]
    [RequiresDynamicCode(DecisionSets.AotMessage)]
    private static (DecisionsQuestion, Func<DecisionsResponse, object?>) Enum(Type owner, PropertyInfo property, string id, string factory)
    {
        var type = property.PropertyType;
        var wrapper = factory == nameof(Choice) ? typeof(DecisionChoice<>) : typeof(DecisionScore<>);
        var (enumType, wrapped) = type.IsEnum ? (type, false)
            : type.IsGenericType && type.GetGenericTypeDefinition() == wrapper ? (type.GetGenericArguments()[0], true)
            : throw Unsupported(owner, property, factory == nameof(Choice) ? "[Choice]" : "[Score] without levels",
                $"an enum or {wrapper.Name[..^2]}<TEnum>");
        var method = typeof(DecisionSetPlan).GetMethod(factory, BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(enumType);
        var instructions = ((DecisionQuestionAttribute)property.GetCustomAttribute(typeof(DecisionQuestionAttribute), true)!).Instructions;
        return ((DecisionsQuestion, Func<DecisionsResponse, object?>))method.Invoke(null, [instructions, id, wrapped])!;
    }

    private static (DecisionsQuestion, Func<DecisionsResponse, object?>) Choice<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum>(string instructions, string id, bool wrapped)
        where TEnum : struct, Enum
    {
        var members = DecisionEnum<TEnum>.Members;
        var question = new ChoiceQuestion(instructions, members.ToDictionary(member => member.Label,
            member => member.Description is null ? null : DecisionsValue.Text(member.Description), StringComparer.Ordinal));
        var offered = members.Select(member => member.Value).ToHashSet();
        return (question, response =>
        {
            var choice = DecisionInterpretation.Choice(response, id, offered);
            return wrapped ? choice : choice.Value;
        });
    }

    private static (DecisionsQuestion, Func<DecisionsResponse, object?>) EnumScore<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum>(string instructions, string id, bool wrapped)
        where TEnum : struct, Enum
    {
        var members = DecisionEnum<TEnum>.Members;
        var question = new ScoreQuestion(instructions, members.Select(member => DecisionsValue.Text(member.Description ?? member.Label)).ToArray());
        var levels = members.Select(member => member.Value).ToArray();
        return (question, response =>
        {
            var score = DecisionInterpretation.Score(response, id, levels);
            return wrapped ? score : score.Value;
        });
    }

    private static InvalidOperationException Unsupported(Type owner, PropertyInfo property, string attribute, string expected)
        => new($"{owner.Name}.{property.Name} is {property.PropertyType.Name}, but {attribute} properties must be {expected}.");
}

internal static class DecisionSets
{
    public const DynamicallyAccessedMemberTypes Members =
        DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor;

    public const string TrimMessage = "Decision sets read their properties and enum property types by reflection.";
    public const string AotMessage = "Decision sets build generic readers for enum property types at run time.";
}
