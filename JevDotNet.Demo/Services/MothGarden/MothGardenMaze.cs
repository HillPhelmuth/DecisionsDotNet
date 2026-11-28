using System.ComponentModel;

namespace JevDotNet.Demo.Services.MothGarden;

/// <summary>A direction through the maze. Member names are the wire labels the moths' decision model chooses from.</summary>
public enum Heading
{
    [Description("Head up.")] Up,
    [Description("Head down.")] Down,
    [Description("Head left.")] Left,
    [Description("Head right.")] Right
}

public static class HeadingExtensions
{
    public static Heading Opposite(this Heading heading) => heading switch
    {
        Heading.Up => Heading.Down,
        Heading.Down => Heading.Up,
        Heading.Left => Heading.Right,
        _ => Heading.Left
    };

    public static string Word(this Heading heading) => heading switch
    {
        Heading.Up => "up",
        Heading.Down => "down",
        Heading.Left => "left",
        _ => "right"
    };
}

/// <summary>A cell in the maze, counted from zero. <see cref="Text"/> counts from one, for people and models.</summary>
public readonly record struct MazeCell(int Column, int Row)
{
    public MazeCell Step(Heading heading) => heading switch
    {
        Heading.Up => this with { Row = Row - 1 },
        Heading.Down => this with { Row = Row + 1 },
        Heading.Left => this with { Column = Column - 1 },
        _ => this with { Column = Column + 1 }
    };

    public string Text => $"column {Column + 1}, row {Row + 1}";
}

/// <summary>
/// The Moth Garden hedge maze. <c>#</c> is hedge, <c>.</c> is pollen, <c>o</c> is a night bloom,
/// <c>F</c> is where the firefly starts and <c>M</c> marks the moths' roosts.
/// </summary>
public static class MothGardenMaze
{
    private static readonly string[] Layout =
    [
        "###################",
        "#o.......#.......o#",
        "#.##.###.#.###.##.#",
        "#.................#",
        "#.##.#.#####.#.##.#",
        "#....#...#...#....#",
        "####.###.#.###.####",
        "####.#...M...#.####",
        "####.#.##.##.#.####",
        "####...#MMM#...####",
        "####.#.#####.#.####",
        "####.#.......#.####",
        "#........#........#",
        "#.##.###.#.###.##.#",
        "#o.#.....F.....#.o#",
        "##.#.#.#####.#.#.##",
        "#....#...#...#....#",
        "#.######.#.######.#",
        "#.................#",
        "###################"
    ];

    public static readonly IReadOnlyList<Heading> Headings = [Heading.Up, Heading.Left, Heading.Down, Heading.Right];

    public static int Columns => Layout[0].Length;
    public static int Rows => Layout.Length;

    public static IReadOnlyList<MazeCell> OpenCells { get; } = Cells(ch => ch != '#');
    public static IReadOnlyList<MazeCell> PollenCells { get; } = Cells(ch => ch == '.');
    public static IReadOnlyList<MazeCell> BloomCells { get; } = Cells(ch => ch == 'o');
    public static IReadOnlyList<MazeCell> RoostCells { get; } = Cells(ch => ch == 'M');
    public static MazeCell FireflyStart { get; } = Cells(ch => ch == 'F').Single();

    private static readonly Dictionary<MazeCell, int> Index = OpenCells.Select((cell, i) => (cell, i)).ToDictionary(p => p.cell, p => p.i);
    private static readonly int[,] Distances = AllDistances();

    public static bool IsOpen(MazeCell cell)
        => cell.Row >= 0 && cell.Row < Rows && cell.Column >= 0 && cell.Column < Columns && Layout[cell.Row][cell.Column] != '#';

    public static IEnumerable<Heading> OpenHeadings(MazeCell cell) => Headings.Where(heading => IsOpen(cell.Step(heading)));

    /// <summary>Steps along the hedges between two open cells.</summary>
    public static int Distance(MazeCell from, MazeCell to) => Distances[Index[from], Index[to]];

    /// <summary>The open cell closest, in a straight line, to any point, including points outside the maze.</summary>
    public static MazeCell NearestOpen(int column, int row)
        => OpenCells.MinBy(cell => Math.Abs(cell.Column - column) + Math.Abs(cell.Row - row) + cell.Row * 0.001 + cell.Column * 0.000001);

    private static List<MazeCell> Cells(Func<char, bool> match)
    {
        var cells = new List<MazeCell>();
        for (var row = 0; row < Layout.Length; row++)
            for (var column = 0; column < Layout[row].Length; column++)
                if (match(Layout[row][column])) cells.Add(new MazeCell(column, row));
        return cells;
    }

    private static int[,] AllDistances()
    {
        var count = OpenCells.Count;
        var distances = new int[count, count];
        for (var start = 0; start < count; start++)
        {
            for (var i = 0; i < count; i++) distances[start, i] = int.MaxValue;
            distances[start, start] = 0;
            var queue = new Queue<MazeCell>([OpenCells[start]]);
            while (queue.TryDequeue(out var cell))
            {
                var steps = distances[start, Index[cell]];
                foreach (var heading in Headings)
                {
                    var next = cell.Step(heading);
                    if (!IsOpen(next) || distances[start, Index[next]] != int.MaxValue) continue;
                    distances[start, Index[next]] = steps + 1;
                    queue.Enqueue(next);
                }
            }
        }
        return distances;
    }
}
