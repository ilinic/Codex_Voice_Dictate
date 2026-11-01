using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CodexVoiceDictate;

[Flags]
internal enum HotkeyModifiers : uint
{
    None = 0, Alt = 0x0001, Control = 0x0002, Shift = 0x0004, Win = 0x0008
}

internal readonly record struct Shortcut(Keys Key, HotkeyModifiers Modifiers)
{
    public bool IsValid => Key is >= Keys.F1 and <= Keys.F12 || Modifiers != HotkeyModifiers.None &&
        (Key is >= Keys.A and <= Keys.Z || Key is >= Keys.D0 and <= Keys.D9 || Key is >= Keys.F1 and <= Keys.F12);

    public override string ToString()
    {
        var bits = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) bits.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) bits.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) bits.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) bits.Add("Win");
        bits.Add(Key.ToString());
        return string.Join("+", bits);
    }

    public static bool TryParse(string text, out Shortcut result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string[] tokens = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var modifiers = HotkeyModifiers.None;
        Keys key = Keys.None;
        foreach (string token in tokens)
        {
            switch (token.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= HotkeyModifiers.Control; break;
                case "alt": modifiers |= HotkeyModifiers.Alt; break;
                case "shift": modifiers |= HotkeyModifiers.Shift; break;
                case "win": modifiers |= HotkeyModifiers.Win; break;
                default:
                    if (key != Keys.None || !Enum.TryParse(token, true, out key)) return false;
                    break;
            }
        }
        result = new Shortcut(key, modifiers);
        return result.IsValid;
    }
}

internal sealed class ShortcutCaptureBox : TextBox
{
    public ShortcutCaptureBox(string initial)
    {
        Text = initial;
        ReadOnly = true;
        TabStop = true;
        ShortcutsEnabled = false;
        Cursor = Cursors.Hand;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.Handled = e.SuppressKeyPress = true;
        if (e.KeyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return;
        var modifiers = HotkeyModifiers.None;
        if (e.Control) modifiers |= HotkeyModifiers.Control;
        if (e.Alt) modifiers |= HotkeyModifiers.Alt;
        if (e.Shift) modifiers |= HotkeyModifiers.Shift;
        var shortcut = new Shortcut(e.KeyCode, modifiers);
        if (shortcut.IsValid) Text = shortcut.ToString();
    }
}

/// <summary>Hidden native window receives system-wide hotkeys without polling keyboard state.</summary>
internal sealed class GlobalHotkeys : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private readonly Action<int> _callback;
    private readonly HashSet<int> _registered = [];
    public bool BindHotkeyAvailable => _registered.Contains(4);

    public GlobalHotkeys(Action<int> callback)
    {
        _callback = callback;
        CreateHandle(new CreateParams { Caption = "Codex Voice Dictate hotkeys" });
    }

    public void RegisterMain(Shortcut insert, Shortcut send, bool registerBinding)
    {
        UnregisterAll();
        try
        {
            Register(1, insert);
            Register(2, send);
            if (registerBinding)
            {
                try { Register(4, new Shortcut(Keys.B, HotkeyModifiers.Control | HotkeyModifiers.Alt)); }
                catch (Win32Exception) { /* Optional binding shortcut must not break dictation hotkeys. */ }
            }
        }
        catch { UnregisterAll(); throw; }
    }

    public void EnableCancel(bool enable)
    {
        if (enable && !_registered.Contains(3)) Register(3, new Shortcut(Keys.Escape, HotkeyModifiers.None), true);
        else if (!enable && _registered.Remove(3)) UnregisterHotKey(Handle, 3);
    }

    private void Register(int id, Shortcut shortcut, bool allowEscape = false)
    {
        if (!shortcut.IsValid && !(allowEscape && shortcut.Key == Keys.Escape)) throw new ArgumentException("Invalid shortcut.");
        if (!RegisterHotKey(Handle, id, (uint)shortcut.Modifiers | MOD_NOREPEAT, (uint)shortcut.Key))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot register {shortcut}; another application may be using it.");
        _registered.Add(id);
    }

    public void UnregisterAll()
    {
        foreach (int id in _registered) UnregisterHotKey(Handle, id);
        _registered.Clear();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WM_HOTKEY) _callback(message.WParam.ToInt32());
        base.WndProc(ref message);
    }

    public void Dispose() { UnregisterAll(); DestroyHandle(); }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
