namespace MeetingFlow.App.Models;

public sealed class AppSettings
{
    public string ProtectedApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gemini-3.5-flash";
    public string Language { get; set; } = "ko-KR";
    public string LanguageMode { get; set; } = "fixed";
    public string AllowedLanguages { get; set; } = "ko,en";
    public string ReportLanguage { get; set; } = "same";
    public string ContentProfile { get; set; } = TranscriptionProfileCatalog.DefaultId;
    public string WhisperModel { get; set; } = "medium";
    public string SttEngine { get; set; } = "python-faster-whisper";
    public bool EnableLiveDraft { get; set; } = true;
    public string LiveDraftModel { get; set; } = "base";
    public string SttQualityProfile { get; set; } = SttQualityPresetCatalog.DefaultId;
    public bool SttQualityConfigured { get; set; }
    public int SttBeamSize { get; set; } = 8;
    public string SttVocabulary { get; set; } = "BMS, FPCB, FP-129, 밸런스 케이블, 커넥터, 커버레이, 에칭, 도체, 피치, 리벳";
    public bool UseCustomVocabulary { get; set; }
    public bool EnableHallucinationGuard { get; set; } = true;
    public bool ShowAdvancedOptions { get; set; } = true;
    public bool AutoSelectAvailableGeminiModel { get; set; } = true;
    public bool AutoRetryPendingAi { get; set; } = true;
    public bool ShowTimelineEditor { get; set; } = true;
    public int GeminiConnectionTimeoutSeconds { get; set; } = 20;
    public double Temperature { get; set; } = 0.2;
    public bool AutoSummarize { get; set; } = true;
    public bool RequireTranscriptReviewBeforeAi { get; set; } = true;
    public string SpeakerDiarizationMode { get; set; } = "off";
    public int SpeakerCount { get; set; } = 2;
    public string ProtectedHuggingFaceToken { get; set; } = string.Empty;
    public string AiOrganizationMode { get; set; } = "표준 회의록";
    public string DefaultReportTemplateId { get; set; } = ReportTemplateCatalog.DefaultId;
    public int AiRangeStartMinute { get; set; }
    public int AiRangeEndMinute { get; set; }
    public string SummaryPrompt { get; set; } = "핵심 안건, 결정사항, 실행 항목, 미해결 질문을 근거 중심으로 정리하세요. 모르는 담당자나 기한은 추측하지 말고 '미정'으로 표시하세요.";
}
