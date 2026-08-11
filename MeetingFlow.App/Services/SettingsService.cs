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
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? new AppSettings();
            Normalize(settings);
            Save(settings);
            return settings;
        }
        catch { return new AppSettings(); }
    }

    public void Save(AppSettings settings) => File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));

    private static void Normalize(AppSettings settings)
    {
        settings.ProtectedApiKey ??= string.Empty;
        settings.ProtectedOpenAiApiKey ??= string.Empty;
        settings.ProtectedAnthropicApiKey ??= string.Empty;
        settings.ProtectedCompatibleApiKey ??= string.Empty;
        settings.ProtectedHuggingFaceToken ??= string.Empty;
        settings.AiProvider = settings.AiProvider is "openai" or "anthropic" or "compatible" ? settings.AiProvider : "gemini";
        settings.CompatibleApiEndpoint = string.IsNullOrWhiteSpace(settings.CompatibleApiEndpoint) ? "http://localhost:11434/v1" : settings.CompatibleApiEndpoint;
        settings.Model = string.IsNullOrWhiteSpace(settings.Model) ? "gemini-3.5-flash" : settings.Model;
        settings.Language = string.IsNullOrWhiteSpace(settings.Language) ? "ko-KR" : settings.Language;
        settings.LanguageMode = settings.LanguageMode is "auto" or "mixed" ? settings.LanguageMode : "fixed";
        settings.AllowedLanguages = string.IsNullOrWhiteSpace(settings.AllowedLanguages) ? "ko,en" : settings.AllowedLanguages;
        settings.ReportLanguage = string.IsNullOrWhiteSpace(settings.ReportLanguage) ? "same" : settings.ReportLanguage;
        settings.ContentProfile = string.IsNullOrWhiteSpace(settings.ContentProfile) ? TranscriptionProfileCatalog.DefaultId : settings.ContentProfile;
        settings.WhisperModel = string.IsNullOrWhiteSpace(settings.WhisperModel) ? "medium" : settings.WhisperModel;
        settings.SttEngine = string.IsNullOrWhiteSpace(settings.SttEngine) ? "python-faster-whisper" : settings.SttEngine;
        settings.CrisperModel = string.IsNullOrWhiteSpace(settings.CrisperModel) ? "small" : settings.CrisperModel;
        settings.CrisperMode = string.IsNullOrWhiteSpace(settings.CrisperMode) ? "intended" : settings.CrisperMode;
        settings.CrisperChunkSeconds = settings.CrisperChunkSeconds is 15 or 30 or 60 or 120 ? settings.CrisperChunkSeconds : 30;
        settings.VadProfile = settings.VadProfile is "noisy" or "sensitive" ? settings.VadProfile : "balanced";
        settings.LiveDraftModel = string.IsNullOrWhiteSpace(settings.LiveDraftModel) ? "base" : settings.LiveDraftModel;
        settings.SttQualityProfile = SttQualityPresetCatalog.Get(settings.SttQualityProfile).Id;
        settings.SttBeamSize = Math.Clamp(settings.SttBeamSize <= 0 ? 8 : settings.SttBeamSize, 1, 12);
        settings.SttVocabulary ??= string.Empty;
        settings.SpeakerDiarizationMode = settings.SpeakerDiarizationMode is "auto" or "fixed" ? settings.SpeakerDiarizationMode : "off";
        settings.SpeakerCount = Math.Clamp(settings.SpeakerCount <= 0 ? 2 : settings.SpeakerCount, 1, 12);
        settings.AiOrganizationMode = string.IsNullOrWhiteSpace(settings.AiOrganizationMode) ? "표준 회의록" : settings.AiOrganizationMode;
        settings.DefaultReportTemplateId = string.IsNullOrWhiteSpace(settings.DefaultReportTemplateId) ? ReportTemplateCatalog.DefaultId : settings.DefaultReportTemplateId;
        settings.SummaryPrompt = string.IsNullOrWhiteSpace(settings.SummaryPrompt)
            ? "핵심 안건, 결정사항, 실행 항목, 미해결 질문을 근거 중심으로 정리하세요. 모르는 담당자나 기한은 추측하지 말고 '미정'으로 표시하세요."
            : settings.SummaryPrompt;
        settings.GeminiConnectionTimeoutSeconds = Math.Clamp(settings.GeminiConnectionTimeoutSeconds <= 0 ? 20 : settings.GeminiConnectionTimeoutSeconds, 5, 60);
    }

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
