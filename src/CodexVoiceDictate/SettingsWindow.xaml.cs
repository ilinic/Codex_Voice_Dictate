using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FormsKeys = System.Windows.Forms.Keys;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace CodexVoiceDictate;

/// <summary>
/// A DPI-independent WPF dialog hosted on the existing WinForms tray UI thread.
/// All layout sizes are WPF device-independent pixels (DIPs), not screen pixels.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly List<(string Code, WpfCheckBox CheckBox)> _languageChecks = [];

    internal SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        FontFamily = System.Windows.SystemFonts.MessageFontFamily;
        FontSize = Math.Max(14, System.Windows.SystemFonts.MessageFontSize);

        ApiKeyBox.Password = settings.GetApiKey();
        PasteToFocusedFieldBox.IsChecked = settings.PasteToFocusedField;
        var selected = settings.SelectedLanguages.ToHashSet();
        foreach (var language in AppSettings.Languages)
        {
            var box = new WpfCheckBox
            {
                Content = $"{language.English} ({language.Code})",
                IsChecked = selected.Contains(language.Code),
                MinWidth = 170,
                Margin = new Thickness(0, 0, 14, 11)
            };
            LanguagesPanel.Children.Add(box);
            _languageChecks.Add((language.Code, box));
        }

        RecognitionContextBox.Text = settings.RecognitionContext;

        CorrectionEnabled.IsChecked = settings.CorrectText;
        foreach (var model in AppSettings.CorrectionModels)
            CorrectionModelBox.Items.Add(model.Label);
        int modelIndex = Array.FindIndex(AppSettings.CorrectionModels, m => m.Id == settings.CorrectionModel);
        CorrectionModelBox.SelectedIndex = modelIndex < 0 ? 0 : modelIndex;

        InsertShortcutBox.Text = settings.InsertShortcut;
        SendShortcutBox.Text = settings.SendShortcut;
        NotificationsEnabled.IsChecked = settings.ShowNotifications;

        // The available working area differs between 4K displays and small laptops.
        // Constrain the window at creation, while preserving WPF's DPI-independent layout.
        SourceInitialized += (_, _) => FitToWorkingArea();
    }

    private void FitToWorkingArea()
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null) return;

        var handle = new WindowInteropHelper(this).Handle;
        var workArea = Screen.FromHandle(handle).WorkingArea;
        Matrix transform = source.CompositionTarget.TransformFromDevice;
        double screenWidth = workArea.Width * transform.M11;
        double screenHeight = workArea.Height * transform.M22;
        double maxWidth = Math.Max(360, screenWidth - 48);
        double maxHeight = Math.Max(360, screenHeight - 48);

        MinWidth = Math.Min(MinWidth, maxWidth);
        MinHeight = Math.Min(MinHeight, maxHeight);
        Width = Math.Min(820, maxWidth);
        Height = Math.Min(760, maxHeight);
        Left = workArea.Left * transform.M11 + (screenWidth - Width) / 2;
        Top = workArea.Top * transform.M22 + (screenHeight - Height) / 2;
    }

    private void OnShortcutKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Read-only text boxes capture combinations without inserting characters.
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        // Preserve keyboard navigation and the standard Save/Cancel dialog keys.
        if (key is Key.Tab or Key.Escape or Key.Return) return;
        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
                   Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;

        FormsKeys virtualKey = (FormsKeys)KeyInterop.VirtualKeyFromKey(key);
        HotkeyModifiers modifiers = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= HotkeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= HotkeyModifiers.Shift;

        var shortcut = new Shortcut(virtualKey, modifiers);
        if (shortcut.IsValid && sender is WpfTextBox box)
            box.Text = shortcut.ToString();
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        if (!Shortcut.TryParse(InsertShortcutBox.Text, out var insert) ||
            !Shortcut.TryParse(SendShortcutBox.Text, out var send) || insert == send)
        {
            System.Windows.MessageBox.Show(this,
                "Choose two distinct shortcuts. F1–F12 are allowed alone; letters require Ctrl or Alt.",
                "Invalid hotkeys", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.SetApiKey(ApiKeyBox.Password);
        _settings.RecognitionLanguages = string.Join(",", _languageChecks
            .Where(item => item.CheckBox.IsChecked == true).Select(item => item.Code));
        _settings.RecognitionContext = RecognitionContextBox.Text.Trim();
        _settings.CorrectText = CorrectionEnabled.IsChecked == true;
        _settings.CorrectionModel = AppSettings.CorrectionModels[CorrectionModelBox.SelectedIndex].Id;
        _settings.InsertShortcut = insert.ToString();
        _settings.SendShortcut = send.ToString();
        _settings.PasteToFocusedField = PasteToFocusedFieldBox.IsChecked == true;
        _settings.ShowNotifications = NotificationsEnabled.IsChecked == true;

        try { _settings.Save(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            System.Windows.MessageBox.Show(this, error.Message, "Cannot save settings",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        DialogResult = true;
    }
}
