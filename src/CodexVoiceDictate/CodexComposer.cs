using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace CodexVoiceDictate;

internal readonly record struct DeliveryResult(bool Succeeded, bool Verified, string Message);

/// <summary>
/// Pins an explicitly focused text target in any Windows application. UI Automation
/// is preferred; native keyboard-focus tracking provides a limited fallback for
/// custom controls such as Swing editors that do not expose a Windows text pattern.
/// No screen coordinates, clipboard reads, or blind auto-send are used.
/// </summary>
internal static class CodexComposer
{
    private static readonly string[] PositiveNames = ["composer", "prompt", "message", "ask codex", "ask anything", "chat input", "type a message"];
    private static readonly string[] NegativeNames = ["search", "find", "terminal", "browser address", "url", "filter", "file name"];
    private static AutomationElement? _boundElement;
    private static IntPtr _boundWindowHandle;
    private static IntPtr _boundFocusHandle;
    private static int _boundProcessId;
    private static string _boundProcessName = "";

    public static string BindCurrent(AppSettings settings)
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("No active application window. Focus a text field and press Ctrl+Alt+B.");
        GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == 0 || processId == (uint)Environment.ProcessId)
            throw new InvalidOperationException("Focus a text field in the destination application, then press Ctrl+Alt+B.");

        var window = AutomationElement.FromHandle(hwnd);
        var focused = Safe(() => AutomationElement.FocusedElement);
        AutomationElement? editor = focused != null && Safe(() => focused.Current.ProcessId) == (int)processId
            ? FindEditableAncestor(focused) ?? focused : null;
        if (editor != null && Safe(() => editor.Current.ControlType == ControlType.Window)) editor = null;
        if (editor != null && Safe(() => editor.Current.IsPassword))
            throw new InvalidOperationException("Password fields cannot be used as dictation targets.");
        if (editor != null)
        {
            ControlType? type = Safe(() => editor.Current.ControlType);
            if (type == ControlType.Button || type == ControlType.MenuItem || type == ControlType.CheckBox)
                throw new InvalidOperationException("The focused control is not a text field. Click inside an editor and bind again.");
        }

        IntPtr nativeFocus = GetNativeFocus(hwnd);
        if (editor == null && nativeFocus == IntPtr.Zero)
            throw new InvalidOperationException("No focused text field could be identified. Click inside it and press Ctrl+Alt+B.");

        string name = editor == null ? "" : Safe(() => editor.Current.Name);
        // Accessibility names may contain an editor's entire text; never save long contents.
        if (name.Length > 120) name = "";
        settings.ComposerAutomationId = editor == null ? "" : Safe(() => editor.Current.AutomationId);
        settings.ComposerName = name;
        settings.ComposerClassName = editor == null ? "" : Safe(() => editor.Current.ClassName);
        settings.TargetControlType = editor == null ? "" : Safe(() => editor.Current.ControlType.ProgrammaticName);
        var w = Safe(() => window.Current.BoundingRectangle);
        var r = editor == null ? default : Safe(() => editor.Current.BoundingRectangle);
        settings.ComposerRelativeX = w.Width > 0 && !r.IsEmpty ? (r.Left + r.Width / 2 - w.Left) / w.Width : 0.5;
        settings.ComposerRelativeY = w.Height > 0 && !r.IsEmpty ? (r.Top + r.Height / 2 - w.Top) / w.Height : 0.5;
        settings.TargetProcessName = Process.GetProcessById((int)processId).ProcessName;
        settings.TargetWindowTitle = Safe(() => window.Current.Name);
        settings.TargetWindowClassName = Safe(() => window.Current.ClassName);
        settings.ComposerBound = true;
        settings.Save();

        _boundElement = editor;
        _boundWindowHandle = hwnd;
        _boundFocusHandle = nativeFocus;
        _boundProcessId = (int)processId;
        _boundProcessName = settings.TargetProcessName;
        return $"Target bound: {settings.TargetProcessName}" +
               (editor == null || ReadText(editor) == null ? " (focus-only; auto-send requires text verification)" : "");
    }

    public static DeliveryResult Insert(string text, bool send)
    {
        if (string.IsNullOrWhiteSpace(text)) return new(false, false, "The transcript is empty.");
        var settings = AppSettings.Load();
        bool customTarget = settings.ComposerBound && !string.IsNullOrWhiteSpace(settings.TargetProcessName);
        var window = customTarget ? FindBoundWindow(settings) : FindCodexWindow();
        if (window == null)
            return new(false, false, customTarget
                ? "Bound application window not found. Open the target and press Ctrl+Alt+B to bind again."
                : "Codex desktop window not found. Focus any text field and press Ctrl+Alt+B to bind it.");
        var editor = customTarget ? FindBoundEditor(window, settings) : FindComposer(window, settings);
        if (editor == null && !customTarget)
            return new(false, false, "Cannot find the Codex composer. Focus it and press Ctrl+Alt+B to bind.");

        IntPtr handle = new(window.Current.NativeWindowHandle);
        if (handle == IntPtr.Zero) return new(false, false, "The target window has no native handle.");
        if (IsIconic(handle)) ShowWindow(handle, 9); // Restore a minimized app only when needed.
        SetForegroundWindow(handle);
        Thread.Sleep(100);
        if (GetForegroundWindow() != handle)
            return new(false, false, "Windows did not activate the target window; nothing was pasted.");

        if (editor != null)
        {
            try { editor.SetFocus(); }
            catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException or COMException)
            { /* Some custom editors expose focus but do not allow setting it. */ }
        }
        Thread.Sleep(100);

        var focused = Safe(() => AutomationElement.FocusedElement);
        bool uiaFocused = editor != null && focused != null && IsDescendantOrSelf(focused, editor);
        bool nativeFocused = customTarget && _boundProcessId == Safe(() => window.Current.ProcessId) &&
                             handle == _boundWindowHandle && _boundFocusHandle != IntPtr.Zero &&
                             GetNativeFocus(handle) == _boundFocusHandle;
        bool differentAccessibleField = nativeFocused && _boundElement != null && focused != null &&
                                        Safe(() => focused.Current.ProcessId) == _boundProcessId &&
                                        !IsDescendantOrSelf(focused, _boundElement) &&
                                        !IsDescendantOrSelf(_boundElement, focused);
        if (differentAccessibleField)
            return new(false, false, "A different field is focused in the bound app. Focus the original field or rebind.");
        if ((!uiaFocused && !nativeFocused) || GetForegroundWindow() != handle)
            return new(false, false, "Cannot confirm focus on the bound text field. Nothing was pasted; press Ctrl+Alt+B to rebind.");

        string? before = editor == null ? null : ReadText(editor);

        // Clipboard paste is supported by standard editors and many Swing-based editors.
        // We never read the clipboard or unrelated text from a third-party application.
        Clipboard.SetText(text);
        SendChord(0x11, 0x56); // Ctrl+V
        Thread.Sleep(350);

        string? after = editor == null ? null : ReadText(editor);
        bool verified = before != null && after != null &&
                        !string.Equals(before, after, StringComparison.Ordinal) &&
                        Normalize(after).Contains(Normalize(text), StringComparison.Ordinal);
        if (!verified)
            return new(true, false, send
                ? "Paste attempted, but text is not readable through accessibility. NOT auto-sent; check the field."
                : "Paste attempted in the bound field; accessibility cannot verify its contents. Check the text.");

        if (send)
        {
            // Codex may require Ctrl+Enter for multi-line prompts. Other editors
            // generally use Enter, so do not change their shortcut behavior.
            if (IsCodexWindow(window))
            {
                SendChord(0x11, 0x0D); // Ctrl+Enter
                return new(true, true, "Text verified; Ctrl+Enter sent to Codex.");
            }
            SendKey(0x0D);
            return new(true, true, "Text verified; Enter sent to the bound application.");
        }
        return new(true, true, "Text verified in the bound field.");
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n").Trim();

    private static AutomationElement? FindBoundWindow(AppSettings settings)
    {
        if (_boundWindowHandle != IntPtr.Zero && IsWindow(_boundWindowHandle))
        {
            GetWindowThreadProcessId(_boundWindowHandle, out uint livePid);
            if (livePid != 0 && livePid == (uint)_boundProcessId &&
                _boundProcessName.Equals(settings.TargetProcessName, StringComparison.OrdinalIgnoreCase))
            {
                var cachedWindow = Safe(() => AutomationElement.FromHandle(_boundWindowHandle));
                if (cachedWindow != null) return cachedWindow;
            }
        }
        var foreground = GetForegroundWindow();
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
        var candidates = new List<(AutomationElement Element, int Score)>();
        foreach (AutomationElement window in windows)
        {
            int pid = Safe(() => window.Current.ProcessId);
            if (pid <= 0) continue;
            string processName;
            try { processName = Process.GetProcessById(pid).ProcessName; }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException) { continue; }
            if (!processName.Equals(settings.TargetProcessName, StringComparison.OrdinalIgnoreCase)) continue;

            IntPtr hwnd = new(Safe(() => window.Current.NativeWindowHandle));
            int score = 0;
            if (hwnd != IntPtr.Zero && hwnd == _boundWindowHandle && pid == _boundProcessId) score += 250;
            if (!string.IsNullOrEmpty(settings.TargetWindowTitle) &&
                Safe(() => window.Current.Name) == settings.TargetWindowTitle) score += 100;
            if (!string.IsNullOrEmpty(settings.TargetWindowClassName) &&
                Safe(() => window.Current.ClassName) == settings.TargetWindowClassName) score += 25;
            if (hwnd == foreground) score += 40;
            candidates.Add((window, score));
        }
        var ranked = candidates.OrderByDescending(candidate => candidate.Score).ToList();
        if (ranked.Count == 1) return ranked[0].Element;
        if (ranked.Count == 0 || ranked[0].Score < 65 || ranked[0].Score - ranked[1].Score < 20)
            return null; // Never guess which IntelliJ project window owns a binding.
        return ranked[0].Element;
    }

    private static AutomationElement? FindBoundEditor(AutomationElement window, AppSettings settings)
    {
        int pid = Safe(() => window.Current.ProcessId);
        if (_boundElement != null && pid == _boundProcessId &&
            Safe(() => _boundElement.Current.ProcessId) == pid && IsDescendantOrSelf(_boundElement, window))
            return _boundElement;

        // Without a distinctive accessibility fingerprint, native-focus mode
        // requires the original process/window to remain alive and be re-bound.
        if (string.IsNullOrEmpty(settings.ComposerAutomationId) &&
            string.IsNullOrEmpty(settings.ComposerName) &&
            string.IsNullOrEmpty(settings.ComposerClassName)) return null;

        Condition condition = !string.IsNullOrEmpty(settings.ComposerAutomationId)
            ? new PropertyCondition(AutomationElement.AutomationIdProperty, settings.ComposerAutomationId)
            : new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane));
        AutomationElementCollection all;
        try { all = window.FindAll(TreeScope.Descendants, condition); }
        catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException or COMException)
        { return null; }

        var bounds = Safe(() => window.Current.BoundingRectangle);
        var options = new List<(AutomationElement Element, int Score)>();
        foreach (AutomationElement editor in all)
        {
            if (!Safe(() => editor.Current.IsEnabled) || Safe(() => editor.Current.IsPassword)) continue;
            string id = Safe(() => editor.Current.AutomationId);
            string name = Safe(() => editor.Current.Name);
            string className = Safe(() => editor.Current.ClassName);
            string type = Safe(() => editor.Current.ControlType.ProgrammaticName);
            int score = 0;
            if (settings.ComposerAutomationId.Length > 0 && id == settings.ComposerAutomationId) score += 100;
            if (settings.ComposerName.Length > 0 && name == settings.ComposerName) score += 45;
            if (settings.ComposerClassName.Length > 0 && className == settings.ComposerClassName) score += 15;
            if (settings.TargetControlType.Length > 0 && type == settings.TargetControlType) score += 10;
            var r = Safe(() => editor.Current.BoundingRectangle);
            if (!r.IsEmpty && !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
            {
                double x = (r.Left + r.Width / 2 - bounds.Left) / bounds.Width;
                double y = (r.Top + r.Height / 2 - bounds.Top) / bounds.Height;
                double distance = Math.Sqrt(Math.Pow(x - settings.ComposerRelativeX, 2) +
                                            Math.Pow(y - settings.ComposerRelativeY, 2));
                if (distance < 0.12) score += (int)(25 * (1 - distance / 0.12));
            }
            if (score >= 40) options.Add((editor, score));
        }
        var ranked = options.OrderByDescending(option => option.Score).ToList();
        if (ranked.Count == 0 || ranked[0].Score < 50) return null;
        if (ranked.Count > 1 && ranked[0].Score - ranked[1].Score < 10) return null;
        return ranked[0].Element;
    }

    private static IntPtr GetNativeFocus(IntPtr window)
    {
        uint thread = GetWindowThreadProcessId(window, out _);
        var info = new GUITHREADINFO { Size = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        if (thread == 0 || !GetGUIThreadInfo(thread, ref info)) return IntPtr.Zero;
        // Some Java windows expose only their active native canvas, not a child HWND.
        return info.FocusedWindow != IntPtr.Zero ? info.FocusedWindow : info.ActiveWindow;
    }

    private static AutomationElement? FindCodexWindow()
    {
        var foreground = GetForegroundWindow();
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
        var candidates = new List<AutomationElement>();
        foreach (AutomationElement window in windows)
            if (IsCodexWindow(window)) candidates.Add(window);
        return candidates.FirstOrDefault(w => new IntPtr(Safe(() => w.Current.NativeWindowHandle)) == foreground)
            ?? candidates.OrderByDescending(w => Safe(() => w.Current.BoundingRectangle.Width)).FirstOrDefault();
    }

    private static bool IsCodexWindow(AutomationElement window)
    {
        try
        {
            string processName = Process.GetProcessById(window.Current.ProcessId).ProcessName;
            bool knownProcess = processName.Contains("codex", StringComparison.OrdinalIgnoreCase)
                || processName.Contains("chatgpt", StringComparison.OrdinalIgnoreCase);
            string title = window.Current.Name;
            return knownProcess && (title.Contains("Codex", StringComparison.OrdinalIgnoreCase)
                || title.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase)
                || processName.Contains("Codex", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or ElementNotAvailableException) { return false; }
    }

    private static AutomationElement? FindComposer(AutomationElement window, AppSettings settings)
    {
        var condition = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
        var all = window.FindAll(TreeScope.Descendants, condition);
        var options = new List<(AutomationElement Element, int Score)>();
        var bounds = window.Current.BoundingRectangle;
        foreach (AutomationElement element in all)
        {
            if (!Safe(() => element.Current.IsEnabled) || !Safe(() => element.Current.IsKeyboardFocusable)) continue;
            var rect = Safe(() => element.Current.BoundingRectangle);
            if (rect.IsEmpty || rect.Width < 180 || rect.Height < 18) continue;
            string id = Safe(() => element.Current.AutomationId);
            string name = Safe(() => element.Current.Name);
            string className = Safe(() => element.Current.ClassName);
            string combined = id + " " + name;
            if (NegativeNames.Any(part => combined.Contains(part, StringComparison.OrdinalIgnoreCase))) continue;

            int score = 0;
            foreach (string part in PositiveNames)
                if (combined.Contains(part, StringComparison.OrdinalIgnoreCase)) score += 20;
            if (!string.IsNullOrWhiteSpace(settings.ComposerAutomationId) && id == settings.ComposerAutomationId) score += 100;
            if (!string.IsNullOrWhiteSpace(settings.ComposerName) && name == settings.ComposerName) score += 30;
            if (!string.IsNullOrWhiteSpace(settings.ComposerClassName) && className == settings.ComposerClassName) score += 5;
            if (settings.ComposerBound && !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
            {
                double x = (rect.Left + rect.Width / 2 - bounds.Left) / bounds.Width;
                double y = (rect.Top + rect.Height / 2 - bounds.Top) / bounds.Height;
                double distance = Math.Sqrt(Math.Pow(x - settings.ComposerRelativeX, 2) + Math.Pow(y - settings.ComposerRelativeY, 2));
                if (distance < 0.18) score += (int)(40 * (1 - distance / 0.18));
            }
            if (!bounds.IsEmpty && rect.Bottom > bounds.Top + bounds.Height * 0.60) score += 12;
            if (rect.Width > 250) score += 5;
            options.Add((element, score));
        }
        var ranked = options.OrderByDescending(option => option.Score).ToList();
        if (ranked.Count == 0 || ranked[0].Score < 25) return null;
        // An ambiguous tie means that an automatic paste could go to the wrong field.
        if (ranked.Count > 1 && ranked[0].Score == ranked[1].Score) return null;
        return ranked[0].Element;
    }

    private static AutomationElement? FindEditableAncestor(AutomationElement element)
    {
        try
        {
            for (var current = element; current != null; current = TreeWalker.ControlViewWalker.GetParent(current))
            {
                ControlType? type = Safe(() => current.Current.ControlType);
                if (type == ControlType.Edit || type == ControlType.Document) return current;
                if (type == ControlType.Window) break;
            }
        }
        catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException or COMException)
        { /* Native focus fallback can still work if Java's UIA tree is incomplete. */ }
        return null;
    }

    private static bool IsDescendantOrSelf(AutomationElement candidate, AutomationElement ancestor)
    {
        try
        {
            for (var current = candidate; current != null; current = TreeWalker.RawViewWalker.GetParent(current))
                if (current.Equals(ancestor)) return true;
        }
        catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException or COMException)
        { /* Accessibility element disappeared during traversal. */ }
        return false;
    }

    private static string? ReadText(AutomationElement editor)
    {
        try
        {
            if (editor.TryGetCurrentPattern(ValuePattern.Pattern, out object value))
                return ((ValuePattern)value).Current.Value;
            if (editor.TryGetCurrentPattern(TextPattern.Pattern, out object pattern))
                return ((TextPattern)pattern).DocumentRange.GetText(-1);
        }
        catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException or COMException)
        { /* Custom editors may not expose readable patterns. */ }
        return null;
    }

    private static T Safe<T>(Func<T> get)
    {
        try { return get(); }
        catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException or COMException)
        { return default!; }
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYUP = 0x0002;
    private static void SendChord(ushort modifier, ushort key)
    {
        SendInputs([Keyboard(modifier), Keyboard(key), Keyboard(key, true), Keyboard(modifier, true)]);
    }

    private static void SendKey(ushort key) => SendInputs([Keyboard(key), Keyboard(key, true)]);
    private static INPUT Keyboard(ushort key, bool up = false) => new()
    {
        Type = INPUT_KEYBOARD,
        Data = new InputUnion { Keyboard = new KEYBDINPUT { VirtualKey = key, Flags = up ? KEYUP : 0 } }
    };

    private static void SendInputs(INPUT[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            throw new InvalidOperationException("Windows rejected the simulated keyboard input.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public HARDWAREINPUT Hardware;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public IntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT { public uint Message; public ushort LowParam, HighParam; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public uint Size, Flags;
        public IntPtr ActiveWindow, FocusedWindow, CaptureWindow, MenuOwnerWindow, MoveSizeWindow, CaretWindow;
        public RECT CaretRectangle;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
