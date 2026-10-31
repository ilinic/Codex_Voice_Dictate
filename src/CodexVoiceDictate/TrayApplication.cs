using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CodexVoiceDictate;

internal enum Stage { Ready, Recording, Transcribing, Correcting, Inserting, Done, Error, Cancelled }

/// <summary>No main window: the tray icon is the primary UI and communicates stage through a glyph and color.</summary>
internal sealed class TrayApplication : ApplicationContext
{
    private const int MaxRecordSeconds = 120;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _stageItem;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _sendItem;
    private readonly ToolStripMenuItem _cancelItem;
    private readonly ToolStripMenuItem _retryItem;
    private readonly ToolStripMenuItem _copyItem;
    private readonly GlobalHotkeys _hotkeys;
    private readonly System.Windows.Forms.Timer _tick;
    private readonly System.Windows.Forms.Timer _reset;
    private readonly OpenAiSpeechClient _client = new();
    private AppSettings _settings;
    private WaveCapture? _capture;
    private CancellationTokenSource? _cancellation;
    private string _lastTranscript = "";
    private string _stageDetails = "Ready";
    private Stage _stage = Stage.Ready;
    private Icon? _icon;
    private bool _exiting;

    public TrayApplication()
    {
        _settings = AppSettings.Load();
        _menu = new ContextMenuStrip();
        _stageItem = new ToolStripMenuItem("Ready") { Enabled = false };
        _startItem = new ToolStripMenuItem("Start / stop (insert only)", null, (_, _) => ToggleRecording());
        _sendItem = new ToolStripMenuItem("Stop & send", null, (_, _) => Finish(true));
        _cancelItem = new ToolStripMenuItem("Cancel (Esc)", null, (_, _) => Cancel());
        _retryItem = new ToolStripMenuItem("Retry inserting last transcript", null, (_, _) => RetryInsertion());
        _copyItem = new ToolStripMenuItem("Copy last transcript", null, (_, _) => CopyLast());
        _menu.Items.AddRange([
            _stageItem, new ToolStripSeparator(), _startItem, _sendItem, _cancelItem,
            new ToolStripSeparator(), _retryItem, _copyItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Bind Codex composer (Ctrl+Alt+B)", null, (_, _) => ExplainBinding()),
            new ToolStripMenuItem("Settings...", null, (_, _) => ShowSettings()),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitThread())
        ]);
        _tray = new NotifyIcon { Text = "Codex Voice Dictate — Ready", ContextMenuStrip = _menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowSettings();
        _tick = new System.Windows.Forms.Timer { Interval = 1000 };
        _tick.Tick += (_, _) => { if (_stage == Stage.Recording && _capture?.Duration.TotalSeconds >= MaxRecordSeconds) Finish(false); };
        _reset = new System.Windows.Forms.Timer { Interval = 5000 };
        _reset.Tick += (_, _) => { _reset.Stop(); if (_stage is Stage.Done or Stage.Cancelled) SetStage(Stage.Ready, "Ready"); };
        _hotkeys = new GlobalHotkeys(HandleHotkey);
        SetStage(Stage.Ready, "Ready");
        RegisterHotkeys();
        if (string.IsNullOrWhiteSpace(_settings.GetApiKey()))
            _tray.ShowBalloonTip(4000, "Codex Voice Dictate", "Open Settings to configure an OpenAI API key.", ToolTipIcon.Info);
    }

    private void RegisterHotkeys()
    {
        try
        {
            Shortcut.TryParse(_settings.InsertShortcut, out var insert);
            Shortcut.TryParse(_settings.SendShortcut, out var send);
            _hotkeys.RegisterMain(insert, send);
        }
        catch (Exception error)
        {
            SetStage(Stage.Error, "Hotkey registration failed: " + error.Message);
            _tray.ShowBalloonTip(6000, "Global shortcut unavailable", error.Message, ToolTipIcon.Warning);
        }
    }

    private void HandleHotkey(int id)
    {
        switch (id)
        {
            case 1: ToggleRecording(); break;
            case 2: Finish(true); break;
            case 3: Cancel(); break;
            case 4: BindFocusedComposer(); break;
        }
    }

