namespace CodexVoiceDictate;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        System.Windows.Forms.ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplication());
    }
}
