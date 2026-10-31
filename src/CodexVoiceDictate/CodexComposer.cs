using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace CodexVoiceDictate;

internal readonly record struct DeliveryResult(bool Verified, string Message);

/// <summary>
/// Targets the Codex desktop composer through Windows UI Automation. Never relies on screen coordinates.
/// A dedicated Bind action lets the user disambiguate rich-text inputs on their actual Codex build.
/// </summary>
internal static class CodexComposer
{
    private static readonly string[] PositiveNames = ["composer", "prompt", "message", "ask codex", "ask anything", "chat input", "type a message"];
    private static readonly string[] NegativeNames = ["search", "find", "terminal", "browser address", "url", "filter", "file name"];

    public static string BindCurrent(AppSettings settings)
    {
        var focused = AutomationElement.FocusedElement ?? throw new InvalidOperationException("No focused element was found.");
        var window = FindAncestorWindow(focused);
        if (window == null || !IsCodexWindow(window))
            throw new InvalidOperationException("Open Codex and click inside its message composer before choosing Bind Composer.");
        var editor = FindEditableAncestor(focused);
        if (editor == null) throw new InvalidOperationException("The focused Codex control is not an editable message field.");
        string name = Safe(() => editor.Current.Name);
        if (NegativeNames.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The focused element looks like a search or terminal field, not a message composer.");
        settings.ComposerAutomationId = Safe(() => editor.Current.AutomationId);
        settings.ComposerName = name;
        settings.ComposerClassName = Safe(() => editor.Current.ClassName);
        var w = window.Current.BoundingRectangle;
        var r = editor.Current.BoundingRectangle;
        settings.ComposerRelativeX = w.Width > 0 ? (r.Left + r.Width / 2 - w.Left) / w.Width : 0.5;
        settings.ComposerRelativeY = w.Height > 0 ? (r.Top + r.Height / 2 - w.Top) / w.Height : 0.8;
        settings.ComposerBound = true;
        settings.Save();
        return $"Composer bound: {(string.IsNullOrWhiteSpace(name) ? "editable field" : name)}";
    }

    public static DeliveryResult Insert(string text, bool send)
    {
        if (string.IsNullOrWhiteSpace(text)) return new(false, "The transcript is empty.");
        var settings = AppSettings.Load();
        var window = FindCodexWindow() ?? throw new InvalidOperationException("Codex desktop window not found. The transcript is still available in the tray menu.");
        var editor = FindComposer(window, settings) ?? throw new InvalidOperationException(
            "Could not confidently identify the Codex composer. Focus it once and use Bind Composer from the tray menu.");

        string? before = ReadText(editor);
        IntPtr handle = new(window.Current.NativeWindowHandle);
        if (handle == IntPtr.Zero) throw new InvalidOperationException("The Codex window has no native handle.");
        if (IsIconic(handle)) ShowWindow(handle, 9); // Restore a minimized app only when needed.
        SetForegroundWindow(handle);
        editor.SetFocus();
        Thread.Sleep(90);

        var focused = AutomationElement.FocusedElement;
        if (focused == null || !IsDescendantOrSelf(focused, editor))
            return new(false, "Cannot focus the Codex composer; text was not inserted.");

        // Clipboard fallback is used because most Chromium rich-text editors do not support ValuePattern.SetValue.
        // The transcript remains on the clipboard even if verification fails, so the user can retry manually.
        Clipboard.SetText(text);
        SendChord(0x11, 0x56); // Ctrl+V
        Thread.Sleep(350);

        string? after = ReadText(editor);
        bool verified = before != null && after != null &&
                        !string.Equals(before, after, StringComparison.Ordinal) &&
                        Normalize(after).Contains(Normalize(text), StringComparison.Ordinal);
        if (!verified)
            return new(false, "Paste could not be verified. The transcript is on the clipboard; it was NOT auto-sent.");

        if (send)
        {
            // Default Codex setting sends on Enter. Users with Ctrl+Enter configured should use Insert mode.
            SendKey(0x0D);
            return new(true, "Text inserted and Enter sent to Codex.");
        }
        return new(true, "Text verified in the Codex composer.");
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n").Trim();

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

    private static AutomationElement? FindAncestorWindow(AutomationElement element)
    {
        for (var current = element; current != null; current = TreeWalker.ControlViewWalker.GetParent(current))
            if (Safe(() => current.Current.ControlType == ControlType.Window)) return current;
        return null;
    }

    private static AutomationElement? FindEditableAncestor(AutomationElement element)
    {
        for (var current = element; current != null; current = TreeWalker.ControlViewWalker.GetParent(current))
        {
            ControlType? type = Safe(() => current.Current.ControlType);
            if (type == ControlType.Edit || type == ControlType.Document) return current;
            if (type == ControlType.Window) break;
        }
        return null;
    }

    private static bool IsDescendantOrSelf(AutomationElement candidate, AutomationElement ancestor)
    {
        for (var current = candidate; current != null; current = TreeWalker.ControlViewWalker.GetParent(current))
            if (current.Equals(ancestor)) return true;
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
        catch (ElementNotAvailableException) { }
        return null;
    }

    private static T Safe<T>(Func<T> get)
    {
        try { return get(); }
        catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException)
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
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
