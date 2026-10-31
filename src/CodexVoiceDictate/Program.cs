namespace CodexVoiceDictate;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Bootstrap high-DPI awareness before creating any WinForms controls.
        ApplicationConfiguration.Initialize();
        Application.SetDefaultFont(SystemFonts.MessageBoxFont);
        Application.Run(new TrayApplication());
    }
}
