namespace MeetingFlow.App.Models;

public sealed class MeetingRecord
{
    public int DataVersion { get; set; } = 8;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string MeetingType { get; set; } = "일반 업무 회의";
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public TimeSpan Duration { get; set; }
    public string AudioPath { get; set; } = string.Empty;
    public string Transcript { get; set; } = string.Empty;
    public string RawTranscript { get; set; } = string.Empty;
    public string LiveDraftTranscript { get; set; } = string.Empty;
    public DateTime? LiveDraftUpdatedAt { get; set; }
    public bool TranscriptReviewed { get; set; }
    public DateTime? TranscriptReviewedAt { get; set; }
    public string AiNotesText { get; set; } = string.Empty;
    public List<TranscriptSegment> TranscriptSegments { get; set; } = [];
    public string SecondaryTranscript { get; set; } = string.Empty;
    public List<TranscriptSegment> SecondaryTranscriptSegments { get; set; } = [];
    public string SttComparisonSummary { get; set; } = string.Empty;
    public int SttDisagreementCount { get; set; }
    public double SttProcessingSeconds { get; set; }
    public double SttRealtimeFactor { get; set; }
    public string SttWarnings { get; set; } = string.Empty;
    public string SttEngine { get; set; } = "Whisper.net / whisper.cpp";
    public string SttModel { get; set; } = string.Empty;
    public string SttQualityProfile { get; set; } = SttQualityPresetCatalog.DefaultId;
    public string LiveDraftModel { get; set; } = string.Empty;
    public string SpeakerDiarizationMode { get; set; } = "off";
    public int ExpectedSpeakerCount { get; set; }
    public int DetectedSpeakerCount { get; set; }
    public string DiarizationStatus { get; set; } = "사용 안 함";
    public string DiarizationWarning { get; set; } = string.Empty;
    public string ContentProfileId { get; set; } = TranscriptionProfileCatalog.DefaultId;
    public string ContentProfileName { get; set; } = "일반 회의·대화";
    public string LanguageMode { get; set; } = "fixed";
    public string PrimaryLanguage { get; set; } = "ko-KR";
    public string DetectedLanguage { get; set; } = string.Empty;
    public double DetectedLanguageProbability { get; set; }
    public string LanguageConstraintWarning { get; set; } = string.Empty;
    public string AudioQualityWarning { get; set; } = string.Empty;
    public double AudioRmsDb { get; set; }
    public double AudioPeakDb { get; set; }
    public string AiOrganizationMode { get; set; } = string.Empty;
    public string AiPromptVersion { get; set; } = GeminiServicePromptVersion;
    public string ReportTemplateId { get; set; } = ReportTemplateCatalog.DefaultId;
    public string ReportTemplateName { get; set; } = "표준 회의록";
    public string AiSourceRange { get; set; } = "전체 원문";
    public int AiRangeStartMinute { get; set; }
    public int AiRangeEndMinute { get; set; }
    public double AiRangeStartSeconds { get; set; }
    public double AiRangeEndSeconds { get; set; }
    public string AiStatus { get; set; } = "사용 안 함";
    public string AiLastError { get; set; } = string.Empty;
    public int AiAttemptCount { get; set; }
    public DateTime? AiUpdatedAt { get; set; }
    public string AudioSource { get; set; } = "마이크";
    public MeetingSummary Summary { get; set; } = new();
    public string ProcessingStatus { get; set; } = "완료";
    public DateTime? CompletedAt { get; set; }
    public string TextEncoding { get; set; } = "UTF-8";
    public string DisplayDate => StartedAt.ToString("yyyy.MM.dd  HH:mm");
    public string DurationText => Duration.TotalSeconds < 1 ? "오디오 파일" : Duration.ToString(@"hh\:mm\:ss");
    private const string GeminiServicePromptVersion = "meeting-evidence-v3";
}

public sealed class TranscriptSegment
{
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Speaker { get; set; } = string.Empty;
    public string Timestamp => Start.ToString(@"hh\:mm\:ss");
    public string SpeakerPrefix => string.IsNullOrWhiteSpace(Speaker) ? string.Empty : $"화자 {Speaker}: ";
}

public sealed class LocalTranscript
{
    public List<TranscriptSegment> Segments { get; set; } = [];
    public string DetectedLanguage { get; set; } = string.Empty;
    public double LanguageProbability { get; set; }
    public string LanguageConstraintWarning { get; set; } = string.Empty;
    public string AudioQualityWarning { get; set; } = string.Empty;
    public double AudioRmsDb { get; set; }
    public double AudioPeakDb { get; set; }
    public int DetectedSpeakerCount { get; set; }
    public string DiarizationStatus { get; set; } = "사용 안 함";
    public string DiarizationWarning { get; set; } = string.Empty;
    public double ProcessingSeconds { get; set; }
    public double RealtimeFactor { get; set; }
    public List<string> Warnings { get; set; } = [];
    public string Text => string.Join(Environment.NewLine, Segments.Select(x => $"[{x.Timestamp}] {x.SpeakerPrefix}{x.Text.Trim()}"));
}

public sealed class MeetingSummary
{
    public string Overview { get; set; } = string.Empty;
    public List<string> Topics { get; set; } = [];
    public List<string> Decisions { get; set; } = [];
    public List<ActionItem> ActionItems { get; set; } = [];
    public List<string> OpenQuestions { get; set; } = [];
}

public sealed class AiReportResult
{
    public string ReportMarkdown { get; set; } = string.Empty;
    public MeetingSummary Summary { get; set; } = new();
}

public sealed class ActionItem
{
    public string Task { get; set; } = string.Empty;
    public string Owner { get; set; } = "미정";
    public string DueDate { get; set; } = "미정";
}
