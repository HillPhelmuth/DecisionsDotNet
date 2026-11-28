using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DecisionsDotNet;
using JevDotNet.Demo.Services;
using JevDotNet.Demo.Services.MothGarden;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace JevDotNet.Demo.Components.Pages;

public partial class MothGarden
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(190);
    private static readonly JsonSerializerOptions PrettyJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [Inject] private DemoEvaluationService Evaluation { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    private MothGardenGame Game = new(Environment.TickCount);
    private MothDirector Director = default!;
    private ElementReference Board;
    private CancellationTokenSource? Session;
    private string Model = DemoDecisionModels.DefaultModelId;
    private bool UseModel;
    private bool Running;
    private bool Disposed;
    private DecisionsRequest? LastRequest;

    private bool ModelSteering => UseModel && Evaluation.HasApiKey && DemoDecisionModels.IsSupported(Model) && !Director.Halted;

    protected override void OnInitialized()
    {
        UseModel = Evaluation.HasApiKey;
        Director = NewDirector();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        try
        {
            // Blazor can only cancel every keydown or none. The module cancels just the steering keys and Space, so Tab still leaves the board.
            await using var module = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/MothGarden.razor.js");
            await module.InvokeVoidAsync("captureSteeringKeys", Board);
        }
        catch (JSDisconnectedException) { }
    }

    private MothDirector NewDirector() => new(Game, action => Disposed ? Task.CompletedTask : InvokeAsync(action));

    private async Task StartAsync()
    {
        if (Running || Game.IsOver) return;
        Game.Start();
        Running = true;
        var session = new CancellationTokenSource();
        Session = session;
        _ = RunAsync(session);
        await Board.FocusAsync();
    }

    private async Task RunAsync(CancellationTokenSource session)
    {
        var token = session.Token;
        // Requests are observed so the page can show the latest one. The model is fixed once the game starts.
        var model = ModelSteering ? Evaluation.ForModel(Model, request => LastRequest = request) : null;
        try
        {
            while (!token.IsCancellationRequested && !Game.IsOver)
            {
                var tickStarted = Stopwatch.GetTimestamp();
                await InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;
                    Game.Advance();
                    if (model is not null && !Game.IsOver) Director.Dispatch(model, token);
                    StateHasChanged();
                });
                var wait = TickInterval - Stopwatch.GetElapsedTime(tickStarted);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(Session, session))
            {
                Session = null;
                Running = false;
            }
            session.Dispose();
            if (!Disposed) await InvokeAsync(StateHasChanged);
        }
    }

    private async Task PauseAsync()
    {
        Pause();
        await Board.FocusAsync();
    }

    private void Pause()
    {
        Session?.Cancel();
        Session = null;
        Running = false;
    }

    private void Reset()
    {
        Pause();
        Game = new MothGardenGame(Environment.TickCount);
        Director = NewDirector();
        LastRequest = null;
    }

    private async Task SteerAsync(Heading heading)
    {
        await SteerOrStartAsync(heading);
        await Board.FocusAsync();
    }

    private async Task SteerOrStartAsync(Heading heading)
    {
        if (Game.IsOver) return;
        Game.Steer(heading);
        if (!Running) await StartAsync();
    }

    private async Task HandleKeyDownAsync(KeyboardEventArgs args)
    {
        Heading? heading = args.Key switch
        {
            "ArrowUp" or "w" or "W" => Heading.Up,
            "ArrowDown" or "s" or "S" => Heading.Down,
            "ArrowLeft" or "a" or "A" => Heading.Left,
            "ArrowRight" or "d" or "D" => Heading.Right,
            _ => null
        };
        if (heading is { } steer) await SteerOrStartAsync(steer);
        else if (args.Key == " ")
        {
            if (Running) Pause();
            else await StartAsync();
        }
    }

    private string BoardState => Game.Status switch
    {
        GardenStatus.Won => "GARDEN CLEARED",
        GardenStatus.Lost => "GAME OVER",
        GardenStatus.Caught => "CAUGHT",
        _ when !Running && Game.Status != GardenStatus.Ready => "PAUSED",
        _ when Game.BloomRemaining > 0 => "BLAZING",
        _ when Running => "IN FLIGHT",
        _ => "READY"
    };

    private string OverlayTitle => Game.Status switch
    {
        GardenStatus.Won => "Garden cleared!",
        GardenStatus.Lost => "Caught for good.",
        GardenStatus.Ready => "Moth Garden",
        _ => "Paused"
    };

    private string OverlayHint => Game.Status switch
    {
        GardenStatus.Won or GardenStatus.Lost => $"Final score {Game.Score}. Reset to play again.",
        GardenStatus.Ready => "Press an arrow key or Start to fly.",
        _ => "Press Space or Resume."
    };

    private string StatusMessage => Game.Status switch
    {
        GardenStatus.Ready => ModelSteering
            ? "Ready. The moths ask the model at every junction once you start."
            : "Ready. The moths fly on reflex in this game.",
        GardenStatus.Caught => $"Caught! {Game.Lives} {(Game.Lives == 1 ? "life" : "lives")} left.",
        GardenStatus.Won => $"You gathered all the pollen with {Game.Lives} {(Game.Lives == 1 ? "life" : "lives")} to spare.",
        GardenStatus.Lost => "The moths caught you. Reset to try again.",
        _ when Game.BloomRemaining > 0 => "You're blazing! Catch the dazzled moths before the glow fades.",
        _ => "Gather the pollen and keep away from the moths."
    };

    private static string CellStyle(MazeCell cell)
        => $"grid-column: {cell.Column + 1}; grid-row: {cell.Row + 1}";

    private static string SpriteStyle(MazeCell cell)
        => string.Create(CultureInfo.InvariantCulture,
            $"left: calc({cell.Column} * 100% / var(--cols)); top: calc({cell.Row} * 100% / var(--rows))");

    private static int Percent(double value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 100);

    public void Dispose()
    {
        Disposed = true;
        Session?.Cancel();
    }
}
