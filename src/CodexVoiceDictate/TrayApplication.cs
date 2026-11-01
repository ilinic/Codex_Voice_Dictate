using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CodexVoiceDictate;

internal enum Stage { Ready, Recording, Transcribing, Correcting, Inserting, Done, Unverified, Error, Cancelled }

/// <summary>No main window: the tray icon is the primary UI and communicates stage through a glyph and color.</summary>
internal sealed class TrayApplication : ApplicationContext
{
    private static readonly string AppVersion = typeof(TrayApplication).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _stageItem;
    private readonly GlobalHotkeys _hotkeys;
    private readonly System.Windows.Forms.Timer _tick;
    private readonly System.Windows.Forms.Timer _reset;
    private readonly OpenAiSpeechClient _client = new();
    private AppSettings _settings;
    private WaveCapture? _capture;
    private DirectPasteTarget? _directPasteTarget;
    private AutoBoundTarget? _autoBoundTarget;
    private CancellationTokenSource? _cancellation;
    private string _lastTranscript = "";
    private string _stageDetails = "Ready";
    private Stage _stage = Stage.Ready;
    private Icon? _icon;
    private bool _exiting;

    public TrayApplication()
    {
        _settings = AppSettings.Load();
        _menu = new ContextMenuStrip { Font = SystemFonts.MenuFont, AutoClose = true, ShowItemToolTips = true };
        _stageItem = new ToolStripMenuItem("Ready") { Enabled = false };
        _menu.Items.AddRange([
            _stageItem, new ToolStripSeparator(),
            new ToolStripMenuItem("Settings...", null, (_, _) => ShowSettings()),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitThread())
        ]);
        // Show the tray menu ourselves so its owner can be foregrounded first.
        // Otherwise Windows can leave a notification-area menu open after a click outside.
        _tray = new NotifyIcon { Text = "Codex Voice Dictate — Ready", Visible = true };
        _tray.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ShowTrayMenu(); };
        _tray.DoubleClick += (_, _) => ShowSettings();
        _tick = new System.Windows.Forms.Timer { Interval = 250 };
        _tick.Tick += (_, _) => { if (_stage == Stage.Recording && _capture?.LimitReached == true) Finish(false); };
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

    private void ShowTrayMenu()
    {
        // The hidden native hotkey window is a stable top-level menu owner.
        // Follow the Win32 tray-menu foreground + WM_NULL sequence so a click
        // outside the popup dismisses it and reopening it works reliably.
        SetForegroundWindow(_hotkeys.Handle);
        _menu.Show(Cursor.Position);
        PostMessage(_hotkeys.Handle, 0, IntPtr.Zero, IntPtr.Zero); // WM_NULL
    }

    private void HandleHotkey(int id)
    {
        switch (id)
        {
            case 1: ToggleRecording(); break;
            case 2: Finish(true); break;
            case 3: Cancel(); break;
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
            _directPasteTarget = null;
            _autoBoundTarget = null;
            if (_settings.PasteToFocusedField)
                _directPasteTarget = CodexComposer.CaptureDirectTarget();
            else
                _autoBoundTarget = CodexComposer.CaptureAutoBoundTarget();
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
            _directPasteTarget = null;
            _autoBoundTarget = null;
            SetError(error is InvalidOperationException ? error.Message : "Could not start microphone: " + error.Message);
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
            byte[] opus = await capture.StopAsync();
            if (cancellation.IsCancellationRequested) return;
            if (capture.Duration.TotalMilliseconds < 250 || opus.Length < 256)
                throw new InvalidOperationException("Recording was too short.");
            if (opus.Length > WaveCapture.MaxEncodedBytes)
                throw new InvalidOperationException("Compressed recording is too large for the transcription API.");
            capture.Dispose();
            _capture = null;
            SetStage(Stage.Transcribing, "Transcribing speech...");
            string key = _settings.GetApiKey();
            string result = await _client.TranscribeAsync(opus, key, _settings, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            _lastTranscript = result; // Keep the raw result even if optional correction fails.

            if (_settings.CorrectText)
            {
                SetStage(Stage.Correcting, "Correcting technical terms...");
                try { result = await _client.CorrectAsync(result, key, _settings, cancellation.Token); }
                catch (Exception error) when (error is IOException or HttpRequestException)
                { /* Preserve the raw recognition result if optional correction fails. */ }
            }
            if (cancellation.IsCancellationRequested) return;
            _lastTranscript = result;
            SetStage(Stage.Inserting, _settings.PasteToFocusedField
                ? "Pasting into the original focused field..." : "Restoring editor captured at recording start...");
            DeliveryResult delivery;
            if (_settings.PasteToFocusedField)
                delivery = _directPasteTarget is { } target
                    ? CodexComposer.InsertDirect(result, send, target)
                    : new DeliveryResult(false, false, "No focused field was captured when recording started.");
            else
                delivery = _autoBoundTarget is { } bound
                    ? CodexComposer.InsertAutoBound(result, send, bound)
                    : new DeliveryResult(false, false, "No editor was captured when recording started.");
            if (delivery.Succeeded)
            {
                SetStage(delivery.Verified ? Stage.Done : Stage.Unverified, delivery.Message);
                if (_settings.ShowNotifications)
                    _tray.ShowBalloonTip(2500, "Codex Voice Dictate", delivery.Message,
                        delivery.Verified ? ToolTipIcon.Info : ToolTipIcon.Warning);
            }
            else SetErrorWithClipboardFallback(delivery.Message);
        }
        catch (OperationCanceledException) { /* Escape cancellation. */ }
        catch (Exception error)
        {
            if (!cancellation.IsCancellationRequested)
            {
                if (string.IsNullOrWhiteSpace(_lastTranscript)) SetError(error.Message);
                else SetErrorWithClipboardFallback(error.Message);
            }
        }
        finally
        {
            _directPasteTarget = null;
            _autoBoundTarget = null;
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
        _directPasteTarget = null;
        _autoBoundTarget = null;
        _hotkeys.EnableCancel(false);
        SetStage(Stage.Cancelled, "Cancelled");
    }

    private static async Task CleanupCancelledCapture(WaveCapture capture)
    {
        try { await capture.StopAsync(); }
        catch { /* Cancellation discards audio and any capture errors. */ }
        finally { capture.Dispose(); }
    }

    private void SetErrorWithClipboardFallback(string message)
    {
        SetError(message + (TryCopyTranscriptToClipboard()
            ? " Transcript copied to clipboard; paste with Ctrl+V."
            : " Could not copy automatically; use Copy last transcript in the tray."));
    }

    private bool TryCopyTranscriptToClipboard()
    {
        if (string.IsNullOrWhiteSpace(_lastTranscript)) return false;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { Clipboard.SetText(_lastTranscript); return true; }
            catch (ExternalException) { if (attempt < 2) Thread.Sleep(50); }
            catch (ThreadStateException) { return false; }
        }
        return false;
    }

    private void ShowSettings()
    {
        if (IsBusy) return;
        _hotkeys.UnregisterAll();
        var window = new SettingsWindow(_settings);
        window.ShowDialog();
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
        _stageItem.Text = $"{stage} · v{AppVersion}";
        _stageItem.ToolTipText = details;
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
                Stage.Unverified => Color.FromArgb(228, 158, 39),
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
                case Stage.Unverified:
                    graphics.DrawArc(pen, 12, 8, 8, 9, 180, 230);
                    graphics.FillEllipse(Brushes.White, 14.5f, 22, 3, 3); break;
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
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
