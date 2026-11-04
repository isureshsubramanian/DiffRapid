using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DiffRapid.Ai;
using DiffRapid.VisualStudio.Options;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace DiffRapid.VisualStudio.UI;

/// <summary>
/// A strip under a comparison that shows an AI answer as it arrives. It only ever shows text: a proposal is used
/// only when the user chooses its action button.
/// </summary>
internal sealed class AiPanel : Border
{
    // Endpoints the user agreed to send to in this session; asked again after Visual Studio restarts.
    private static readonly HashSet<string> ConfirmedEndpoints = new(StringComparer.OrdinalIgnoreCase);

    private readonly DiffRapidPackage _package;
    private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private const double MinPanelHeight = 90;

    // The panel's height is kept for the session, so every comparison opens it at the size last dragged to.
    private static double s_height = 220;

    // A selectable answer in its own scroll viewer: the text box's built-in scrolling lost its place on every update.
    private readonly TextBox _text = new()
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(10, 6, 10, 8),
    };

    private readonly ScrollViewer _scroller = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        CanContentScroll = false,
    };

    private readonly Button _use;
    private readonly Button _stop;
    private CancellationTokenSource? _cancellation;
    private Action? _useAction;

    public AiPanel(DiffRapidPackage package)
    {
        _package = package;
        Visibility = Visibility.Collapsed;
        Height = s_height;
        BorderThickness = new Thickness(0, 1, 0, 0);
        SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
        SetResourceReference(BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
        _text.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
        _text.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        _status.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);

        _use = DiffTheme.CommandButton("Use proposal", "Put the proposed lines into the result; you can still edit or undo them", () => _useAction?.Invoke());
        _stop = DiffTheme.CommandButton("Stop", "Stop waiting for the answer", () => _cancellation?.Cancel());
        var copy = DiffTheme.CommandButton("Copy", "Copy the answer", () => Clipboard.SetText(_text.Text));
        var close = DiffTheme.CommandButton("Close", "Hide this panel", Close);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_use);
        buttons.Children.Add(_stop);
        buttons.Children.Add(copy);
        buttons.Children.Add(close);
        var header = new DockPanel { Margin = new Thickness(10, 4, 6, 0) };
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        var heading = new StackPanel { Orientation = Orientation.Horizontal };
        heading.Children.Add(_title);
        heading.Children.Add(_status);
        header.Children.Add(heading);

        // The top edge is a drag handle: dragging it up makes the panel taller and the comparison shorter.
        var resize = new Thumb { Height = 5, Cursor = Cursors.SizeNS, ToolTip = "Drag to resize the panel" };
        var grip = new FrameworkElementFactory(typeof(Border));
        grip.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        resize.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = grip };
        resize.DragDelta += (_, e) => Resize(Height - e.VerticalChange);

        _text.TextChanged += (_, _) =>
        {
            if (_followEnd)
            {
                _scroller.ScrollToEnd();
            }
        };
        _scroller.ScrollChanged += (_, e) =>
        {
            // Follow the answer as it streams only while the reader is at the bottom; scrolling up stops that.
            if (e.ExtentHeightChange == 0)
            {
                _followEnd = _scroller.VerticalOffset >= _scroller.ScrollableHeight - 2;
            }
        };
        _scroller.Content = _text;

        var layout = new DockPanel();
        DockPanel.SetDock(resize, Dock.Top);
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(resize);
        layout.Children.Add(header);
        layout.Children.Add(_scroller);
        Child = layout;
    }

    private bool _followEnd = true;

    private void Resize(double height)
    {
        // Leave the comparison above at least a few lines.
        var available = Parent is FrameworkElement host ? host.ActualHeight - 150 : double.MaxValue;
        Height = s_height = Math.Max(MinPanelHeight, Math.Min(height, Math.Max(MinPanelHeight, available)));
    }

    /// <summary>
    /// Sends a prompt built by <paramref name="buildPrompt"/> to the configured service and shows the answer.
    /// <paramref name="onReply"/> runs on the UI thread with the full reply and may offer an action through
    /// <see cref="OfferAction"/>. Nothing is sent without an endpoint in the settings and the user's go-ahead.
    /// </summary>
    public void Ask(string title, Func<DiffPromptBuilder, AiPrompt> buildPrompt, Action<string>? onReply = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _cancellation?.Cancel();
        _package.JoinableTaskFactory.RunAsync(() => AskAsync(title, buildPrompt, onReply)).FileAndForget("DiffRapid/AiAsk");
    }

    /// <summary>Shows a button that applies the answer; it is the only way an answer changes anything.</summary>
    public void OfferAction(string label, Action action)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _use.Content = label;
        _useAction = action;
        _use.Visibility = Visibility.Visible;
    }

    public void Close()
    {
        _cancellation?.Cancel();
        Visibility = Visibility.Collapsed;
    }

    private async Task AskAsync(string title, Func<DiffPromptBuilder, AiPrompt> buildPrompt, Action<string>? onReply)
    {
        await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
        var settings = await DiffRapidSettings.ReadAsync(_package);
        if (settings.Ai is not { } endpoint)
        {
            Show(title, "AI is off", "No AI service is set up. Choose one in Tools > Options > DiffRapid > AI: an OpenAI-compatible endpoint (OpenAI, Ollama, LM Studio...) or Azure OpenAI. Nothing is sent until you do.");
            return;
        }

        AiPrompt prompt;
        try
        {
            prompt = buildPrompt(new DiffPromptBuilder(endpoint));
        }
        catch (ArgumentException ex)
        {
            Show(title, "Cannot ask", ex.Message);
            return;
        }

        if (!Confirm(endpoint, prompt, settings.RedactSecrets))
        {
            return;
        }

        var cancellation = new CancellationTokenSource(endpoint.Timeout);
        _cancellation = cancellation;
        Show(title, "Waiting for the answer…", string.Empty);
        _stop.Visibility = Visibility.Visible;
        try
        {
            using var client = new ChatClient(endpoint);
            var reply = await Task.Run(
                () => client.StreamAsync(prompt, delta => Append(delta, cancellation), settings.RedactSecrets, cancellation.Token),
                cancellation.Token);
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_cancellation != cancellation)
            {
                return;
            }

            // The streamed text is normally the whole reply already; setting it again would lose the reader's place.
            if (_text.Text != reply)
            {
                _text.Text = reply;
            }

            _status.Text = "AI answer: check it before relying on it";
            onReply?.Invoke(reply);
        }
        catch (OperationCanceledException)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            _status.Text = cancellation.IsCancellationRequested && _cancellation == cancellation ? "Stopped" : _status.Text;
        }
        catch (Exception ex) when (ex is AiException or System.Net.Http.HttpRequestException or ArgumentException or FormatException or UriFormatException)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            _status.Text = "Failed";
            _text.Text = ex.Message;
        }
        finally
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_cancellation == cancellation)
            {
                _stop.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void Append(string delta, CancellationTokenSource cancellation)
    {
        _package.JoinableTaskFactory.RunAsync(async () =>
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_cancellation == cancellation)
            {
                _text.AppendText(delta);
            }
        }).FileAndForget("DiffRapid/AiAppend");
    }

    private void Show(string title, string status, string text)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _title.Text = title;
        _status.Text = status;
        _followEnd = true;
        _text.Text = text;
        _scroller.ScrollToTop();
        _use.Visibility = Visibility.Collapsed;
        _stop.Visibility = Visibility.Collapsed;
        _useAction = null;
        Visibility = Visibility.Visible;
    }

    /// <summary>Asks once per endpoint and session before any text leaves the computer, saying how much and where.</summary>
    private bool Confirm(AiEndpoint endpoint, AiPrompt prompt, bool redact)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (ConfirmedEndpoints.Contains(endpoint.BaseUrl))
        {
            return true;
        }

        var size = (prompt.SystemMessage.Length + prompt.UserMessage.Length) / 1024.0;
        var message = $"DiffRapid will send about {size:N1} KB of the compared text to {endpoint.BaseUrl}."
            + (redact ? " Passwords, keys and tokens it recognizes are masked first." : " Secrets are NOT masked (see Tools > Options > DiffRapid > AI).")
            + "\n\nSend it? You will not be asked again for this service until Visual Studio restarts.";
        var answer = VsShellUtilities.ShowMessageBox(_package, message, "DiffRapid AI", OLEMSGICON.OLEMSGICON_QUERY, OLEMSGBUTTON.OLEMSGBUTTON_YESNO, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND);
        if (answer != 6)
        {
            return false;
        }

        ConfirmedEndpoints.Add(endpoint.BaseUrl);
        return true;
    }
}
