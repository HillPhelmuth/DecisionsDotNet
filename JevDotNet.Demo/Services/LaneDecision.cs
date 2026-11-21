using System.ComponentModel;
using DecisionsDotNet;

namespace JevDotNet.Demo.Services;

public sealed record LaneObstacle(int Lane, double TimeToCollisionMs);
public sealed record LaneSnapshot(int SessionId, bool Running, bool Crashed, double CarLane, int Passed, double ElapsedMs, LaneObstacle[] Obstacles);
public sealed record LaneEvent(int SessionId, string Reason, LaneSnapshot Snapshot);

public static class LaneDecision
{
    public const int MaxDecisions = 1000;
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(10);

    public const string Question = "Which lane should the car choose?";

    public static string BuildState(LaneSnapshot snapshot) =>
        $"""
        Current lane: {snapshot.CarLane}
        Lanes: Lane 1 is left, lane 2 is center, lane 3 is right. You can cross intermediate lanes.
        Obstacles:\n {string.Join("\n- ", snapshot.Obstacles.Select(o => $"(lane {o.Lane}, time to collision {Math.Round(o.TimeToCollisionMs) + 20} ms)"))}

        Goal: Avoid collisions with obstacles. Prefer staying in the current lane when it has no obstacles. The road keeps moving while you decide.
        """;

    /// <summary>One choice among the <see cref="Lane"/> members. The SDK rejects labels it did not offer and invalid probabilities.</summary>
    public static Task<DecisionChoice<Lane>> ChooseAsync(DecisionModel model, LaneSnapshot snapshot, CancellationToken cancellationToken = default)
        => model.ChooseAsync<Lane>(BuildState(snapshot), Question, cancellationToken);

    /// <summary>The lane number to steer to. Also insists on a full distribution, so a sloppy model can't steer the car.</summary>
    public static int Validate(DecisionChoice<Lane> choice)
    {
        if (choice.Probabilities.Count != 3 || Math.Abs(choice.Probabilities.Values.Sum() - 1) > 0.02)
            throw new InvalidOperationException("The model returned an invalid lane choice or probability distribution.");
        return (int)choice.Value;
    }
}

public enum Lane
{
    [Description("Drive in the left lane.")] Left = 1,
    [Description("Drive in the center lane.")] Center = 2,
    [Description("Drive in the right lane.")] Right = 3
}
