namespace DecisionsDotNet;

/// <summary>Retry settings shared by every provider's client options.</summary>
public abstract class DecisionsClientOptions
{
    /// <summary>Total attempts per request, including the first. Must be at least 1.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>The delay before the first retry. Later retries double it, up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest delay between attempts, including delays requested by a Retry-After header.</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The clock behind retry delays. Tests replace it to observe delays without waiting.</summary>
    internal TimeProvider Clock { get; init; } = TimeProvider.System;
}
