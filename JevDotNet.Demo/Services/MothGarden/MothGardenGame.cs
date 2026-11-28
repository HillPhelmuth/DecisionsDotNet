namespace JevDotNet.Demo.Services.MothGarden;

public enum GardenStatus { Ready, Playing, Caught, Won, Lost }

public enum MothTemper { Stalker, Ambusher, Drifter, Pincer }

/// <summary>One moth. The game moves it; a decision model, when one is connected, picks its turns at junctions.</summary>
public sealed class Moth
{
    internal Moth(string name, string color, MothTemper temper, MazeCell roost, int releaseDelay)
    {
        Name = name;
        Color = color;
        Temper = temper;
        Roost = roost;
        ReleaseDelay = releaseDelay;
        Position = roost;
    }

    public string Name { get; }
    public string Color { get; }
    public MothTemper Temper { get; }
    public MazeCell Roost { get; }
    public int ReleaseDelay { get; }
    public MazeCell Position { get; internal set; }
    public Heading Heading { get; internal set; } = Heading.Up;
    public bool IsDazzled { get; internal set; }

    /// <summary>Ticks left before the moth leaves its roost.</summary>
    public int WaitTicks { get; internal set; }

    /// <summary>Counts the junctions the moth has passed, plus resets. A decision is only good for the ticket it was asked for.</summary>
    public int Ticket { get; internal set; }

    /// <summary>Changes whenever the moth jumps instead of walking, so the page can skip the slide animation.</summary>
    public int Generation { get; internal set; }

    public int ModelTurns { get; internal set; }
    public int ReflexTurns { get; internal set; }
    public bool? LastTurnByModel { get; internal set; }
    internal (int Ticket, Heading Heading)? Plan { get; set; }

    public string Goal => Temper switch
    {
        MothTemper.Stalker => "a stalker: head straight for the firefly",
        MothTemper.Ambusher => "an ambusher: get in front of the firefly, about four cells ahead of where it is flying",
        MothTemper.Drifter => "a drifter: patrol toward your waypoint, and close in only when the firefly comes within six steps",
        _ => "a pincer: come at the firefly from the side opposite the stalker, so it gets trapped between you"
    };
}

/// <summary>What a moth's decision model is asked before the moth reaches its next junction.</summary>
public sealed record MothDecisionPoint(
    Moth Moth, int Ticket, MazeCell Junction, int StepsAway, IReadOnlyDictionary<Heading, string> Options, string State, string Question);

/// <summary>
/// Moth Garden: a firefly gathers pollen in a hedge maze while four moths hunt it. The game is tick based and has no clock
/// of its own, so the page sets the pace and tests can step it. Moths take turns on their own reflexes unless a decision
/// for the junction they are standing on arrived in time.
/// </summary>
public sealed class MothGardenGame
{
    public const int StartingLives = 3;
    public const int BloomTicks = 40;
    public const int CaughtFreezeTicks = 8;
    public const int RespawnTicks = 15;
    public const int WaypointTicks = 45;
    public const int PollenPoints = 10;
    public const int BloomPoints = 50;
    public const int MothPoints = 200;

    private static readonly (string Name, string Color, MothTemper Temper, int Delay)[] Cast =
    [
        ("Dusk", "#9b7be0", MothTemper.Stalker, 0),
        ("Ember", "#f08a3c", MothTemper.Ambusher, 12),
        ("Haze", "#5fb3d9", MothTemper.Drifter, 24),
        ("Pip", "#e374ad", MothTemper.Pincer, 36)
    ];

    private readonly HashSet<MazeCell> _pollen = [.. MothGardenMaze.PollenCells];
    private readonly HashSet<MazeCell> _blooms = [.. MothGardenMaze.BloomCells];
    private readonly Random _random;
    private readonly List<Moth> _moths;
    private int _mothsEatenThisBloom;

    public MothGardenGame(int seed = 1)
    {
        _random = new Random(seed);
        _moths = Cast.Select((m, i) => new Moth(m.Name, m.Color, m.Temper, MothGardenMaze.RoostCells[i], m.Delay)).ToList();
        Waypoint = RandomOpenCell();
        ResetPositions();
    }

    public GardenStatus Status { get; private set; } = GardenStatus.Ready;
    public bool IsOver => Status is GardenStatus.Won or GardenStatus.Lost;
    public int Ticks { get; private set; }
    public int Lives { get; private set; } = StartingLives;
    public int Score { get; private set; }
    public int BloomRemaining { get; private set; }
    public int FreezeRemaining { get; private set; }
    public MazeCell Firefly { get; private set; }
    public Heading FireflyHeading { get; private set; }
    public Heading? QueuedHeading { get; private set; }
    public int FireflyGeneration { get; private set; }
    public MazeCell Waypoint { get; private set; }
    public IReadOnlyList<Moth> Moths => _moths;
    public int PollenLeft => _pollen.Count + _blooms.Count;
    public int TotalPollen => MothGardenMaze.PollenCells.Count + MothGardenMaze.BloomCells.Count;
    public bool HasPollen(MazeCell cell) => _pollen.Contains(cell);
    public bool HasBloom(MazeCell cell) => _blooms.Contains(cell);

