using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace DecisionsDotNet;

#pragma warning disable DECISIONS002 // The conditional layer uses its own experimental types.

/// <summary>How the collection verbs on <see cref="DecisionCollectionExtensions"/> pack items into requests.</summary>
/// <typeparam name="T">The item type.</typeparam>
[Experimental(DecisionsDiagnostics.Conditions, UrlFormat = DecisionsDiagnostics.UrlFormat)]
public sealed class DecisionCollectionOptions<T>
{
    /// <summary>Items per request, from 1 upward. Providers cap the questions per request, so keep it modest. Defaults to 10.</summary>
    public int ItemsPerRequest { get; init; } = 10;

    /// <summary>
    /// A cheap check run before batching. Items it rejects are never sent: they count as not matching for
    /// <c>WhereAsync</c> and <c>PartitionAsync</c>, and are left out of <c>GroupByChoiceAsync</c> and <c>OrderByScoreAsync</c>.
    /// </summary>
    public Func<T, bool>? Prefilter { get; init; }
}

/// <summary>An item and the decision made about it.</summary>
/// <typeparam name="T">The item type.</typeparam>
/// <typeparam name="TResult">The decision type.</typeparam>
/// <param name="Item">The item.</param>
/// <param name="Result">The decision. Its <c>Response</c> is the batched response that answered this and other items.</param>
[Experimental(DecisionsDiagnostics.Conditions, UrlFormat = DecisionsDiagnostics.UrlFormat)]
public sealed record DecisionItem<T, TResult>(T Item, TResult Result);

/// <summary>Items that received the same choice.</summary>
/// <typeparam name="TEnum">The choice enum.</typeparam>
/// <typeparam name="T">The item type.</typeparam>
[Experimental(DecisionsDiagnostics.Conditions, UrlFormat = DecisionsDiagnostics.UrlFormat)]
public sealed class DecisionGroup<TEnum, T> where TEnum : struct, Enum
{
    internal DecisionGroup(TEnum key, IReadOnlyList<DecisionItem<T, DecisionChoice<TEnum>>> results)
    {
        Key = key;
        Results = results;
        Items = results.Select(result => result.Item).ToArray();
    }

    /// <summary>The choice every item in the group received.</summary>
    public TEnum Key { get; }

    /// <summary>The items, in source order.</summary>
    public IReadOnlyList<T> Items { get; }

    /// <summary>The items with the evidence for each choice, in source order.</summary>
    public IReadOnlyList<DecisionItem<T, DecisionChoice<TEnum>>> Results { get; }
}

/// <summary>
/// LINQ-style verbs that ask one question about every item in a collection, packing items into a few batched requests
/// (<see cref="DecisionsBatch.EvaluateBatchesAsync{TItem}"/>) instead of one request per item.
/// </summary>
/// <remarks>
/// Each request holds a JSON state of <c>{ items: [{ id, input }] }</c> and one question per item. Requests run one after another;
/// a failed request or a refusal stops the operation with its exception.
/// </remarks>
[Experimental(DecisionsDiagnostics.Conditions, UrlFormat = DecisionsDiagnostics.UrlFormat)]
public static class DecisionCollectionExtensions
{
    private const string QuestionKey = "q";

    /// <summary>Streams the items whose probability of yes is at least <paramref name="minProbability"/>, a batch at a time.</summary>
    public static IAsyncEnumerable<T> WhereAsync<T>(this DecisionModel model, IEnumerable<T> source,
        Func<T, DecisionsValue> selector, string instructions, double minProbability, CancellationToken cancellationToken = default)
        => model.WhereAsync(ToAsync(source), selector, instructions, minProbability, new DecisionCollectionOptions<T>(), cancellationToken);

    /// <summary>Streams the items whose probability of yes is at least <paramref name="minProbability"/>, with batching options.</summary>
    public static IAsyncEnumerable<T> WhereAsync<T>(this DecisionModel model, IEnumerable<T> source,
        Func<T, DecisionsValue> selector, string instructions, double minProbability, DecisionCollectionOptions<T> options,
        CancellationToken cancellationToken = default)
        => model.WhereAsync(ToAsync(source), selector, instructions, minProbability, options, cancellationToken);

    /// <summary>Streams the items of an async source whose probability of yes is at least <paramref name="minProbability"/>.</summary>
    public static IAsyncEnumerable<T> WhereAsync<T>(this DecisionModel model, IAsyncEnumerable<T> source,
        Func<T, DecisionsValue> selector, string instructions, double minProbability, CancellationToken cancellationToken = default)
        => model.WhereAsync(source, selector, instructions, minProbability, new DecisionCollectionOptions<T>(), cancellationToken);

