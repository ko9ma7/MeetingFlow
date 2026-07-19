using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeetingFlow.App.Models;

namespace MeetingFlow.App.Services;

public sealed class SettingsService
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public SettingsService(string? root = null)
    {
        root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetingFlow");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(_path)) return new AppSettings();
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? new AppSettings(); }
        catch { return new AppSettings(); }
    }

    public void Save(AppSettings settings) => File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));

    public static string ProtectApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return string.Empty;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(apiKey.Trim()), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string UnprotectApiKey(string protectedKey)
    {
        if (string.IsNullOrWhiteSpace(protectedKey)) return string.Empty;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedKey), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return string.Empty; }
    }
}
