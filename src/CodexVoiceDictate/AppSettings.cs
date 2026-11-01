using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexVoiceDictate;

internal sealed class AppSettings
{
    public string ProtectedApiKey { get; set; } = "";
    public string RecognitionLanguages { get; set; } = "ru,en";
    public string CorrectionModel { get; set; } = "gpt-6-luna";
    public bool CorrectText { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public string InsertShortcut { get; set; } = "F8";
    public string SendShortcut { get; set; } = "F9";
    public bool PasteToFocusedField { get; set; }
    public string ComposerAutomationId { get; set; } = "";
    public string ComposerName { get; set; } = "";
    public string ComposerClassName { get; set; } = "";
    public bool ComposerBound { get; set; }
    public double ComposerRelativeX { get; set; }
    public double ComposerRelativeY { get; set; }
    // Bound target identity. Composer* properties remain for compatibility with older settings.json files.
    public string TargetProcessName { get; set; } = "";
    public string TargetWindowTitle { get; set; } = "";
    public string TargetWindowClassName { get; set; } = "";
    public string TargetControlType { get; set; } = "";

    public static readonly (string Code, string English, string Russian)[] Languages =
    [
        ("ru", "Russian", "Русский"), ("en", "English", "Английский"),
        ("de", "German", "Немецкий"), ("fr", "French", "Французский"),
        ("es", "Spanish", "Испанский"), ("it", "Italian", "Итальянский"),
        ("pt", "Portuguese", "Португальский"), ("uk", "Ukrainian", "Украинский"),
        ("pl", "Polish", "Польский"), ("zh", "Chinese", "Китайский"),
        ("ja", "Japanese", "Японский"), ("ko", "Korean", "Корейский"),
        ("hi", "Hindi", "Хинди"), ("ar", "Arabic", "Арабский")
    ];

    public static readonly (string Id, string Label, string Effort)[] CorrectionModels =
    [
        ("gpt-6-luna", "GPT-6 Luna — economical", "none"),
        ("gpt-6.1-terra", "GPT-6.1 Terra — balanced", "none"),
        ("gpt-6.1-sol", "GPT-6.1 Sol — advanced", "low"),
        ("gpt-6-astra", "GPT-6 Astra — highest quality", "low")
    ];

    public string[] SelectedLanguages => RecognitionLanguages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public string GetApiKey()
    {
        if (!string.IsNullOrWhiteSpace(ProtectedApiKey))
        {
            try
            {
                byte[] bytes = ProtectedData.Unprotect(Convert.FromBase64String(ProtectedApiKey), null, DataProtectionScope.CurrentUser);
                try { return Encoding.UTF8.GetString(bytes); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            catch (Exception error) when (error is CryptographicException or FormatException)
            {
                // Never log or display encrypted key material.
                return "";
            }
        }
        return Environment.GetEnvironmentVariable("OPENAI_API_KEY")?.Trim() ?? "";
    }

    public void SetApiKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) { ProtectedApiKey = ""; return; }
        byte[] bytes = Encoding.UTF8.GetBytes(key.Trim());
        try { ProtectedApiKey = Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexVoiceDictate");
    private static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        string temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, true);
    }
}
