using System.IO;
using MeetingFlow.App.Models;
using MeetingFlow.App.Services;

namespace MeetingFlow.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"MeetingFlowTests-{Guid.NewGuid()}");

    [Fact]
    public void Settings_RoundTrip_PreservesValuesAndEncryptsKey()
    {
        var service = new SettingsService(_root);
        var protectedKey = SettingsService.ProtectApiKey("test-secret-key");
        service.Save(new AppSettings { ProtectedApiKey = protectedKey, AiProvider = "openai", ProtectedOpenAiApiKey = SettingsService.ProtectApiKey("openai-secret"), CompatibleApiEndpoint = "http://localhost:11434/v1", Model = "gpt-test", Temperature = 0.4, ShowTimelineEditor = false, AutoRetryPendingAi = false, GeminiConnectionTimeoutSeconds = 15, RequireTranscriptReviewBeforeAi = true, EnableLiveDraft = true, LiveDraftModel = "base", SttQualityProfile = "cpu-accurate", SttEngine = "hybrid-compare", CrisperModel = "small", CrisperMode = "intended", CrisperChunkMinutes = 2, CrisperChunkSeconds = 30, VadProfile = "noisy", SpeakerDiarizationMode = "fixed", SpeakerCount = 3 });

        var loaded = service.Load();

        Assert.Equal("gpt-test", loaded.Model);
        Assert.Equal(0.4, loaded.Temperature);
        Assert.NotEqual("test-secret-key", loaded.ProtectedApiKey);
        Assert.Equal("test-secret-key", SettingsService.UnprotectApiKey(loaded.ProtectedApiKey));
        Assert.Equal("openai", loaded.AiProvider);
        Assert.Equal("openai-secret", GeminiService.GetApiKey(loaded));
        Assert.Equal("http://localhost:11434/v1", loaded.CompatibleApiEndpoint);
        Assert.False(loaded.ShowTimelineEditor);
        Assert.False(loaded.AutoRetryPendingAi);
        Assert.Equal(15, loaded.GeminiConnectionTimeoutSeconds);
        Assert.True(loaded.RequireTranscriptReviewBeforeAi);
        Assert.True(loaded.EnableLiveDraft);
        Assert.Equal("base", loaded.LiveDraftModel);
        Assert.Equal("hybrid-compare", loaded.SttEngine);
        Assert.Equal("small", loaded.CrisperModel);
        Assert.Equal("intended", loaded.CrisperMode);
        Assert.Equal(2, loaded.CrisperChunkMinutes);
        Assert.Equal(30, loaded.CrisperChunkSeconds);
        Assert.Equal("noisy", loaded.VadProfile);
        Assert.Equal("cpu-accurate", loaded.SttQualityProfile);
        Assert.Equal("fixed", loaded.SpeakerDiarizationMode);
        Assert.Equal(3, loaded.SpeakerCount);
    }

    [Fact]
    public void Settings_Load_RepairsMissingLegacyValues()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "settings.json"), """{"AiProvider":null,"VadProfile":null,"CrisperChunkSeconds":0,"SpeakerCount":0}""");

        var loaded = new SettingsService(_root).Load();

        Assert.Equal("gemini", loaded.AiProvider);
        Assert.Equal("balanced", loaded.VadProfile);
        Assert.Equal(30, loaded.CrisperChunkSeconds);
        Assert.Equal(2, loaded.SpeakerCount);
    }

    [Fact]
    public void Repository_SavesLoadsAndDeletesMeeting()
    {
        var repository = new MeetingRepository(_root);
        var meeting = new MeetingRecord
        {
            Title = "설계 검토",
            Transcript = "회의 전사",
            RawTranscript = "회의 전사",
            LiveDraftTranscript = "[00:00:00] 실시간 저장본",
            LiveDraftUpdatedAt = DateTime.Now,
            AiNotesText = "AI 회의 요약",
            ReportTemplateId = "technical-review",
            ReportTemplateName = "04. 기술·설계 검토서",
            AiStatus = "대기",
            AiLastError = "네트워크 연결 실패",
            TranscriptReviewed = true,
            TranscriptReviewedAt = DateTime.Now,
            SttQualityProfile = "cpu-accurate",
            SpeakerDiarizationMode = "fixed",
            ExpectedSpeakerCount = 2,
            DetectedSpeakerCount = 2,
            DiarizationStatus = "완료",
            SecondaryTranscript = "보조 회의 전사",
            SttComparisonSummary = "1개 시간 구간 비교",
            SttDisagreementCount = 0,
            SttProcessingSeconds = 12.3,
            SttRealtimeFactor = 2.1,
            TranscriptSegments = [new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(3), Speaker = "A", Text = "회의 전사" }],
            SpeakerNames = new Dictionary<string, string> { ["A"] = "김과장" },
            Summary = new MeetingSummary { Overview = "요약" }
        };

        repository.Save(meeting);
        var loaded = repository.LoadAll();

        Assert.Single(loaded);
        Assert.Equal("설계 검토", loaded[0].Title);
        Assert.Equal("요약", loaded[0].Summary.Overview);
        Assert.Equal("회의 전사", loaded[0].RawTranscript);
        Assert.Equal("[00:00:00] 실시간 저장본", loaded[0].LiveDraftTranscript);
        Assert.Equal("AI 회의 요약", loaded[0].AiNotesText);
        Assert.Equal("technical-review", loaded[0].ReportTemplateId);
        Assert.Equal("대기", loaded[0].AiStatus);
        Assert.Equal("네트워크 연결 실패", loaded[0].AiLastError);
        Assert.True(loaded[0].TranscriptReviewed);
        Assert.Equal("완료", loaded[0].DiarizationStatus);
        Assert.Equal(2, loaded[0].DetectedSpeakerCount);
        Assert.Equal("A", loaded[0].TranscriptSegments[0].Speaker);
        Assert.Equal("김과장", loaded[0].SpeakerNames["A"]);
        Assert.Equal("cpu-accurate", loaded[0].SttQualityProfile);
        Assert.Equal("보조 회의 전사", loaded[0].SecondaryTranscript);
        Assert.Equal(2.1, loaded[0].SttRealtimeFactor);
        var jsonBytes = File.ReadAllBytes(Path.Combine(_root, "Meetings", $"{meeting.Id}.json"));
        Assert.False(jsonBytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.Contains("회의 전사", System.Text.Encoding.UTF8.GetString(jsonBytes));

        repository.Delete(meeting.Id);
        Assert.Empty(repository.LoadAll());
    }

    [Fact]
    public void Repository_MarksEmptyTranscriptWithSavedAudioAsRecoverable()
    {
        var audioPath = Path.Combine(_root, "recoverable.wav");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(audioPath, [0, 1, 2, 3]);
        var repository = new MeetingRepository(_root);
        repository.Save(new MeetingRecord
        {
            Title = "복구 회의",
            AudioPath = audioPath,
            ProcessingStatus = "AI 정리 대기",
            AiStatus = "대기"
        });

        var loaded = Assert.Single(repository.LoadAll());

        Assert.Equal("STT 재처리 필요 · 저장된 오디오 있음", loaded.ProcessingStatus);
        Assert.Equal("STT 재처리 필요", loaded.AiStatus);
    }

    [Fact]
    public void ReportTemplates_ProvideReusableContentAwareFormats()
    {
        Assert.True(ReportTemplateCatalog.All.Count >= 12);
        Assert.All(ReportTemplateCatalog.All, template =>
        {
            Assert.False(string.IsNullOrWhiteSpace(template.Id));
            Assert.False(string.IsNullOrWhiteSpace(template.Name));
            Assert.False(string.IsNullOrWhiteSpace(template.Instructions));
        });
        Assert.Equal("04. 기술·설계 검토서", ReportTemplateCatalog.Get("technical-review").Name);
        Assert.Equal("11. 뉴스·미디어 브리핑", ReportTemplateCatalog.Get("news-brief").Name);
    }

    [Fact]
    public void AiReportParser_RepairsEscapedJsonWithTrailingGarbage()
    {
        var malformed = "{\\\"reportMarkdown\\\":\\\"# 뉴스 요약\\\\n\\\\n확인된 본문\\\",\\\"summary\\\":{\\\"overview\\\":\\\"요약\\\",\\\"topics\\\":[\\\"정치\\\"],\\\"decisions\\\":[],\\\"actionItems\\\":[],\\\"openQuestions\\\":[]}}\\n}\\\"]}\\n:openQuestions\\\":[\\\"없음\\\"]}}";

        var parsed = AiReportParser.Parse(malformed);

        Assert.StartsWith("# 뉴스 요약", parsed.ReportMarkdown);
        Assert.Contains("확인된 본문", parsed.ReportMarkdown);
        Assert.Equal("요약", parsed.Summary.Overview);
        Assert.Equal(["정치"], parsed.Summary.Topics);
    }

    [Fact]
    public void TranscriptionProfiles_DoNotInjectTechnicalTermsUnlessEnabled()
    {
        var newsPrompt = TranscriptionProfileCatalog.BuildPrompt("ko-KR", "news", "ko,en", false, "BMS, FPCB");
        var technicalPrompt = TranscriptionProfileCatalog.BuildPrompt("ko-KR", "technical", "ko,en", true, "BMS, FPCB");

        Assert.Contains("뉴스 방송", newsPrompt);
        Assert.DoesNotContain("BMS", newsPrompt);
        Assert.Contains("BMS, FPCB", technicalPrompt);
        Assert.Equal("zh", LanguageCatalog.ToWhisperCode("zh-CN"));
        Assert.Equal("de", LanguageCatalog.ToWhisperCode("de-DE"));
        Assert.Equal("fr", LanguageCatalog.ToWhisperCode("fr-FR"));
    }

    [Fact]
    public void CpuQualityPresets_MapLegacySettingsAndExposeAccurateMode()
    {
        var migrated = SttQualityPresetCatalog.Get("korean-accurate");
        var accurate = SttQualityPresetCatalog.Get("cpu-accurate");

        Assert.Equal("cpu-accurate", migrated.Id);
        Assert.Equal("medium", accurate.FinalModel);
        Assert.Equal("base", accurate.LiveModel);
        Assert.Equal(5, accurate.BeamSize);
        Assert.Contains(SttQualityPresetCatalog.All, x => x.Id == "cpu-maximum" && x.FinalModel == "large-v3-turbo");
        Assert.Equal("media", TranscriptionProfileCatalog.Get("media").Id);
    }

    [Fact]
    public void TranscriptRange_SelectsOnlyOverlappingSegments()
    {
        var segments = new[]
        {
            new TranscriptSegment { Start = TimeSpan.FromMinutes(0), End = TimeSpan.FromMinutes(4), Text = "처음" },
            new TranscriptSegment { Start = TimeSpan.FromMinutes(9), End = TimeSpan.FromMinutes(11), Text = "경계" },
            new TranscriptSegment { Start = TimeSpan.FromMinutes(20), End = TimeSpan.FromMinutes(22), Text = "나중" }
        };

        var selected = LocalWhisperService.SelectRange(segments, 10, 20);

        Assert.Single(selected);
        Assert.Equal("경계", selected[0].Text);
    }

    [Fact]
    public void TranscriptRangeBySeconds_UsesExactTimelineSelection()
    {
        var segments = new[]
        {
            new TranscriptSegment { Start = TimeSpan.FromSeconds(0), End = TimeSpan.FromSeconds(4), Text = "처음" },
            new TranscriptSegment { Start = TimeSpan.FromSeconds(8), End = TimeSpan.FromSeconds(11), Text = "선택" },
            new TranscriptSegment { Start = TimeSpan.FromSeconds(13), End = TimeSpan.FromSeconds(16), Text = "끝" }
        };

        var selected = LocalWhisperService.SelectRangeBySeconds(segments, 7, 12);

        Assert.Single(selected);
        Assert.Equal("선택", selected[0].Text);
    }

    [Theory]
    [InlineData("ㅋㅋㅋㅋㅋㅋㅋㅋㅋㅋㅋㅋ", "")]
    [InlineData("회의를 시작합니다 ㅋㅋㅋㅋㅋㅋㅋㅋㅋㅋ", "회의를 시작합니다")]
    [InlineData("정상적인 회의 문장입니다.", "정상적인 회의 문장입니다.")]
    [InlineData("가가가가가가가가가가가가", "")]
    public void LiveTranscriptSanitizer_RemovesLowInformationRepetition(string input, string expected)
    {
        Assert.Equal(expected, TranscriptTextSanitizer.SanitizeLiveSegment(input));
    }

    [Fact]
    public void SpeakerLabels_ApplyNamesWithoutLosingStableIds()
    {
        var record = new MeetingRecord
        {
            Duration = TimeSpan.FromSeconds(10),
            SpeakerNames = new Dictionary<string, string> { ["A"] = "김과장" },
            TranscriptSegments = [new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), Speaker = "A", Text = "일정을 확정합니다." }]
        };

        var formatted = SpeakerLabelService.Format(record);
        var parsed = SpeakerLabelService.Parse(formatted, record.Duration, record.SpeakerNames);

        Assert.Contains("화자 A(김과장):", formatted);
        Assert.Equal("A", parsed[0].Speaker);
        Assert.Equal("김과장", record.SpeakerNames["A"]);
    }

    [Fact]
    public void TranscriptComparison_FlagsTimeAlignedDisagreement()
    {
        var primary = new[]
        {
            new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(5), Text = "오늘 예산을 확정했습니다" },
            new TranscriptSegment { Start = TimeSpan.FromSeconds(5), End = TimeSpan.FromSeconds(10), Text = "납기는 다음 주입니다" }
        };
        var secondary = new[]
        {
            new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(5), Text = "오늘 예산을 확정했습니다" },
            new TranscriptSegment { Start = TimeSpan.FromSeconds(5), End = TimeSpan.FromSeconds(10), Text = "완전히 다른 내용" }
        };

        var result = TranscriptComparisonService.Compare(primary, secondary);

        Assert.Equal(2, result.ComparedSegments);
        Assert.Equal(1, result.DisagreementCount);
        Assert.Contains("검토 필요 1개", result.Summary);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