    public void Start()
    {
        if (Status == GardenStatus.Ready) Status = GardenStatus.Playing;
    }

    /// <summary>Queues a turn. The firefly takes it at the first cell where that way is open.</summary>
    public void Steer(Heading heading)
    {
        if (!IsOver) QueuedHeading = heading;
    }

    /// <summary>Runs one tick: the firefly flies a cell, then each moth that is due flies a cell.</summary>
    public void Advance()
    {
        if (Status is not (GardenStatus.Playing or GardenStatus.Caught)) return;
        Ticks++;
        if (FreezeRemaining > 0)
        {
            if (--FreezeRemaining == 0) Status = GardenStatus.Playing;
            return;
        }
        if (Ticks % WaypointTicks == 0) Waypoint = RandomOpenCell();

        var fireflyBefore = Firefly;
        if (QueuedHeading is { } queued && MothGardenMaze.IsOpen(Firefly.Step(queued)))
        {
            FireflyHeading = queued;
            QueuedHeading = null;
        }
        if (MothGardenMaze.IsOpen(Firefly.Step(FireflyHeading))) Firefly = Firefly.Step(FireflyHeading);
        Gather();
        if (IsOver || Collide(fireflyBefore, null)) return;

        if (BloomRemaining > 0 && --BloomRemaining == 0)
            foreach (var moth in _moths) moth.IsDazzled = false;

        var mothsBefore = _moths.Select(moth => moth.Position).ToArray();
        foreach (var moth in _moths) Fly(moth);
        Collide(fireflyBefore, mothsBefore);
    }

    /// <summary>
    /// The next junction <paramref name="moth"/> will reach, with the turns it can take there, or null when it has nothing to
    /// decide (the game is not running). Corridors and corners have one way on, so only junctions need a decision.
    /// </summary>
    public MothDecisionPoint? NextDecision(Moth moth)
    {
        if (Status is not (GardenStatus.Playing or GardenStatus.Caught)) return null;
        var (junction, arriving, steps) = NextJunction(moth.Position, moth.Heading);
        if (steps < 0) return null;

        var target = TargetFor(moth);
        var options = new Dictionary<Heading, string>();
        foreach (var heading in Exits(junction, arriving))
        {
            var next = junction.Step(heading);
            var toFirefly = MothGardenMaze.Distance(next, Firefly) + 1;
            var text = moth.IsDazzled
                ? $"Fly {heading.Word()} to {next.Text}. The firefly would then be {toFirefly} steps away."
                : $"Fly {heading.Word()} to {next.Text}. The firefly would then be {toFirefly} steps away" +
                  (target == Firefly ? "." : $", and your goal at {target.Text} {MothGardenMaze.Distance(next, target) + 1} steps away.");
            options[heading] = text;
        }

        var others = string.Join("; ", _moths.Where(other => other != moth).Select(other => $"{other.Name} at {other.Position.Text}"));
        var dazzle = moth.IsDazzled
            ? $"The firefly is blazing from a night bloom for {BloomRemaining} more ticks. You are dazzled and slow, and it can catch you. Get as far from it as you can."
            : $"Your goal right now is {target.Text}. Catching the firefly is the aim.";
        var state =
            $"A hedge maze, {MothGardenMaze.Columns} columns by {MothGardenMaze.Rows} rows, at night. " +
            $"The firefly is at {Firefly.Text}, flying {FireflyHeading.Word()}. " +
            $"You are the moth {moth.Name}, {moth.Goal}. " +
            $"You are at {moth.Position.Text} flying {moth.Heading.Word()}, and reach the junction at {junction.Text} in {steps} steps. " +
            $"The other moths: {others}. {dazzle} Moths cannot turn back except at dead ends, so pick the turn that serves your goal.";
        return new MothDecisionPoint(moth, moth.Ticket, junction, steps, options, state,
            $"Which way should {moth.Name} turn at the junction at {junction.Text}?");
    }

    /// <summary>Hands a moth a turn for its next junction. Returns false when the moth has already passed it.</summary>
    public bool SubmitTurn(Moth moth, int ticket, Heading heading)
    {
        if (IsOver || ticket != moth.Ticket) return false;
        moth.Plan = (ticket, heading);
        return true;
    }

