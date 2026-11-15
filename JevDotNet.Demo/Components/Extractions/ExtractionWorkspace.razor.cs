using System.Diagnostics;
using System.Text.Json;
using ExtractionsDotNet;
using JevDotNet.Demo.Services;
using Microsoft.AspNetCore.Components;

namespace JevDotNet.Demo.Components.Extractions;

public partial class ExtractionWorkspace : IAsyncDisposable
{
    [Inject] private ExtractionDemoService Service { get; set; } = default!;
    [Inject] private ILogger<ExtractionWorkspace> Logger { get; set; } = default!;

    [Parameter, EditorRequired] public string Title { get; set; } = "";
    [Parameter, EditorRequired] public string Description { get; set; } = "";
    [Parameter, EditorRequired] public ExtractionDemo Demo { get; set; } = default!;
    [Parameter] public string State { get; set; } = "";
    [Parameter] public ExtractionDemoAsset? Sample { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }

    private (ExtractionDemo Demo, string State, ExtractionDemoAsset? Sample)? _input;
    private CancellationTokenSource? _run;
    private ExtractionResponse? _response;
    private string? _status;
    private string? _error;
    private bool _disposed;

    private bool CanRun => !_disposed && _run is null && Service.HasApiKey
        && (Sample is not null || (State.Length <= 12000 && !string.IsNullOrWhiteSpace(State)));

    protected override void OnParametersSet()
    {
        var input = (Demo, State, Sample);
        if (_input == input)
            return;

        _input = input;
        StopRun();
        _response = null;
        _status = null;
        _error = null;
    }

    private async Task RunAsync()
    {
        if (!CanRun)
            return;

        using var run = new CancellationTokenSource();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        _run = run;
        _response = null;
        _status = "Extracting fields…";
        _error = null;
        var timer = Stopwatch.StartNew();

        try
        {
            var result = await Service.ExtractAsync(Demo, State, Sample, timeout.Token);
            if (ReferenceEquals(_run, run))
            {
                _response = result;
                _status = $"Completed in {timer.Elapsed.TotalSeconds:0.0} s.";
            }
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_run, run))
                _error = "The request timed out. Try extracting again.";
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException)
        {
            Logger.LogWarning(exception, "Extraction demo failed for {Model}.", Demo.Model);
            if (ReferenceEquals(_run, run))
            {
                _error = exception is ExtractionsApiException { StatusCode: var status }
                    ? $"OpenRouter returned HTTP {(int)status}. Try extracting again."
                    : "The extraction could not be completed. Try extracting again.";
            }
        }
        finally
        {
            if (ReferenceEquals(_run, run))
            {
                _run = null;
                if (_error is not null)
                    _status = null;
            }
        }
    }

    private void Cancel()
    {
        StopRun();
        _status = "Extraction canceled.";
    }

    private void StopRun()
    {
        var run = _run;
        _run = null;
        run?.Cancel();
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        StopRun();
        return ValueTask.CompletedTask;
    }
}