    /// <summary>Streams the items of an async source whose probability of yes is at least <paramref name="minProbability"/>, with batching options.</summary>
    public static IAsyncEnumerable<T> WhereAsync<T>(this DecisionModel model, IAsyncEnumerable<T> source,
        Func<T, DecisionsValue> selector, string instructions, double minProbability, DecisionCollectionOptions<T> options,
        CancellationToken cancellationToken = default)
    {
        DecisionGuard.Probability(minProbability, nameof(minProbability));
        Validate(model, source, selector, instructions, options);
        return Where(model, source, selector, instructions, minProbability, options, cancellationToken);

        static async IAsyncEnumerable<T> Where(DecisionModel model, IAsyncEnumerable<T> source, Func<T, DecisionsValue> selector,
            string instructions, double minProbability, DecisionCollectionOptions<T> options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var (_, item, response, id) in EvaluateAsync(model, source, selector, NoulFor(instructions), options, cancellationToken)
                               .ConfigureAwait(false))
            {
                if (response is not null && DecisionInterpretation.Noul(response, id).Noul >= minProbability)
                    yield return item;
            }
        }
    }

    /// <summary>Splits items by whether their probability of yes is at least <paramref name="minProbability"/>, keeping source order in each list.</summary>
    public static Task<(IReadOnlyList<T> Matching, IReadOnlyList<T> NotMatching)> PartitionAsync<T>(this DecisionModel model,
        IEnumerable<T> source, Func<T, DecisionsValue> selector, string instructions, double minProbability,
        CancellationToken cancellationToken = default)
        => model.PartitionAsync(ToAsync(source), selector, instructions, minProbability, new DecisionCollectionOptions<T>(), cancellationToken);

    /// <summary>Splits items by whether their probability of yes is at least <paramref name="minProbability"/>, with batching options.</summary>
    public static Task<(IReadOnlyList<T> Matching, IReadOnlyList<T> NotMatching)> PartitionAsync<T>(this DecisionModel model,
        IEnumerable<T> source, Func<T, DecisionsValue> selector, string instructions, double minProbability,
        DecisionCollectionOptions<T> options, CancellationToken cancellationToken = default)
        => model.PartitionAsync(ToAsync(source), selector, instructions, minProbability, options, cancellationToken);

    /// <summary>Splits the items of an async source by whether their probability of yes is at least <paramref name="minProbability"/>.</summary>
    public static Task<(IReadOnlyList<T> Matching, IReadOnlyList<T> NotMatching)> PartitionAsync<T>(this DecisionModel model,
        IAsyncEnumerable<T> source, Func<T, DecisionsValue> selector, string instructions, double minProbability,
        CancellationToken cancellationToken = default)
        => model.PartitionAsync(source, selector, instructions, minProbability, new DecisionCollectionOptions<T>(), cancellationToken);

    /// <summary>Splits the items of an async source by whether their probability of yes is at least <paramref name="minProbability"/>, with batching options.</summary>
    public static async Task<(IReadOnlyList<T> Matching, IReadOnlyList<T> NotMatching)> PartitionAsync<T>(this DecisionModel model,
        IAsyncEnumerable<T> source, Func<T, DecisionsValue> selector, string instructions, double minProbability,
        DecisionCollectionOptions<T> options, CancellationToken cancellationToken = default)
    {
        DecisionGuard.Probability(minProbability, nameof(minProbability));
        Validate(model, source, selector, instructions, options);
        var results = new List<(int Index, T Item, bool Match)>();
        await foreach (var (index, item, response, id) in EvaluateAsync(model, source, selector, NoulFor(instructions), options, cancellationToken)
                           .ConfigureAwait(false))
            results.Add((index, item, response is not null && DecisionInterpretation.Noul(response, id).Noul >= minProbability));

        results.Sort((a, b) => a.Index.CompareTo(b.Index));
        return (results.Where(r => r.Match).Select(r => r.Item).ToArray(), results.Where(r => !r.Match).Select(r => r.Item).ToArray());
    }

    /// <summary>Chooses a member of <typeparamref name="TEnum"/> for every item and yields one group per member chosen, in enum order.</summary>
    public static IAsyncEnumerable<DecisionGroup<TEnum, T>> GroupByChoiceAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum, T>(this DecisionModel model,
        IEnumerable<T> source, Func<T, DecisionsValue> selector, string instructions, CancellationToken cancellationToken = default)
        where TEnum : struct, Enum
        => model.GroupByChoiceAsync<TEnum, T>(ToAsync(source), selector, instructions, new DecisionCollectionOptions<T>(), cancellationToken);

    /// <summary>Chooses a member of <typeparamref name="TEnum"/> for every item and groups them, with batching options.</summary>
    public static IAsyncEnumerable<DecisionGroup<TEnum, T>> GroupByChoiceAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum, T>(this DecisionModel model,
        IEnumerable<T> source, Func<T, DecisionsValue> selector, string instructions, DecisionCollectionOptions<T> options,
        CancellationToken cancellationToken = default)
        where TEnum : struct, Enum
        => model.GroupByChoiceAsync<TEnum, T>(ToAsync(source), selector, instructions, options, cancellationToken);

    /// <summary>Chooses a member of <typeparamref name="TEnum"/> for every item of an async source and groups them.</summary>
    public static IAsyncEnumerable<DecisionGroup<TEnum, T>> GroupByChoiceAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum, T>(this DecisionModel model,
        IAsyncEnumerable<T> source, Func<T, DecisionsValue> selector, string instructions, CancellationToken cancellationToken = default)
        where TEnum : struct, Enum
        => model.GroupByChoiceAsync<TEnum, T>(source, selector, instructions, new DecisionCollectionOptions<T>(), cancellationToken);

    /// <summary>
    /// Chooses a member of <typeparamref name="TEnum"/> for every item of an async source and groups them, with batching options.
    /// Groups are yielded once every item is answered, because any later item could join any group.
    /// </summary>
    public static IAsyncEnumerable<DecisionGroup<TEnum, T>> GroupByChoiceAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum, T>(this DecisionModel model,
        IAsyncEnumerable<T> source, Func<T, DecisionsValue> selector, string instructions, DecisionCollectionOptions<T> options,
        CancellationToken cancellationToken = default)
        where TEnum : struct, Enum
    {
        Validate(model, source, selector, instructions, options);
        var members = DecisionEnum<TEnum>.Members;
        var offered = members.Select(member => member.Value).ToHashSet();
        var criteria = members.ToDictionary(member => member.Label,
            member => member.Description is null ? null : DecisionsValue.Text(member.Description), StringComparer.Ordinal);
        return Group(model, source, selector, id => new ChoiceQuestion(Scoped(instructions, id), criteria), offered, options, cancellationToken);

        static async IAsyncEnumerable<DecisionGroup<TEnum, T>> Group(DecisionModel model, IAsyncEnumerable<T> source,
            Func<T, DecisionsValue> selector, Func<string, DecisionsQuestion> question, HashSet<TEnum> offered,
            DecisionCollectionOptions<T> options, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var results = new List<(int Index, DecisionItem<T, DecisionChoice<TEnum>> Result)>();
            await foreach (var (index, item, response, id) in EvaluateAsync(model, source, selector, question, options, cancellationToken)
                               .ConfigureAwait(false))
            {
                if (response is not null)
                    results.Add((index, new(item, DecisionInterpretation.Choice(response, id, offered))));
            }

            foreach (var group in results.OrderBy(r => r.Index).Select(r => r.Result)
                         .GroupBy(r => r.Result.Value).OrderBy(g => g.Key))
                yield return new DecisionGroup<TEnum, T>(group.Key, group.ToArray());
        }
    }

    /// <summary>Rates every item on the members of <typeparamref name="TEnum"/> and returns them from lowest to highest score.</summary>
    public static Task<IReadOnlyList<DecisionItem<T, DecisionScore<TEnum>>>> OrderByScoreAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum, T>(this DecisionModel model,
        IEnumerable<T> source, Func<T, DecisionsValue> selector, string instructions, CancellationToken cancellationToken = default)
        where TEnum : struct, Enum
        => model.OrderByScoreAsync<TEnum, T>(ToAsync(source), selector, instructions, new DecisionCollectionOptions<T>(), cancellationToken);

    /// <summary>Rates every item and returns them from lowest to highest score, with batching options.</summary>
    public static Task<IReadOnlyList<DecisionItem<T, DecisionScore<TEnum>>>> OrderByScoreAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum, T>(this DecisionModel model,
        IEnumerable<T> source, Func<T, DecisionsValue> selector, string instructions, DecisionCollectionOptions<T> options,
        CancellationToken cancellationToken = default)
        where TEnum : struct, Enum
        => model.OrderByScoreAsync<TEnum, T>(ToAsync(source), selector, instructions, options, cancellationToken);

    /// <summary>Rates every item of an async source and returns them from lowest to highest score.</summary>
    public static Task<IReadOnlyList<DecisionItem<T, DecisionScore<TEnum>>>> OrderByScoreAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum, T>(this DecisionModel model,
        IAsyncEnumerable<T> source, Func<T, DecisionsValue> selector, string instructions, CancellationToken cancellationToken = default)
        where TEnum : struct, Enum
        => model.OrderByScoreAsync<TEnum, T>(source, selector, instructions, new DecisionCollectionOptions<T>(), cancellationToken);

    /// <summary>
    /// Rates every item of an async source and returns them from lowest to highest probability-weighted score, with batching options.
    /// Ties keep source order. Reverse the list for highest first.
    /// </summary>
    public static async Task<IReadOnlyList<DecisionItem<T, DecisionScore<TEnum>>>> OrderByScoreAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum, T>(this DecisionModel model,
        IAsyncEnumerable<T> source, Func<T, DecisionsValue> selector, string instructions, DecisionCollectionOptions<T> options,
        CancellationToken cancellationToken = default)
        where TEnum : struct, Enum
    {
        Validate(model, source, selector, instructions, options);
        var members = DecisionEnum<TEnum>.Members;
        var levels = members.Select(member => member.Value).ToArray();
        var criteria = members.Select(member => DecisionsValue.Text(member.Description ?? member.Label)).ToArray();

        var results = new List<(int Index, DecisionItem<T, DecisionScore<TEnum>> Result)>();
        await foreach (var (index, item, response, id) in EvaluateAsync(model, source, selector,
                           itemId => new ScoreQuestion(Scoped(instructions, itemId), criteria), options, cancellationToken).ConfigureAwait(false))
        {
            if (response is not null)
                results.Add((index, new(item, DecisionInterpretation.Score(response, id, levels))));
        }
        return results.OrderBy(r => r.Result.Result.Score).ThenBy(r => r.Index).Select(r => r.Result).ToArray();
    }

    /// <summary>
    /// Reads the source in chunks and sends each chunk as one batched request. Prefiltered items are yielded straight away with a
    /// null response. Items are yielded with their source index, the response, and their question ID.
    /// </summary>
    private static async IAsyncEnumerable<(int Index, T Item, DecisionsResponse? Response, string QuestionId)> EvaluateAsync<T>(
        DecisionModel model, IAsyncEnumerable<T> source, Func<T, DecisionsValue> selector, Func<string, DecisionsQuestion> question,
        DecisionCollectionOptions<T> options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var batch = new DecisionsBatchOptions<(int Index, T Item)>
        {
            ItemId = entry => ItemId(entry.Index),
            State = chunk => DecisionsValue.FromObject(new
            {
                items = chunk.Select(entry => new { id = ItemId(entry.Index), input = selector(entry.Item) }).ToArray(),
            }),
            Questions = entry => new Dictionary<string, DecisionsQuestion> { [QuestionKey] = question(ItemId(entry.Index)) },
            Model = model.ModelId,
            ItemsPerRequest = options.ItemsPerRequest,
            ContinueOnError = false,
        };

        var pending = new List<(int Index, T Item)>(options.ItemsPerRequest);
        var index = 0;
        await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var entry = (index++, item);
            if (options.Prefilter is { } prefilter && !prefilter(item))
            {
                yield return (entry.Item1, item, null, "");
                continue;
            }
            pending.Add(entry);
            if (pending.Count < options.ItemsPerRequest) continue;
            await foreach (var result in SendAsync(pending).ConfigureAwait(false)) yield return result;
            pending.Clear();
        }
        if (pending.Count > 0)
            await foreach (var result in SendAsync(pending).ConfigureAwait(false)) yield return result;

        async IAsyncEnumerable<(int, T, DecisionsResponse?, string)> SendAsync(List<(int Index, T Item)> chunk)
        {
            await foreach (var result in model.Client.EvaluateBatchesAsync(chunk.ToArray(), batch, cancellationToken).ConfigureAwait(false))
            {
                foreach (var (i, item) in result.Items)
                    yield return (i, item, result.Response, DecisionsBatch.QuestionId(ItemId(i), QuestionKey));
            }
        }
    }

    private static Func<string, DecisionsQuestion> NoulFor(string instructions)
        => id => new NoulQuestion(Scoped(instructions, id));

    private static string Scoped(string instructions, string itemId) => $"About item \"{itemId}\" only: {instructions}";

    private static string ItemId(int index) => $"item{index}";

    private static void Validate<T>(DecisionModel model, object source, Func<T, DecisionsValue> selector, string instructions,
        DecisionCollectionOptions<T> options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentException.ThrowIfNullOrWhiteSpace(instructions);
        ArgumentNullException.ThrowIfNull(options);
        if (options.ItemsPerRequest < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "ItemsPerRequest must be at least 1.");
    }

    private static IAsyncEnumerable<T> ToAsync<T>(IEnumerable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Iterate(source);

        static async IAsyncEnumerable<T> Iterate(IEnumerable<T> source)
        {
            foreach (var item in source)
            {
                yield return item;
                await Task.CompletedTask.ConfigureAwait(false);
            }
        }
    }
}