    /// <summary>The cell this moth's reflexes steer toward. A dazzled moth flees the firefly instead.</summary>
    public MazeCell TargetFor(Moth moth)
    {
        switch (moth.Temper)
        {
            case MothTemper.Ambusher:
                var ahead = Firefly;
                for (var i = 0; i < 4 && MothGardenMaze.IsOpen(ahead.Step(FireflyHeading)); i++) ahead = ahead.Step(FireflyHeading);
                return ahead;
            case MothTemper.Drifter:
                return MothGardenMaze.Distance(moth.Position, Firefly) <= 6 ? Firefly : Waypoint;
            case MothTemper.Pincer:
                var stalker = _moths[0].Position;
                return MothGardenMaze.NearestOpen(2 * Firefly.Column - stalker.Column, 2 * Firefly.Row - stalker.Row);
            default:
                return Firefly;
        }
    }

    private void Fly(Moth moth)
    {
        if (moth.WaitTicks > 0)
        {
            moth.WaitTicks--;
            return;
        }
        var due = moth.IsDazzled ? Ticks % 2 == 0 : Ticks % 5 != 0;
        if (!due) return;

        var exits = Exits(moth.Position, moth.Heading);
        Heading next;
        if (exits.Count == 0) next = moth.Heading.Opposite();
        else if (exits.Count == 1) next = exits[0];
        else
        {
            if (moth.Plan is { } plan && plan.Ticket == moth.Ticket && exits.Contains(plan.Heading))
            {
                next = plan.Heading;
                moth.ModelTurns++;
                moth.LastTurnByModel = true;
            }
            else
            {
                next = ReflexTurn(moth, exits);
                moth.ReflexTurns++;
                moth.LastTurnByModel = false;
            }
            moth.Plan = null;
            moth.Ticket++;
        }
        moth.Heading = next;
        moth.Position = moth.Position.Step(next);
    }

    /// <summary>The fallback when no decision arrived in time: the turn that gets closest to the target, or farthest from the firefly when dazzled.</summary>
    public Heading ReflexTurn(Moth moth, IReadOnlyList<Heading> exits)
    {
        if (moth.IsDazzled) return exits.MaxBy(heading => MothGardenMaze.Distance(moth.Position.Step(heading), Firefly));
        var target = TargetFor(moth);
        return exits.MinBy(heading => MothGardenMaze.Distance(moth.Position.Step(heading), target));
    }

    private void Gather()
    {
        if (_pollen.Remove(Firefly)) Score += PollenPoints;
        else if (_blooms.Remove(Firefly))
        {
            Score += BloomPoints;
            BloomRemaining = BloomTicks;
            _mothsEatenThisBloom = 0;
            foreach (var moth in _moths.Where(moth => moth.WaitTicks == 0))
            {
                moth.IsDazzled = true;
                moth.Heading = moth.Heading.Opposite();
                moth.Plan = null;
                moth.Ticket++;
            }
        }
        if (PollenLeft == 0) Status = GardenStatus.Won;
    }

    private bool Collide(MazeCell fireflyBefore, MazeCell[]? mothsBefore)
    {
        for (var i = 0; i < _moths.Count; i++)
        {
            var moth = _moths[i];
            var met = moth.Position == Firefly ||
                (mothsBefore is not null && moth.Position == fireflyBefore && mothsBefore[i] == Firefly);
            if (!met) continue;

            if (moth.IsDazzled)
            {
                Score += MothPoints << Math.Min(_mothsEatenThisBloom++, 3);
                SendHome(moth, RespawnTicks);
                continue;
            }

            Lives--;
            ResetPositions();
            if (Lives == 0) Status = GardenStatus.Lost;
            else
            {
                Status = GardenStatus.Caught;
                FreezeRemaining = CaughtFreezeTicks;
            }
            return true;
        }
        return false;
    }

    private void ResetPositions()
    {
        Firefly = MothGardenMaze.FireflyStart;
        FireflyHeading = Heading.Left;
        QueuedHeading = null;
        FireflyGeneration++;
        BloomRemaining = 0;
        foreach (var moth in _moths) SendHome(moth, moth.ReleaseDelay);
    }

    private static void SendHome(Moth moth, int wait)
    {
        moth.Position = moth.Roost;
        moth.Heading = Heading.Up;
        moth.IsDazzled = false;
        moth.WaitTicks = wait;
        moth.Plan = null;
        moth.Ticket++;
        moth.Generation++;
    }

    private static List<Heading> Exits(MazeCell cell, Heading heading)
        => MothGardenMaze.OpenHeadings(cell).Where(exit => exit != heading.Opposite()).ToList();

    private static (MazeCell Junction, Heading Arriving, int Steps) NextJunction(MazeCell cell, Heading heading)
    {
        for (var steps = 0; steps < MothGardenMaze.OpenCells.Count; steps++)
        {
            var exits = Exits(cell, heading);
            if (exits.Count >= 2) return (cell, heading, steps);
            heading = exits.Count == 1 ? exits[0] : heading.Opposite();
            cell = cell.Step(heading);
        }
        return (cell, heading, -1);
    }

    private MazeCell RandomOpenCell() => MothGardenMaze.OpenCells[_random.Next(MothGardenMaze.OpenCells.Count)];
}