    private void ToggleRecording()
    {
        if (_stage == Stage.Recording) { Finish(false); return; }
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(_settings.GetApiKey()))
        {
            SetStage(Stage.Error, "OpenAI API key required");
            ShowSettings();
            return;
        }
        try
        {
            _reset.Stop();
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            _capture = new WaveCapture();
            _lastTranscript = "";
            SetStage(Stage.Recording, "Recording • press stop key or Esc");
            _tick.Start();
            try { _hotkeys.EnableCancel(true); }
            catch { /* Escape is best effort when another application reserved it. Tray Cancel still works. */ }
        }
        catch (Exception error)
        {
            _capture?.Dispose();
            _capture = null;
            SetError("Could not start microphone: " + error.Message);
        }
    }

    private bool IsBusy => _stage is Stage.Recording or Stage.Transcribing or Stage.Correcting or Stage.Inserting;

    private async void Finish(bool send)
    {
        if (_stage != Stage.Recording || _capture == null || _cancellation == null) return;
        _tick.Stop();
        var capture = _capture;
        var cancellation = _cancellation;
        SetStage(Stage.Transcribing, "Preparing audio...");
        try
        {
            byte[] wav = await capture.StopAsync();
            if (cancellation.IsCancellationRequested) return;
            if (capture.Duration.TotalMilliseconds < 250 || wav.Length < 512)
                throw new InvalidOperationException("Recording was too short.");
            if (wav.Length > 24 * 1024 * 1024)
                throw new InvalidOperationException("Recording is too large for the transcription API.");
            capture.Dispose();
            _capture = null;
            SetStage(Stage.Transcribing, "Transcribing speech...");
            string key = _settings.GetApiKey();
            string result = await _client.TranscribeAsync(wav, key, _settings, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;

            if (_settings.CorrectText)
            {
                SetStage(Stage.Correcting, "Correcting technical terms...");
                try { result = await _client.CorrectAsync(result, key, _settings, cancellation.Token); }
                catch (Exception error) when (error is IOException or HttpRequestException)
                { /* Preserve the raw recognition result if optional correction fails. */ }
            }
            if (cancellation.IsCancellationRequested) return;
            _lastTranscript = result;
            SetStage(Stage.Inserting, "Finding Codex composer...");
            DeliveryResult delivery = CodexComposer.Insert(result, send);
            if (delivery.Verified)
            {
                SetStage(Stage.Done, delivery.Message);
                if (_settings.ShowNotifications)
                    _tray.ShowBalloonTip(1500, "Codex Voice Dictate", delivery.Message, ToolTipIcon.Info);
            }
            else SetError(delivery.Message);
        }
        catch (OperationCanceledException) { /* Escape cancellation. */ }
        catch (Exception error) { if (!cancellation.IsCancellationRequested) SetError(error.Message); }
        finally
        {
            if (ReferenceEquals(_capture, capture)) { _capture = null; capture.Dispose(); }
            _hotkeys.EnableCancel(false);
        }
    }

    private void Cancel()
    {
        if (!IsBusy) return;
        _cancellation?.Cancel();
        if (_capture is { } capture)
        {
            _capture = null;
            capture.Cancel();
            _ = CleanupCancelledCapture(capture);
        }
        _tick.Stop();
        _hotkeys.EnableCancel(false);
        SetStage(Stage.Cancelled, "Cancelled");
    }

    private static async Task CleanupCancelledCapture(WaveCapture capture)
    {
        try { await capture.StopAsync(); }
        catch { /* Cancellation discards audio and any capture errors. */ }
        finally { capture.Dispose(); }
    }

    private void RetryInsertion()
    {
        if (IsBusy || string.IsNullOrEmpty(_lastTranscript)) return;
        try
        {
            SetStage(Stage.Inserting, "Retrying insertion...");
            var delivery = CodexComposer.Insert(_lastTranscript, false);
            if (delivery.Verified) SetStage(Stage.Done, delivery.Message);
            else SetError(delivery.Message);
        }
        catch (Exception error) { SetError(error.Message); }
    }

    private void CopyLast()
    {
        if (string.IsNullOrEmpty(_lastTranscript)) return;
        Clipboard.SetText(_lastTranscript);
        SetStage(Stage.Done, "Transcript copied to clipboard");
    }

    private void BindFocusedComposer()
    {
        if (IsBusy) return;
        try
        {
            string result = CodexComposer.BindCurrent(_settings);
            SetStage(Stage.Done, result);
            _tray.ShowBalloonTip(2000, "Composer linked", result, ToolTipIcon.Info);
        }
        catch (Exception error) { SetError(error.Message); }
    }

    private static void ExplainBinding()
    {
        MessageBox.Show("1. Open the Codex desktop application.\n2. Click inside the chat message composer.\n3. Press Ctrl+Alt+B.\n\nThis records an accessibility fingerprint of the intended editor. You can then dictate even when focus has moved elsewhere.",
            "Bind Codex composer", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ShowSettings()
    {
        if (IsBusy) return;
        _hotkeys.UnregisterAll();
        using var form = new SettingsForm(_settings);
        form.ShowDialog();
        RegisterHotkeys();
        if (_stage == Stage.Error && _stageDetails.StartsWith("Hotkey registration", StringComparison.Ordinal)) return;
        SetStage(Stage.Ready, "Ready");
    }

    private void SetError(string message)
    {
        SetStage(Stage.Error, message);
        if (_settings.ShowNotifications)
            _tray.ShowBalloonTip(5000, "Codex Voice Dictate", message.Length > 220 ? message[..220] : message, ToolTipIcon.Warning);
    }

    private void SetStage(Stage stage, string details)
    {
        _stage = stage;
        _stageDetails = details;
        _stageItem.Text = $"{stage}: {details}";
        _startItem.Enabled = stage is Stage.Ready or Stage.Recording or Stage.Done or Stage.Error or Stage.Cancelled;
        _sendItem.Enabled = stage == Stage.Recording;
        _cancelItem.Enabled = IsBusy;
        _retryItem.Enabled = !IsBusy && !string.IsNullOrWhiteSpace(_lastTranscript);
        _copyItem.Enabled = !string.IsNullOrWhiteSpace(_lastTranscript);
        var nextIcon = DrawIcon(stage);
        _tray.Icon = nextIcon;
        _icon?.Dispose();
        _icon = nextIcon;
        string tooltip = $"Codex Voice Dictate — {stage}";
        _tray.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
        _reset.Stop();
        if (stage is Stage.Done or Stage.Cancelled) _reset.Start();
    }

    private static Icon DrawIcon(Stage stage)
    {
        using var bitmap = new Bitmap(32, 32);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            Color color = stage switch
            {
                Stage.Ready => Color.FromArgb(54, 156, 105),
                Stage.Recording => Color.FromArgb(226, 65, 65),
                Stage.Transcribing => Color.FromArgb(232, 164, 48),
                Stage.Correcting => Color.FromArgb(139, 91, 213),
                Stage.Inserting => Color.FromArgb(44, 143, 224),
                Stage.Done => Color.FromArgb(30, 172, 116),
                Stage.Error => Color.FromArgb(220, 95, 44),
                _ => Color.FromArgb(126, 134, 147)
            };
            using var fill = new SolidBrush(color);
            using var pen = new Pen(Color.White, 2.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            graphics.FillEllipse(fill, 2, 2, 28, 28);
            switch (stage)
            {
                case Stage.Recording:
                    graphics.FillEllipse(Brushes.White, 11, 11, 10, 10); break;
                case Stage.Transcribing:
                    graphics.DrawLine(pen, 10, 12, 10, 20);
                    graphics.DrawLine(pen, 16, 8, 16, 24);
                    graphics.DrawLine(pen, 22, 12, 22, 20); break;
                case Stage.Correcting:
                    graphics.DrawLine(pen, 10, 22, 21, 11);
                    graphics.DrawLine(pen, 17, 10, 22, 15);
                    graphics.DrawLine(pen, 10, 10, 10, 14); break;
                case Stage.Inserting:
                    graphics.DrawLine(pen, 8, 16, 22, 16);
                    graphics.DrawLine(pen, 17, 10, 23, 16);
                    graphics.DrawLine(pen, 17, 22, 23, 16); break;
                case Stage.Done:
                    graphics.DrawLines(pen, new Point[] { new(9, 16), new(14, 21), new(23, 11) }); break;
                case Stage.Error:
                    graphics.DrawLine(pen, 16, 9, 16, 19);
                    graphics.FillEllipse(Brushes.White, 14.5f, 22, 3, 3); break;
                case Stage.Cancelled:
                    graphics.DrawLine(pen, 11, 11, 21, 21);
                    graphics.DrawLine(pen, 21, 11, 11, 21); break;
                default:
                    graphics.FillRoundedRectangleFallback(pen); break;
            }
        }
        IntPtr handle = bitmap.GetHicon();
        try { using var temp = Icon.FromHandle(handle); return (Icon)temp.Clone(); }
        finally { DestroyIcon(handle); }
    }

    protected override void ExitThreadCore()
    {
        if (_exiting) return;
        _exiting = true;
        _cancellation?.Cancel();
        _capture?.Cancel();
        _tick.Stop();
        _reset.Stop();
        _hotkeys.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        _icon?.Dispose();
        _cancellation?.Dispose();
        _tick.Dispose();
        _reset.Dispose();
        base.ExitThreadCore();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}

internal static class TrayIconDrawing
{
    public static void FillRoundedRectangleFallback(this Graphics graphics, Pen pen)
    {
        graphics.FillRoundedRectangleFallbackImpl(pen);
    }

    private static void FillRoundedRectangleFallbackImpl(this Graphics graphics, Pen pen)
    {
        graphics.DrawLine(pen, 16, 8, 16, 19);
        graphics.DrawArc(pen, 11, 15, 10, 8, 0, 180);
        graphics.DrawLine(pen, 12, 25, 20, 25);
    }
}
