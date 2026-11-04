using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace DiffRapid.VisualStudio;

/// <summary>Thrown when a named step of an operation fails; the message says which step.</summary>
internal sealed class StepFailedException : Exception
{
    public StepFailedException(string step, Exception inner)
        : base($"{step} failed: {inner.Message}", inner)
    {
        Step = step;
    }

    public string Step { get; }
}

/// <summary>Error reporting: the "DiffRapid" Output window pane and the Visual Studio activity log.</summary>
internal static class Diagnostics
{
    private static readonly Guid OutputPaneGuid = new("8f4b5a3e-2c1d-4e7a-9b6f-0d2c3e4f5a61");
    private static IVsOutputWindowPane? _pane;

    /// <summary>Runs one step and wraps any failure with the step name.</summary>
    public static T Step<T>(string step, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is not StepFailedException and not OperationCanceledException)
        {
            throw new StepFailedException(step, ex);
        }
    }

    public static void Step(string step, Action action)
    {
        Step<object?>(step, () =>
        {
            action();
            return null;
        });
    }

    public static void LogError(Exception ex)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var details = ex.ToString();
        ActivityLog.TryLogError("DiffRapid", details);
        WriteLine($"[{DateTime.Now:HH:mm:ss}] ERROR {details}");
        AppendToLogFile($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR {details}");
    }

    /// <summary>Records something that went wrong without interrupting the user (activity log and log file).</summary>
    public static void LogWarning(string message)
    {
        ActivityLog.TryLogWarning("DiffRapid", message);
        AppendToLogFile($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] WARNING {message}");
    }

    /// <summary>Logs a step on the UI thread that took long enough for the user to notice (over 200 ms).</summary>
    public static void LogIfSlow(string step, System.Diagnostics.Stopwatch elapsed)
    {
        if (elapsed.ElapsedMilliseconds > 200)
        {
            AppendToLogFile($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] SLOW {step}: {elapsed.ElapsedMilliseconds:N0} ms");
        }
    }

    /// <summary>%LOCALAPPDATA%\DiffRapid\diffrapid.log, so errors survive after Visual Studio closes.</summary>
    public static string LogFilePath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiffRapid", "diffrapid.log");

    private static void AppendToLogFile(string text)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogFilePath)!);
            System.IO.File.AppendAllText(LogFilePath, text + Environment.NewLine + Environment.NewLine);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // Logging must never turn into a second failure.
        }
    }

    public static void WriteLine(string message)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var pane = GetPane();
        if (pane is null)
        {
            return;
        }

        pane.OutputStringThreadSafe(message + Environment.NewLine);
        pane.Activate();
    }

    private static IVsOutputWindowPane? GetPane()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_pane is not null)
        {
            return _pane;
        }

        if (Package.GetGlobalService(typeof(SVsOutputWindow)) is not IVsOutputWindow output)
        {
            return null;
        }

        var guid = OutputPaneGuid;
        if (ErrorHandler.Failed(output.GetPane(ref guid, out _pane)) || _pane is null)
        {
            output.CreatePane(ref guid, "DiffRapid", fInitVisible: 1, fClearWithSolution: 0);
            output.GetPane(ref guid, out _pane);
        }

        return _pane;
    }
}
