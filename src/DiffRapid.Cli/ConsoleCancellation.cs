using System;
using System.Threading;

namespace DiffRapid.Hosting;

/// <summary>
/// Turns the first Ctrl+C into a cancellation request for the token; a second Ctrl+C ends the process as usual,
/// so work that does not observe the token can still be stopped.
/// </summary>
/// <remarks>
/// Shared source: DiffRapid.Mcp compiles this file too (a linked Compile item in its project file), so it stays
/// free of CLI types.
/// </remarks>
internal sealed class ConsoleCancellation : IDisposable
{
    private readonly CancellationTokenSource _source = new();

    public ConsoleCancellation() => Console.CancelKeyPress += OnCancelKeyPress;

    /// <summary>Cancelled by the first Ctrl+C.</summary>
    public CancellationToken Token => _source.Token;

    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancelKeyPress;
        _source.Dispose();
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs eventArgs)
    {
        if (_source.IsCancellationRequested)
        {
            return;
        }

        eventArgs.Cancel = true;
        _source.Cancel();
    }
}
