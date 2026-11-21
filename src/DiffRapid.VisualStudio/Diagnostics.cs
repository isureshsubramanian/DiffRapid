using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// Logs the exception's type and message (and the types of the exceptions inside it), with the user's profile
    /// folder written as %USERPROFILE% and other path segments that are the user name as %USERNAME%; stack traces are left out, as they add little and can name local paths.
    /// </summary>
    public static void LogError(Exception ex)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var details = Redact(Describe(ex));
        ActivityLog.TryLogError("DiffRapid", details);
        WriteLine($"[{DateTime.Now:HH:mm:ss}] ERROR {details}");
        AppendToLogFile($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR {details}");
    }

    /// <summary>Records something that went wrong without interrupting the user (activity log and log file).</summary>
    public static void LogWarning(string message)
    {
        message = Redact(message);
        ActivityLog.TryLogWarning("DiffRapid", message);
        AppendToLogFile($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] WARNING {message}");
    }

    private static string Describe(Exception ex)
    {
        var text = new StringBuilder(ex.GetType().FullName).Append(": ").Append(ex.Message);
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            text.Append(" ← ").Append(inner.GetType().FullName);
        }

        return text.ToString();
    }

    // The profile folder as written (C:\Users\Jane.Doe) and in its 8.3 form (C:\Users\JANEDO~1), either slash
    // separating the parts; and the user name as a whole path segment anywhere else (D:\Work\Jane.Doe\...).
    private static readonly Regex? ProfilePattern = BuildProfilePattern();
    private static readonly Regex? UserNamePattern = BuildUserNamePattern();

    // Paths under the profile folder carry the user name; the rest of the path still says which file it was.
    private static string Redact(string text)
    {
        if (ProfilePattern is not null)
        {
            text = ProfilePattern.Replace(text, "%USERPROFILE%");
        }

        return UserNamePattern is null ? text : UserNamePattern.Replace(text, "%USERNAME%");
    }

    private static Regex? BuildProfilePattern()
    {
        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');
            if (profile.Length < 3)
            {
                return null;
            }

            var forms = new List<string> { profile };
            if (ShortPath(profile) is { } shortForm && !string.Equals(shortForm, profile, StringComparison.OrdinalIgnoreCase))
            {
                forms.Add(shortForm);
            }

            var alternatives = forms.Select(form => string.Join(@"[\\/]", form.Split('\\', '/').Select(Regex.Escape)));
            return new Regex(string.Join("|", alternatives), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (Exception ex) when (ex is ArgumentException or PlatformNotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static Regex? BuildUserNamePattern()
    {
        try
        {
            var name = Environment.UserName;
            return string.IsNullOrWhiteSpace(name)
                ? null
                : new Regex(@"(?<=[\\/])" + Regex.Escape(name) + @"(?![\w.\-~$])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (Exception ex) when (ex is ArgumentException or PlatformNotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    // The 8.3 form of a path, or null when the volume has none or it cannot be read.
    private static string? ShortPath(string path)
    {
        var buffer = new StringBuilder(260); // MAX_PATH; a longer form is read again at its reported length.
        var length = GetShortPathName(path, buffer, buffer.Capacity);
        if (length > buffer.Capacity)
        {
            buffer = new StringBuilder(length);
            length = GetShortPathName(path, buffer, buffer.Capacity);
        }

        return length > 0 && length <= buffer.Capacity ? buffer.ToString().TrimEnd('\\', '/') : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetShortPathNameW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetShortPathName(string longPath, StringBuilder shortPath, int bufferLength);

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

    // Past this size the log is renamed to diffrapid.1.log (replacing the previous one) and a new one started, so it
    // never takes more than about twice this. 1 MB is an estimate: thousands of entries, still quick to open.
    private const long MaxLogFileSize = 1024 * 1024;

    private static readonly object LogFileLock = new();

    private static void AppendToLogFile(string text)
    {
        try
        {
            // Warnings come from background threads too.
            lock (LogFileLock)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogFilePath)!);
                var log = new System.IO.FileInfo(LogFilePath);
                if (log.Exists && log.Length > MaxLogFileSize)
                {
                    var previous = System.IO.Path.ChangeExtension(LogFilePath, ".1.log");
                    System.IO.File.Delete(previous);
                    System.IO.File.Move(LogFilePath, previous);
                }

                System.IO.File.AppendAllText(LogFilePath, text + Environment.NewLine + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // Logging must never turn into a second failure.
        }
    }

    public static void WriteLine(string message) => WriteLines(new[] { message });

    /// <summary>Writes the lines to the DiffRapid Output pane and brings the pane forward once.</summary>
    public static void WriteLines(System.Collections.Generic.IEnumerable<string> messages)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var pane = GetPane();
        if (pane is null)
        {
            return;
        }

        foreach (var message in messages)
        {
            pane.OutputStringThreadSafe(message + Environment.NewLine);
        }

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
