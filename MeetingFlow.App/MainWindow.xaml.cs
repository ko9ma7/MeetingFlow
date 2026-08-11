using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MeetingFlow.App.Models;
using MeetingFlow.App.Services;
using Microsoft.Win32;

namespace MeetingFlow.App;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService = new();
    private readonly MeetingRepository _repository = new();
    private readonly GeminiService _gemini = new();
    private readonly LocalWhisperService _localStt = new();
    private readonly PythonSttService _pythonStt = new();
    private readonly AudioRecorder _recorder = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ObservableCollection<MeetingRecord> _records = [];
    private AppSettings _settings = new();
    private DateTime _recordingStarted;
    private string _currentAudioPath = string.Empty;
    private TimeSpan _preparedDuration;
    private bool _updatingRangeUi;
    private bool _isBusy;
    private CancellationTokenSource? _processingCts;
    private bool _pythonReady;
    private bool _crisperReady;
    private bool _preparedAudioIsImported;
    private IReadOnlyList<PythonAudioDevice> _pythonDevices = [];
    private MeetingRecord? _activeRecord;
    private readonly StringBuilder _liveDraftText = new();
    private DateTime _lastLiveCheckpointSavedAt;
    private DateTime _lastSttCheckpointSavedAt;
    private string _lastLiveSegmentText = string.Empty;
    private bool _suppressLiveDraftSync;
    private readonly StringBuilder _sttDiagnostics = new();
    private string _lastSttLogMessage = string.Empty;

    public MainWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => UpdateTimer();
        _recorder.LevelChanged += (_, level) => Dispatcher.InvokeAsync(() => LevelMeter.Value = Math.Min(1, level * 3.2));
        _recorder.PreviewChunkReady += (_, chunk) => _ = _pythonStt.SubmitLiveChunkAsync(chunk.Path, chunk.StartSeconds);
        TemperatureSlider.ValueChanged += (_, _) => TemperatureValue.Text = TemperatureSlider.Value.ToString("0.0");
        _pythonStt.LevelChanged += (_, level) => Dispatcher.InvokeAsync(() => LevelMeter.Value = Math.Min(1, level * 3.2));
        _pythonStt.LiveDraftReceived += (_, segment) => Dispatcher.InvokeAsync(() => AppendLiveDraft(segment));
        _pythonStt.LiveDraftStatusChanged += (_, status) => Dispatcher.InvokeAsync(() => { LiveDraftStatusText.Text = status; AppendSttLog(status); });
        Loaded += async (_, _) => await InitializePythonAsync();
        Closing += (_, _) => { _recorder.Dispose(); _pythonStt.Dispose(); };
        LoadAppState();
        ShowPage(HomePage, "새 회의", HomeNav);
    }

    private void LoadAppState()
    {
        _settings = _settingsService.Load();
        if (!_settings.SttQualityConfigured)
        {
            _settings.WhisperModel = "medium";
            _settings.LiveDraftModel = "medium";
            _settings.SttBeamSize = 5;
        }
        var qualityPreset = SttQualityPresetCatalog.Get(_settings.SttQualityProfile);
        _settings.SttQualityProfile = qualityPreset.Id;
        _settings.WhisperModel = qualityPreset.FinalModel;
        _settings.LiveDraftModel = qualityPreset.LiveModel;
        _settings.SttBeamSize = qualityPreset.BeamSize;
        ApiKeyBox.Password = SettingsService.UnprotectApiKey(_settings.ProtectedApiKey);
        OpenAiApiKeyBox.Password = SettingsService.UnprotectApiKey(_settings.ProtectedOpenAiApiKey);
        AnthropicApiKeyBox.Password = SettingsService.UnprotectApiKey(_settings.ProtectedAnthropicApiKey);
        CompatibleApiKeyBox.Password = SettingsService.UnprotectApiKey(_settings.ProtectedCompatibleApiKey);
        CompatibleEndpointBox.Text = _settings.CompatibleApiEndpoint;
        AiProviderBox.SelectedIndex = _settings.AiProvider switch { "openai" => 1, "anthropic" => 2, "compatible" => 3, _ => 0 };
        ModelBox.Text = _settings.Model;
        TemperatureSlider.Value = _settings.Temperature;
        AutoSummaryBox.IsChecked = !_settings.RequireTranscriptReviewBeforeAi && _settings.AutoSummarize;
        RequireReviewBox.IsChecked = _settings.RequireTranscriptReviewBeforeAi;
        EnableLiveDraftBox.IsChecked = _settings.EnableLiveDraft;
        HuggingFaceTokenBox.Password = SettingsService.UnprotectApiKey(_settings.ProtectedHuggingFaceToken);
        PromptBox.Text = _settings.SummaryPrompt;
        SttVocabularyBox.Text = _settings.SttVocabulary;
        AllowedLanguagesBox.Text = _settings.AllowedLanguages;
        UseCustomVocabularyBox.IsChecked = _settings.UseCustomVocabulary;
        HallucinationGuardBox.IsChecked = _settings.EnableHallucinationGuard;
        SttBeamSizeBox.Text = _settings.SttBeamSize.ToString();
        ShowTimelineEditorBox.IsChecked = _settings.ShowTimelineEditor;
        AutoSelectGeminiModelBox.IsChecked = _settings.AutoSelectAvailableGeminiModel;
        AutoRetryPendingAiBox.IsChecked = _settings.AutoRetryPendingAi;
        GeminiTimeoutBox.Text = _settings.GeminiConnectionTimeoutSeconds.ToString();
        HomeReportTemplateBox.ItemsSource = ReportTemplateCatalog.All;
        HomeSttQualityBox.ItemsSource = SttQualityPresetCatalog.All;
        HomeSttQualityBox.SelectedItem = qualityPreset;
        HomeSttQualityHelp.Text = GetQualityHelp(qualityPreset);
        DefaultReportTemplateBox.ItemsSource = ReportTemplateCatalog.All;
        RecordTemplateBox.ItemsSource = ReportTemplateCatalog.All;
        ContentProfileBox.ItemsSource = TranscriptionProfileCatalog.All;
        LanguageBox.ItemsSource = LanguageCatalog.All;
        RecordRangeStartBox.ToolTip = "시작 시간 (HH:MM:SS 또는 초)";
        RecordRangeEndBox.ToolTip = "종료 시간 (HH:MM:SS 또는 초)";
        HomeReportTemplateBox.SelectedItem = ReportTemplateCatalog.Get(_settings.DefaultReportTemplateId);
        DefaultReportTemplateBox.SelectedItem = ReportTemplateCatalog.Get(_settings.DefaultReportTemplateId);
        ContentProfileBox.SelectedItem = TranscriptionProfileCatalog.Get(_settings.ContentProfile);
        LanguageBox.SelectedItem = LanguageCatalog.Get(_settings.Language);
        LanguageModeBox.SelectedIndex = _settings.LanguageMode switch { "auto" => 1, "mixed" => 2, _ => 0 };
        ReportLanguageBox.SelectedIndex = _settings.ReportLanguage switch { "ko-KR" => 1, "en-US" => 2, "zh-CN" => 3, "ja-JP" => 4, "de-DE" => 5, "fr-FR" => 6, _ => 0 };
        WhisperModelBox.SelectedIndex = _settings.WhisperModel switch { "tiny" => 0, "base" => 1, "small" => 2, "large-v3" => 4, "large-v3-turbo" => 5, _ => 3 };
        SttEngineBox.SelectedIndex = _settings.SttEngine switch { "python-crisperwhisper" => 1, "hybrid-compare" => 2, "csharp-whispernet" => 3, _ => 0 };
        LiveDraftModelBox.SelectedIndex = _settings.LiveDraftModel switch { "tiny" => 0, "small" => 2, "medium" => 3, _ => 1 };
        CrisperModelBox.SelectedIndex = _settings.CrisperModel switch { "medium" => 1, "turbo" => 2, "large" => 3, _ => 0 };
        CrisperModeBox.SelectedIndex = _settings.CrisperMode == "verbatim" ? 1 : 0;
        CrisperChunkSecondsBox.SelectedIndex = _settings.CrisperChunkSeconds switch { 15 => 0, 60 => 2, 120 => 3, _ => 1 };
        VadProfileBox.SelectedIndex = _settings.VadProfile switch { "noisy" => 1, "sensitive" => 2, _ => 0 };
        SpeakerModeBox.SelectedIndex = _settings.SpeakerDiarizationMode switch { "auto" => 1, "fixed" => 2, _ => 0 };
        SpeakerCountBox.Text = _settings.SpeakerCount.ToString();
        HomeSpeakerModeBox.SelectedIndex = SpeakerModeBox.SelectedIndex;
        HomeSpeakerCountBox.Text = _settings.SpeakerCount.ToString();
        AiModeBox.SelectedIndex = _settings.AiOrganizationMode switch { "사용 안 함" => 0, "간단 정리" => 1, "상세 회의록" => 3, _ => 2 };
        HomeAiModeBox.SelectedIndex = AiModeBox.SelectedIndex;
        MeetingTypeBox.SelectedIndex = _settings.ContentProfile switch { "business" => 1, "media" => 2, "news" => 3, "technical" => 4, "interview" => 5, "customer" => 6, "lecture" => 7, "legal" => 8, _ => 0 };
        AiRangeStartBox.Text = _settings.AiRangeStartMinute.ToString();
        AiRangeEndBox.Text = _settings.AiRangeEndMinute.ToString();
        WhisperModelStatusText.Text = _localStt.IsModelInstalled(_settings.WhisperModel) ? "선택한 모델이 설치되어 있습니다." : "첫 텍스트화 때 모델을 자동으로 다운로드합니다.";
        RefreshApiStatus();

        UpdateAudioSourceUi();
        ApplyFeatureVisibility();

        ReloadRecords();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"버전 {version?.Major}.{version?.Minor}.{version?.Build}";
    }

    private void RefreshApiStatus()
    {
        var hasKey = !string.IsNullOrWhiteSpace(GeminiService.GetApiKey(_settings)) || _settings.AiProvider == "compatible";
        var pendingCount = _repository.LoadAll().Count(x => x.AiStatus == "대기");
        ApiStatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#35A46F"));
        var engine = _pythonReady ? "PyAudio 준비" : "C# 로컬 STT";
        ApiStatusText.Text = pendingCount > 0 ? $"{engine} · AI 대기 {pendingCount}건" : hasKey ? $"{engine} · AI 설정됨" : $"{engine} · 로컬 전용";
    }

    private async Task InitializePythonAsync()
    {
        try
        {
            var health = await _pythonStt.GetHealthAsync();
            _pythonReady = health.Ready;
            if (!_pythonReady) throw new InvalidOperationException("PyAudio 또는 faster-whisper가 설치되지 않았습니다.");
            _pythonDevices = await _pythonStt.GetDevicesAsync();
            UpdateAudioSourceUi();
            WhisperModelStatusText.Text = _pythonStt.IsModelPrepared(_settings.WhisperModel)
                ? $"Python {health.Python} · {_settings.WhisperModel} 모델 설치 완료"
                : $"Python {health.Python} 준비 · {_settings.WhisperModel} 모델은 첫 텍스트화 때 다운로드됩니다 (medium 약 1.5GB)";
            DiarizationStatusText.Text = health.Pyannote
                ? "pyannote Community-1 엔진 설치됨 · Hugging Face 토큰 설정 후 사용 가능"
                : "화자 분리는 선택 기능입니다 · pyannote.audio 설치와 Hugging Face 토큰이 필요합니다";
            FooterStatus.Text = "Python 로컬 음성 엔진 준비됨";
            AppendSttLog($"faster-whisper 준비 · Python {health.Python}");
        }
        catch (Exception ex)
        {
            _pythonReady = false;
            WhisperModelStatusText.Text = $"Python 엔진 미사용 · C# Whisper 폴백: {ex.Message}";
            AppendSttLog($"faster-whisper 미사용 · {ex.Message}");
        }
        try
        {
            var crisper = await _pythonStt.GetCrisperHealthAsync();
            _crisperReady = crisper.CrisperWhisper;
            CrisperStatusText.Text = _crisperReady
                ? $"CrisperWhisper 2.0 준비 · Python {crisper.Python} · CPU 정밀 후처리 전용"
                : "CrisperWhisper 미설치 · scripts/setup-python.ps1 실행 필요";
            AppendSttLog(_crisperReady ? $"CrisperWhisper 준비 · Python {crisper.Python}" : "CrisperWhisper 전용 환경 미설치");
        }
        catch (Exception ex)
        {
            _crisperReady = false;
            CrisperStatusText.Text = $"CrisperWhisper 미사용 · {ex.Message}";
            AppendSttLog($"CrisperWhisper 확인 실패 · {ex.Message}");
        }
        RefreshApiStatus();
    }

    private void AppendSttLog(string message)
    {
        if (string.Equals(message, _lastSttLogMessage, StringComparison.Ordinal)) return;
        _lastSttLogMessage = message;
        _sttDiagnostics.Append('[').Append(DateTime.Now.ToString("HH:mm:ss")).Append("] ").AppendLine(message);
        if (_sttDiagnostics.Length > 12000) _sttDiagnostics.Remove(0, _sttDiagnostics.Length - 8000);
        if (SttDiagnosticsBox is null) return;
        SttDiagnosticsBox.Text = _sttDiagnostics.ToString();
        SttDiagnosticsBox.ScrollToEnd();
    }

    private void AudioSourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceBox is null) return;
        if (GetComboTag(AudioSourceBox, "microphone") == "loopback" && MeetingTypeBox?.SelectedIndex == 0)
            MeetingTypeBox.SelectedIndex = 2;
        UpdateAudioSourceUi();
    }

    private void HomeSttQualityBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HomeSttQualityBox?.SelectedItem is not SttQualityPreset preset || HomeSttQualityHelp is null) return;
        HomeSttQualityHelp.Text = GetQualityHelp(preset);
        _settings.SttQualityProfile = preset.Id;
        _settings.WhisperModel = preset.FinalModel;
        _settings.LiveDraftModel = preset.LiveModel;
        _settings.SttBeamSize = preset.BeamSize;
        _settings.SttQualityConfigured = true;
        if (WhisperModelBox is not null)
            WhisperModelBox.SelectedIndex = preset.FinalModel switch { "small" => 2, "large-v3" => 4, "large-v3-turbo" => 5, _ => 3 };
        if (LiveDraftModelBox is not null)
            LiveDraftModelBox.SelectedIndex = preset.LiveModel switch { "small" => 2, "medium" => 3, _ => 1 };
        if (SttBeamSizeBox is not null) SttBeamSizeBox.Text = preset.BeamSize.ToString();
        _settingsService.Save(_settings);
    }

    private string GetQualityHelp(SttQualityPreset preset) => $"{preset.Description} · {(_pythonStt.IsModelPrepared(preset.FinalModel) ? "모델 준비됨" : "첫 사용 시 자동 설치")}";

    private void MeetingTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HomeReportTemplateBox is null || HomeReportTemplateBox.ItemsSource is null) return;
        var profile = TranscriptionProfileCatalog.Get(GetContentProfileId());
        HomeReportTemplateBox.SelectedItem = ReportTemplateCatalog.Get(profile.Id == "general" ? _settings.DefaultReportTemplateId : profile.RecommendedTemplateId);
    }

    private void UpdateAudioSourceUi()
    {
        var loopback = GetComboTag(AudioSourceBox, "microphone") == "loopback";
        if (loopback)
        {
            DeviceLabel.Text = "재생 장치";
            try
            {
                DeviceBox.ItemsSource = new[] { $"기본 스피커 · {AudioRecorder.GetSystemOutputDevice()}" };
                DeviceBox.SelectedIndex = 0;
                DeviceBox.IsEnabled = false;
                RecordButton.IsEnabled = true;
                AudioSourceHelp.Text = "PC에서 재생되는 미디어·회의 소리를 WASAPI 루프백으로 직접 받습니다. 마이크 소리는 포함되지 않습니다.";
            }
            catch (Exception ex)
            {
                DeviceBox.ItemsSource = new[] { "사용 가능한 스피커가 없습니다" };
                DeviceBox.SelectedIndex = 0;
                RecordButton.IsEnabled = false;
                AudioSourceHelp.Text = ex.Message;
            }
            return;
        }

        DeviceLabel.Text = "마이크 입력 장치";
        DeviceBox.IsEnabled = true;
        if (_pythonReady && _pythonDevices.Count > 0)
            DeviceBox.ItemsSource = _pythonDevices.Select(x => $"PyAudio · {x.Name}").ToList();
        else
            DeviceBox.ItemsSource = AudioRecorder.GetInputDevices();
        DeviceBox.SelectedIndex = DeviceBox.Items.Count > 0 ? 0 : -1;
        RecordButton.IsEnabled = DeviceBox.SelectedIndex >= 0;
        AudioSourceHelp.Text = DeviceBox.SelectedIndex >= 0 ? "마이크 음성을 로컬 WAV로 녹음합니다." : "Windows 마이크 권한과 연결 상태를 확인하세요.";
    }

    private void ApplyFeatureVisibility()
    {
        if (HomeRangeStartSlider.Parent is Grid row && row.Parent is StackPanel panel && panel.Parent is Border timeline)
            timeline.Visibility = _settings.ShowTimelineEditor ? Visibility.Visible : Visibility.Collapsed;
        if (AiRangeStartBox.Parent is StackPanel defaultsColumn && defaultsColumn.Parent is Grid defaultsGrid)
            defaultsGrid.Visibility = Visibility.Collapsed;
    }

    private void ShowPage(UIElement page, string title, Button selectedNav)
    {
        HomePage.Visibility = Visibility.Collapsed;
        RecordsPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        AboutPage.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;
        PageTitle.Text = title;
        foreach (var button in new[] { HomeNav, RecordsNav, SettingsNav, AboutNav })
        {
            button.Background = Brushes.Transparent;
            button.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#BFC8DA"));
        }
        selectedNav.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3157D5"));
        selectedNav.Foreground = Brushes.White;
    }

    private void HomeNav_Click(object sender, RoutedEventArgs e) => ShowPage(HomePage, "새 회의", HomeNav);
    private void RecordsNav_Click(object sender, RoutedEventArgs e) { ReloadRecords(); ShowPage(RecordsPage, "회의 기록", RecordsNav); }
    private void SettingsNav_Click(object sender, RoutedEventArgs e) => ShowPage(SettingsPage, "설정", SettingsNav);
    private void AboutNav_Click(object sender, RoutedEventArgs e) => ShowPage(AboutPage, "앱 정보", AboutNav);
    private void ShowTranscriptReview_Click(object sender, RoutedEventArgs e) => ResultTabs.SelectedIndex = 0;
    private void ShowAiReport_Click(object sender, RoutedEventArgs e) => ResultTabs.SelectedIndex = 1;
    private void ShowSttComparison_Click(object sender, RoutedEventArgs e) => ResultTabs.SelectedIndex = 2;

    private void NewMeetingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            ShowError("처리 중인 회의가 있습니다", "현재 작업을 완료하거나 취소한 뒤 새 회의를 시작하세요.");
            return;
        }

        MeetingTitleBox.Text = "새 회의";
        TimerText.Text = "00:00:00";
        TranscriptBox.Text = "녹음하거나 오디오 파일을 가져오면 로컬 STT 원문이 여기에 표시됩니다.";
        SummaryBox.Text = "선택한 AI로 정리하면 회의 요약, 결정사항과 실행 항목이 여기에 표시됩니다.";
        ComparisonSummaryText.Text = "이중 검증 엔진을 선택하면 시간 구간별 일치도를 표시합니다.";
        SecondaryTranscriptBox.Text = "보조 전사 결과가 여기에 별도로 보존됩니다.";
        RecordingConsentBox.IsChecked = false;
        _activeRecord = null;
        _preparedAudioIsImported = false;
        SaveTranscriptButton.IsEnabled = false;
        GenerateAiButton.IsEnabled = false;
        ReviewStatusText.Text = "전사 완료 후 원문을 확인하고 AI 정리를 실행하세요";
        _liveDraftText.Clear();
        LiveDraftBox.Text = "녹음을 시작하면 빠른 초안 모델이 말하는 내용을 구간별로 표시합니다.";
        LiveDraftStatusText.Text = "실시간 초안 대기";
        TranscriptCount.Text = "0자";
        WorkspaceTabs.SelectedIndex = 0;
        ResultTabs.SelectedIndex = 0;
        FooterStatus.Text = "새 회의 준비됨";
        PrepareTimeline(string.Empty, TimeSpan.Zero);
    }

    private void CancelProcessingButton_Click(object sender, RoutedEventArgs e)
    {
        _processingCts?.Cancel();
        ProgressText.Text = "진행 중인 작업을 취소하고 있습니다…";
        CancelProcessingButton.IsEnabled = false;
    }

    private async void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || DeviceBox.SelectedIndex < 0) return;
        if (RecordingConsentBox.IsChecked != true)
        {
            ShowError("녹음 동의를 확인하세요", "참석자에게 녹음과 전사 사실을 알린 뒤 확인란을 선택하세요.");
            return;
        }
        try
        {
            var audioFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetingFlow", "Audio");
            _currentAudioPath = Path.Combine(audioFolder, $"meeting-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
            _preparedAudioIsImported = false;
            var loopback = GetComboTag(AudioSourceBox, "microphone") == "loopback";
            var livePreview = _settings.EnableLiveDraft && _pythonReady && _settings.SttEngine != "csharp-whispernet";
            _activeRecord = null;
            _liveDraftText.Clear();
            _lastLiveSegmentText = string.Empty;
            _suppressLiveDraftSync = true;
            LiveDraftBox.Clear();
            _suppressLiveDraftSync = false;
            LiveDraftStatusText.Text = livePreview ? "실시간 저장 전사 엔진 시작 중" : "실시간 전사 꺼짐 · 오디오만 안전하게 저장";
            if (livePreview)
                await _pythonStt.StartLivePreviewAsync(_settings.LiveDraftModel, _settings.Language, _settings.LanguageMode, _settings.SttQualityProfile, _settings.VadProfile);
            if (loopback)
                _recorder.StartLoopback(_currentAudioPath, livePreview);
            else if (_pythonReady && _pythonDevices.Count > DeviceBox.SelectedIndex)
                await _pythonStt.StartRecordingAsync(_currentAudioPath, _pythonDevices[DeviceBox.SelectedIndex].Index, livePreview);
            else
                _recorder.Start(_currentAudioPath, DeviceBox.SelectedIndex, false);
            _recordingStarted = DateTime.Now;
            _activeRecord = CreateRecordingCheckpoint(_currentAudioPath, _recordingStarted);
            await Task.Run(() => _repository.Save(_activeRecord));
            _lastLiveCheckpointSavedAt = DateTime.Now;
            _timer.Start();
            RecordButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            ImportButton.IsEnabled = false;
            DeviceBox.IsEnabled = false;
            RecordingStatus.Text = "녹음 중";
            RecordingDot.Fill = new SolidColorBrush(Color.FromRgb(220, 38, 38));
            RecordingHint.Text = loopback ? "스피커에서 재생되는 시스템 소리를 직접 녹음하고 있습니다" : "마이크 음성을 이 PC에 안전하게 저장하고 있습니다";
            FooterStatus.Text = "녹음 중 · 오디오와 실시간 전사를 로컬에 자동 저장합니다";
        }
        catch (Exception ex)
        {
            if (_pythonStt.IsLivePreviewRunning) await _pythonStt.StopLivePreviewAsync();
            ShowError("녹음을 시작할 수 없습니다", ex.Message);
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_recorder.IsRecording && !_pythonStt.IsRecording) return;
        if (_pythonStt.IsRecording) await _pythonStt.StopRecordingAsync();
        else _recorder.Stop();
        if (_pythonStt.IsLivePreviewRunning)
        {
            await Task.Delay(150);
            await _pythonStt.StopLivePreviewAsync();
        }
        _timer.Stop();
        var duration = DateTime.Now - _recordingStarted;
        ResetRecordingControls();
        PrepareTimeline(_currentAudioPath, duration);
        SaveLiveCheckpoint(duration, "녹음 완료 · 자동 정밀 보정 중", true);
        LiveDraftStatusText.Text = "실시간 저장 완료 · 전체 음성을 자동으로 정밀 보정합니다";
        TranscriptBox.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#172033"));
        TranscriptBox.Text = _liveDraftText.Length == 0 ? "실시간 전사 내용이 없어 저장된 오디오에서 정밀 전사를 시작합니다." : _liveDraftText.ToString();
        TranscriptCount.Text = $"{_liveDraftText.Length:N0}자";
        ReviewStatusText.Text = "실시간 저장본을 먼저 표시했습니다 · 지금 자동 정밀 보정 중이며 완료되면 같은 기록을 갱신합니다";
        WorkspaceTabs.SelectedIndex = 1;
        ResultTabs.SelectedIndex = 0;
        await ProcessAudioAsync(_currentAudioPath, duration);
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "회의 오디오 선택",
            Filter = "오디오 파일|*.wav;*.mp3;*.m4a;*.flac;*.ogg|모든 파일|*.*"
        };
        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                var duration = AudioRecorder.GetAudioDuration(dialog.FileName);
                _preparedAudioIsImported = true;
                PrepareTimeline(dialog.FileName, duration);
                RecordingStatus.Text = "오디오 준비 완료";
                RecordingHint.Text = "전체 음성을 정확하게 텍스트화할 준비가 되었습니다";
            }
            catch (Exception ex) { ShowError("오디오 파일을 열 수 없습니다", ex.Message); }
        }
    }

    private async void TranscribeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || string.IsNullOrWhiteSpace(_currentAudioPath) || !File.Exists(_currentAudioPath)) return;
        await ProcessAudioAsync(_currentAudioPath, _preparedDuration);
    }

    private void PrepareTimeline(string audioPath, TimeSpan duration)
    {
        _currentAudioPath = audioPath;
        _preparedDuration = duration;
        var total = Math.Max(0, duration.TotalSeconds);
        _updatingRangeUi = true;
        HomeRangeStartSlider.Maximum = Math.Max(1, total);
        HomeRangeEndSlider.Maximum = Math.Max(1, total);
        HomeRangeStartSlider.Value = 0;
        HomeRangeEndSlider.Value = Math.Max(1, total);
        HomeRangeStartBox.Text = FormatClock(TimeSpan.Zero);
        HomeRangeEndBox.Text = FormatClock(duration);
        var enabled = total > 0;
        HomeRangeStartSlider.IsEnabled = enabled;
        HomeRangeEndSlider.IsEnabled = enabled;
        HomeRangeStartBox.IsEnabled = enabled;
        HomeRangeEndBox.IsEnabled = enabled;
        TranscribeButton.IsEnabled = enabled && _preparedAudioIsImported;
        TranscribeButton.Visibility = enabled && _preparedAudioIsImported ? Visibility.Visible : Visibility.Collapsed;
        MediaDurationText.Text = enabled ? $"전체 길이 {FormatClock(duration)} · 기본값: 전체 구간" : "오디오를 녹음하거나 가져오면 전체 길이가 표시됩니다";
        _updatingRangeUi = false;
        if (enabled) FooterStatus.Text = _preparedAudioIsImported
            ? "가져온 오디오 준비 완료 · 전사를 시작하세요"
            : "녹음 파일과 실시간 전사를 저장했습니다 · 자동 정밀 보정을 준비합니다";
    }

    private void HomeRangeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingRangeUi || HomeRangeStartBox is null || HomeRangeEndBox is null || HomeRangeStartSlider is null || HomeRangeEndSlider is null) return;
        _updatingRangeUi = true;
        if (HomeRangeStartSlider.Value >= HomeRangeEndSlider.Value)
        {
            if (ReferenceEquals(sender, HomeRangeStartSlider)) HomeRangeStartSlider.Value = Math.Max(0, HomeRangeEndSlider.Value - 1);
            else HomeRangeEndSlider.Value = Math.Min(HomeRangeEndSlider.Maximum, HomeRangeStartSlider.Value + 1);
        }
        HomeRangeStartBox.Text = FormatClock(TimeSpan.FromSeconds(HomeRangeStartSlider.Value));
        HomeRangeEndBox.Text = FormatClock(TimeSpan.FromSeconds(HomeRangeEndSlider.Value));
        _updatingRangeUi = false;
    }

    private void HomeRangeTimeBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_updatingRangeUi) return;
        try
        {
            var start = ParseClock(HomeRangeStartBox.Text, "시작 시간");
            var end = ParseClock(HomeRangeEndBox.Text, "종료 시간");
            if (start < TimeSpan.Zero || end > _preparedDuration || end <= start) throw new ArgumentException("시작은 종료보다 앞서야 하고 전체 길이 안에 있어야 합니다.");
            _updatingRangeUi = true;
            HomeRangeStartSlider.Value = start.TotalSeconds;
            HomeRangeEndSlider.Value = end.TotalSeconds;
            HomeRangeStartBox.Text = FormatClock(start);
            HomeRangeEndBox.Text = FormatClock(end);
        }
        catch (ArgumentException ex) { ShowError("AI 적용 구간을 확인하세요", ex.Message); }
        finally { _updatingRangeUi = false; }
    }

    private async Task ProcessAudioAsync(string audioPath, TimeSpan duration)
    {
        var aiMode = GetComboTag(HomeAiModeBox, "표준 회의록");
        var reportTemplate = HomeReportTemplateBox.SelectedItem as ReportTemplate ?? ReportTemplateCatalog.Get(_settings.DefaultReportTemplateId);
        double startSeconds;
        double endSeconds;
        try
        {
            startSeconds = ParseClock(HomeRangeStartBox.Text, "AI 정리 시작 위치").TotalSeconds;
            endSeconds = ParseClock(HomeRangeEndBox.Text, "AI 정리 종료 위치").TotalSeconds;
            if (endSeconds <= startSeconds) throw new ArgumentException("AI 정리 종료 위치는 시작 위치보다 커야 합니다.");
        }
        catch (ArgumentException ex)
        {
            ShowError("AI 정리 범위를 확인하세요", ex.Message);
            return;
        }
        _processingCts?.Dispose();
        _processingCts = new CancellationTokenSource();
        var token = _processingCts.Token;
        if (_activeRecord is null || !string.Equals(_activeRecord.AudioPath, audioPath, StringComparison.OrdinalIgnoreCase))
        {
            _activeRecord = CreateRecordingCheckpoint(audioPath, _preparedAudioIsImported ? DateTime.Now : _recordingStarted);
            _activeRecord.AudioSource = _preparedAudioIsImported ? "오디오 파일 가져오기" : _activeRecord.AudioSource;
        }
        _activeRecord.Duration = duration;
        _activeRecord.ProcessingStatus = "STT 처리 중 · 구간별 자동 저장";
        _activeRecord.AiStatus = "STT 처리 중";
        await Task.Run(() => _repository.Save(_activeRecord), token);
        _lastSttCheckpointSavedAt = DateTime.Now;
        WorkspaceTabs.SelectedIndex = 1;
        ResultTabs.SelectedIndex = 0;
        SetBusy(true, "2/3  저장된 전체 음성을 자동으로 정밀 보정합니다…");
        try
        {
            var progress = new Progress<SttProgress>(update =>
            {
                ProcessingProgressBar.IsIndeterminate = update.Percent < 0;
                if (update.Percent >= 0) ProcessingProgressBar.Value = update.Percent;
                ProgressText.Text = update.Percent < 0 ? $"2/3  {update.Stage}" : $"2/3  {update.Stage}  {update.Percent:P0}";
                AppendSttLog(update.Stage);
                if (!string.IsNullOrWhiteSpace(update.PartialTranscript))
                {
                    TranscriptBox.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#172033"));
                    TranscriptBox.Text = update.PartialTranscript;
                    TranscriptBox.ScrollToEnd();
                    TranscriptCount.Text = $"{update.PartialTranscript.Length:N0}자";
                    if (_activeRecord is not null && DateTime.Now - _lastSttCheckpointSavedAt >= TimeSpan.FromSeconds(15))
                    {
                        _activeRecord.Transcript = update.PartialTranscript;
                        _activeRecord.RawTranscript = update.PartialTranscript;
                        _activeRecord.ProcessingStatus = $"STT 처리 중 · {Math.Max(update.Percent, 0):P0} · 부분 결과 자동 저장";
                        _repository.Save(_activeRecord);
                        _lastSttCheckpointSavedAt = DateTime.Now;
                    }
                }
            });
            var contentProfile = TranscriptionProfileCatalog.Get(GetContentProfileId());
            var speakerMode = GetComboTag(HomeSpeakerModeBox, _settings.SpeakerDiarizationMode);
            var speakerCount = ParseSpeakerCount(HomeSpeakerCountBox.Text);
            var selectedEngine = _settings.SttEngine;
            LocalTranscript localTranscript;
            LocalTranscript? secondaryTranscript = null;
            TranscriptComparisonResult? comparison = null;
            var engineLabel = "Whisper.net / whisper.cpp";
            var modelLabel = _settings.WhisperModel;
            if (selectedEngine == "csharp-whispernet" || (!_pythonReady && selectedEngine != "python-crisperwhisper"))
            {
                localTranscript = await _localStt.TranscribeAsync(audioPath, _settings.WhisperModel, _settings.LanguageMode == "fixed" ? _settings.Language : "auto", progress, token);
            }
            else if (selectedEngine == "python-crisperwhisper")
            {
                if (!_crisperReady) throw new InvalidOperationException("CrisperWhisper 전용 환경이 준비되지 않았습니다. 설정 화면의 설치 상태를 확인하세요.");
                localTranscript = await _pythonStt.TranscribeCrisperAsync(audioPath, _settings.CrisperModel, _settings.Language, _settings.CrisperMode, _settings.CrisperChunkSeconds, progress, token);
                engineLabel = "CrisperWhisper 2.0 / Transformers CPU";
                modelLabel = _settings.CrisperModel;
            }
            else if (selectedEngine == "hybrid-compare")
            {
                var fastTranscript = await _pythonStt.TranscribeAsync(audioPath, _settings.WhisperModel, _settings.Language, _settings.LanguageMode, _settings.AllowedLanguages, contentProfile.Id, _settings.SttQualityProfile, _settings.SttBeamSize, _settings.SttVocabulary, _settings.UseCustomVocabulary, _settings.EnableHallucinationGuard, speakerMode, speakerCount, SettingsService.UnprotectApiKey(_settings.ProtectedHuggingFaceToken), progress, token);
                localTranscript = fastTranscript;
                engineLabel = "이중 검증 / faster-whisper + CrisperWhisper 2.0";
                modelLabel = $"{_settings.WhisperModel} + {_settings.CrisperModel}";
                if (_crisperReady)
                {
                    try
                    {
                        var crisperTranscript = await _pythonStt.TranscribeCrisperAsync(audioPath, _settings.CrisperModel, _settings.Language, _settings.CrisperMode, _settings.CrisperChunkSeconds, progress, token);
                        if (crisperTranscript.Segments.Count > 0)
                        {
                            localTranscript = crisperTranscript;
                            secondaryTranscript = fastTranscript;
                            localTranscript.ProcessingSeconds += fastTranscript.ProcessingSeconds;
                            localTranscript.RealtimeFactor = duration.TotalSeconds / Math.Max(localTranscript.ProcessingSeconds, 0.001);
                            comparison = TranscriptComparisonService.Compare(localTranscript.Segments, secondaryTranscript.Segments);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        localTranscript.Warnings.Add($"Crisper 비교 전사 실패 · faster-whisper 결과로 안전하게 완료: {ex.Message}");
                        engineLabel += " (Crisper 실패 · 빠른 결과 보존)";
                    }
                }
                else localTranscript.Warnings.Add("Crisper 전용 환경이 없어 faster-whisper 결과만 저장했습니다.");
            }
            else
            {
                localTranscript = await _pythonStt.TranscribeAsync(audioPath, _settings.WhisperModel, _settings.Language, _settings.LanguageMode, _settings.AllowedLanguages, contentProfile.Id, _settings.SttQualityProfile, _settings.SttBeamSize, _settings.SttVocabulary, _settings.UseCustomVocabulary, _settings.EnableHallucinationGuard, speakerMode, speakerCount, SettingsService.UnprotectApiKey(_settings.ProtectedHuggingFaceToken), progress, token);
                engineLabel = "Python faster-whisper / CTranslate2";
            }
            var transcript = localTranscript.Text;
            if (localTranscript.Segments.Count == 0) throw new InvalidOperationException("음성을 인식하지 못했습니다. 입력 장치와 오디오 음량을 확인하세요.");
            TranscriptBox.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#172033"));
            TranscriptBox.Text = transcript;
            TranscriptCount.Text = $"{transcript.Length:N0}자";
            LiveDraftStatusText.Text = "실시간 저장본 보존 완료 · 확정 결과는 원문 검토 화면에서 확인";
            var diagnostics = new List<string>();
            if (!string.IsNullOrWhiteSpace(localTranscript.DetectedLanguage)) diagnostics.Add($"감지 언어 {localTranscript.DetectedLanguage} ({localTranscript.LanguageProbability:P0})");
            if (localTranscript.AudioRmsDb != 0) diagnostics.Add($"평균 음량 {localTranscript.AudioRmsDb:0.0} dBFS");
            if (!string.IsNullOrWhiteSpace(localTranscript.LanguageConstraintWarning)) diagnostics.Add(localTranscript.LanguageConstraintWarning);
            if (!string.IsNullOrWhiteSpace(localTranscript.AudioQualityWarning)) diagnostics.Add(localTranscript.AudioQualityWarning);
            if (!string.IsNullOrWhiteSpace(localTranscript.DiarizationWarning)) diagnostics.Add(localTranscript.DiarizationWarning);
            diagnostics.AddRange(localTranscript.Warnings);
            if (localTranscript.RealtimeFactor > 0) diagnostics.Add($"처리 속도 {localTranscript.RealtimeFactor:0.0}x 실시간 · {localTranscript.ProcessingSeconds:0.0}초");
            if (comparison is not null) diagnostics.Add(comparison.Summary);
            TranscriptCount.ToolTip = diagnostics.Count == 0 ? "STT 처리 완료" : string.Join(Environment.NewLine, diagnostics);
            ComparisonSummaryText.Text = comparison?.Summary ?? "단일 엔진 전사입니다. 설정에서 '이중 검증'을 선택하면 두 결과를 시간 구간별로 비교합니다.";
            SecondaryTranscriptBox.Text = secondaryTranscript?.Text ?? "보조 전사 결과 없음";

            var fullRange = startSeconds <= 0 && Math.Abs(endSeconds - duration.TotalSeconds) < 1.5;
            var rangeLabel = fullRange ? $"전체 원문 ({FormatClock(duration)})" : $"{FormatClock(TimeSpan.FromSeconds(startSeconds))} ~ {FormatClock(TimeSpan.FromSeconds(endSeconds))}";
            var checkpoint = _activeRecord is not null && string.Equals(_activeRecord.AudioPath, audioPath, StringComparison.OrdinalIgnoreCase)
                ? _activeRecord
                : null;
            var record = new MeetingRecord
            {
                Id = checkpoint?.Id ?? Guid.NewGuid(),
                Title = string.IsNullOrWhiteSpace(MeetingTitleBox.Text) ? $"회의 {DateTime.Now:yyyy-MM-dd HH:mm}" : MeetingTitleBox.Text.Trim(),
                MeetingType = GetMeetingType(), StartedAt = _preparedAudioIsImported || _recordingStarted == default ? DateTime.Now : _recordingStarted,
                Duration = duration, AudioPath = audioPath, Transcript = transcript, RawTranscript = transcript,
                LiveDraftTranscript = checkpoint?.LiveDraftTranscript ?? _liveDraftText.ToString(),
                LiveDraftUpdatedAt = checkpoint?.LiveDraftUpdatedAt,
                TranscriptSegments = localTranscript.Segments,
                SecondaryTranscript = secondaryTranscript?.Text ?? string.Empty,
                SecondaryTranscriptSegments = secondaryTranscript?.Segments ?? [],
                SttComparisonSummary = comparison?.Summary ?? string.Empty,
                SttDisagreementCount = comparison?.DisagreementCount ?? 0,
                SttProcessingSeconds = localTranscript.ProcessingSeconds,
                SttRealtimeFactor = localTranscript.RealtimeFactor,
                SttWarnings = string.Join(Environment.NewLine, localTranscript.Warnings),
                SttEngine = engineLabel,
                SttModel = modelLabel, SttQualityProfile = _settings.SttQualityProfile,
                LiveDraftModel = _settings.EnableLiveDraft ? _settings.LiveDraftModel : string.Empty,
                SpeakerDiarizationMode = speakerMode, ExpectedSpeakerCount = speakerCount,
                DetectedSpeakerCount = localTranscript.DetectedSpeakerCount, DiarizationStatus = localTranscript.DiarizationStatus,
                DiarizationWarning = localTranscript.DiarizationWarning,
                ContentProfileId = contentProfile.Id, ContentProfileName = contentProfile.Name,
                LanguageMode = _settings.LanguageMode, PrimaryLanguage = _settings.Language,
                DetectedLanguage = localTranscript.DetectedLanguage, DetectedLanguageProbability = localTranscript.LanguageProbability,
                LanguageConstraintWarning = localTranscript.LanguageConstraintWarning, AudioQualityWarning = localTranscript.AudioQualityWarning,
                AudioRmsDb = localTranscript.AudioRmsDb, AudioPeakDb = localTranscript.AudioPeakDb,
                AiOrganizationMode = aiMode, AiProvider = _settings.AiProvider, AiModel = _settings.Model, AiSourceRange = rangeLabel,
                AiPromptVersion = GeminiService.PromptVersion,
                AiRangeStartMinute = (int)(startSeconds / 60), AiRangeEndMinute = (int)Math.Ceiling(endSeconds / 60),
                AiRangeStartSeconds = startSeconds, AiRangeEndSeconds = endSeconds,
                ReportTemplateId = reportTemplate.Id, ReportTemplateName = reportTemplate.Name,
                AiStatus = aiMode == "사용 안 함" ? "사용 안 함" : _settings.RequireTranscriptReviewBeforeAi ? "검토 대기" : "대기",
                ProcessingStatus = "STT 완료", CompletedAt = DateTime.Now, TextEncoding = "UTF-8",
                AudioSource = _preparedAudioIsImported ? "오디오 파일 가져오기" : GetComboTag(AudioSourceBox, "microphone") == "loopback" ? "시스템 소리 (WASAPI 루프백)" : "마이크"
            };
            await Task.Run(() => _repository.Save(record), token);
            _activeRecord = record;
            SaveTranscriptButton.IsEnabled = true;
            GenerateAiButton.IsEnabled = aiMode != "사용 안 함";
            ReviewStatusText.Text = localTranscript.DiarizationStatus is "실패" or "미설치" or "미실행"
                ? $"전체 음성 확정본 · 화자 분리 {localTranscript.DiarizationStatus} · 선택 범위는 AI 보고서에만 적용"
                : comparison is not null ? $"전체 음성 확정본 · {comparison.Summary}" : "전체 음성 확정본입니다 · 내용을 확인한 뒤 저장하세요";
            SummaryBox.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#172033"));
            if (!_settings.RequireTranscriptReviewBeforeAi && _settings.AutoSummarize && aiMode != "사용 안 함")
            {
                record.TranscriptReviewed = true;
                record.TranscriptReviewedAt = DateTime.Now;
                ProgressText.Text = $"3/3  {GeminiService.GetProviderName(_settings.AiProvider)}가 {rangeLabel}을 '{reportTemplate.Name}' 형식으로 정리하고 있습니다…";
                await GenerateAiReportAsync(record, reportTemplate, token);
                SummaryBox.Text = record.AiNotesText;
                ResultTabs.SelectedIndex = 1;
            }
            else
            {
                record.AiNotesText = aiMode == "사용 안 함"
                    ? "AI 정리를 사용하지 않았습니다. 회의 기록에서 보고서 유형을 선택해 언제든 다시 정리할 수 있습니다."
                    : "정확도 전사가 완료되었습니다. 원문과 화자 구분을 검토·수정한 뒤 '검토본으로 AI 보고서 만들기'를 실행하세요.";
                await Task.Run(() => _repository.Save(record), token);
                SummaryBox.Text = record.AiNotesText;
            }
            ReloadRecords();
            FooterStatus.Text = $"저장 완료 · {record.DisplayDate}";
            ProgressText.Text = record.AiStatus == "검토 대기"
                ? "정확도 전사를 저장했습니다. 원문 검토가 끝날 때까지 외부 AI에는 아무 내용도 보내지 않습니다."
                : record.AiStatus == "대기" ? "STT 원문은 저장했고 AI 보고서는 연결 복구 후 다시 처리합니다." : "로컬 원문과 AI 보고서를 분리해 저장했습니다.";
            await Task.Delay(1200, token);
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "작업을 취소했습니다.";
            FooterStatus.Text = "처리 취소됨";
        }
        catch (Exception ex)
        {
            SaveLiveCheckpoint(duration, "자동 정밀 보정 실패 · 실시간 저장본 보존", true);
            ShowError("자동 정밀 보정에 실패했습니다", $"실시간 전사와 오디오는 이미 저장되어 있습니다. '오디오 파일 가져오기'에서 저장된 WAV를 선택해 다시 시도할 수 있습니다.\n\n{ex.Message}");
        }
        finally
        {
            _processingCts?.Dispose();
            _processingCts = null;
            SetBusy(false, string.Empty);
        }
    }

    private async Task GenerateAiReportAsync(MeetingRecord record, ReportTemplate template, CancellationToken token)
    {
        record.AiAttemptCount++;
        record.AiStatus = "처리 중";
        record.AiLastError = string.Empty;
        try
        {
            var apiKey = GeminiService.GetApiKey(_settings);
            if (string.IsNullOrWhiteSpace(apiKey) && _settings.AiProvider != "compatible") throw new InvalidOperationException($"{GeminiService.GetProviderName(_settings.AiProvider)} API 키가 설정되지 않았습니다.");
            var fullRange = record.AiRangeStartSeconds <= 0.5 &&
                            (record.AiRangeEndSeconds <= 0 || record.Duration <= TimeSpan.Zero || record.AiRangeEndSeconds >= record.Duration.TotalSeconds - 1.5);
            var selectedSegments = record.AiRangeEndSeconds > 0
                ? LocalWhisperService.SelectRangeBySeconds(record.TranscriptSegments, record.AiRangeStartSeconds, record.AiRangeEndSeconds)
                : LocalWhisperService.SelectRange(record.TranscriptSegments, record.AiRangeStartMinute, record.AiRangeEndMinute);
            var sourceText = fullRange && !string.IsNullOrWhiteSpace(record.Transcript)
                ? record.Transcript
                : selectedSegments.Count > 0
                    ? string.Join(Environment.NewLine, selectedSegments.Select(x => $"[{x.Timestamp}] {x.SpeakerPrefix}{x.Text}"))
                    : string.Empty;
            if (string.IsNullOrWhiteSpace(sourceText)) throw new InvalidOperationException("지정한 시간 범위에 전사 내용이 없습니다.");
            _settings.AiOrganizationMode = record.AiOrganizationMode;
            record.AiProvider = _settings.AiProvider;
            record.AiModel = _settings.Model;
            var result = await _gemini.OrganizeAsync(sourceText, record.MeetingType, template, _settings, record.ContentProfileId, token);
            record.Summary = result.Summary;
            record.AiNotesText = result.ReportMarkdown;
            record.ReportTemplateId = template.Id;
            record.ReportTemplateName = template.Name;
            record.AiStatus = "완료";
            record.ProcessingStatus = "완료";
            record.AiUpdatedAt = DateTime.Now;
        }
        catch (OperationCanceledException)
        {
            record.AiStatus = "대기";
            record.AiLastError = "사용자가 작업을 취소했습니다.";
            throw;
        }
        catch (Exception ex)
        {
            record.AiStatus = "대기";
            record.ProcessingStatus = "AI 정리 대기";
            record.AiLastError = ex.Message;
            record.AiNotesText = $"AI 보고서 정리 대기\n\nSTT 원문은 안전하게 저장되었습니다. 선택한 AI 연결이 복구되면 설정의 'AI 대기 작업 다시 처리' 또는 기록의 재정리 버튼을 사용하세요.\n\n마지막 오류: {ex.Message}";
        }
        finally
        {
            await Task.Run(() => _repository.Save(record), CancellationToken.None);
        }
    }

    private async void SaveTranscriptButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeRecord is null) return;
        await SaveActiveTranscriptReviewAsync(_activeRecord);
        ReviewStatusText.Text = "2/3 검토본 저장 완료 · AI 보고서를 만들 준비가 되었습니다";
        FooterStatus.Text = "수정한 전사 검토본을 로컬에 저장했습니다";
    }

    private async void GenerateAiButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _activeRecord is null) return;
        var record = _activeRecord;
        if (record.AiOrganizationMode == "사용 안 함")
        {
            ShowError("AI 정리가 꺼져 있습니다", "회의 설정에서 정리 수준을 간단·표준·상세 중 하나로 선택하세요.");
            return;
        }
        _processingCts = new CancellationTokenSource();
        try
        {
            await SaveActiveTranscriptReviewAsync(record);
            var template = HomeReportTemplateBox.SelectedItem as ReportTemplate ?? ReportTemplateCatalog.Get(record.ReportTemplateId);
            SetBusy(true, $"3/3 검토한 원문을 '{template.Name}' 형식으로 정리하고 있습니다…");
            await GenerateAiReportAsync(record, template, _processingCts.Token);
            SummaryBox.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#172033"));
            SummaryBox.Text = record.AiNotesText;
            ResultTabs.SelectedIndex = 1;
            ReviewStatusText.Text = record.AiStatus == "완료" ? "3/3 AI 보고서 완료 · 원문과 별도로 저장됨" : "AI 연결 대기 · 검토본은 안전하게 저장됨";
            ReloadRecords();
        }
        catch (OperationCanceledException) { FooterStatus.Text = "AI 보고서 생성을 취소했습니다"; }
        finally
        {
            _processingCts?.Dispose();
            _processingCts = null;
            SetBusy(false, string.Empty);
        }
    }

    private async Task SaveActiveTranscriptReviewAsync(MeetingRecord record)
    {
        var reviewedText = TranscriptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(reviewedText)) throw new InvalidOperationException("검토한 원문이 비어 있습니다.");
        record.Transcript = reviewedText;
        var parsedSegments = ParseReviewedSegments(reviewedText, record.Duration);
        if (parsedSegments.Count > 0) record.TranscriptSegments = parsedSegments;
        record.TranscriptReviewed = true;
        record.TranscriptReviewedAt = DateTime.Now;
        if (record.AiStatus == "검토 대기") record.AiStatus = "준비됨";
        record.ProcessingStatus = "원문 검토 완료";
        await Task.Run(() => _repository.Save(record));
    }

    private static List<TranscriptSegment> ParseReviewedSegments(string transcript, TimeSpan duration)
    {
        var parsed = new List<TranscriptSegment>();
        var pattern = new Regex(@"^\[(?<time>\d{1,2}:\d{2}(?::\d{2})?)\]\s*(?:화자\s+(?<speaker>[^:]+):\s*)?(?<text>.+)$");
        foreach (var line in transcript.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = pattern.Match(line);
            if (!match.Success || !TimeSpan.TryParse(match.Groups["time"].Value, out var start)) continue;
            parsed.Add(new TranscriptSegment
            {
                Start = start,
                Speaker = match.Groups["speaker"].Value.Trim(),
                Text = match.Groups["text"].Value.Trim()
            });
        }
        for (var i = 0; i < parsed.Count; i++)
            parsed[i].End = i + 1 < parsed.Count ? parsed[i + 1].Start : duration > parsed[i].Start ? duration : parsed[i].Start + TimeSpan.FromSeconds(5);
        return parsed;
    }

    private async Task<int> RetryPendingAiCoreAsync(CancellationToken token)
    {
        var pending = _repository.LoadAll().Where(x => x.AiStatus == "대기" && !string.IsNullOrWhiteSpace(x.RawTranscript)).ToList();
        var completed = 0;
        for (var i = 0; i < pending.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            ProgressText.Text = $"AI 대기 작업 처리 중 {i + 1}/{pending.Count} · {pending[i].Title}";
            await GenerateAiReportAsync(pending[i], ReportTemplateCatalog.Get(pending[i].ReportTemplateId), token);
            if (pending[i].AiStatus == "완료") completed++;
        }
        ReloadRecords();
        return completed;
    }

    private async void RetryPendingAiButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        _processingCts = new CancellationTokenSource();
        SetBusy(true, "AI 대기 작업을 확인하고 있습니다…");
        try
        {
            var count = await RetryPendingAiCoreAsync(_processingCts.Token);
            SettingsMessage.Text = count == 0 ? "처리할 AI 대기 작업이 없습니다." : $"AI 대기 작업 {count}건을 완료했습니다.";
        }
        catch (OperationCanceledException) { SettingsMessage.Text = "대기 작업 처리를 취소했습니다."; }
        finally
        {
            _processingCts.Dispose();
            _processingCts = null;
            SetBusy(false, string.Empty);
        }
    }

    private async void RegenerateAiButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || RecordsList.SelectedItem is not MeetingRecord record) return;
        try
        {
            var start = ParseClock(RecordRangeStartBox.Text, "정리 시작 위치");
            var end = ParseClock(RecordRangeEndBox.Text, "정리 종료 위치");
            if (end <= start || (record.Duration > TimeSpan.Zero && end > record.Duration)) throw new ArgumentException("정리 범위는 전체 오디오 길이 안에서 시작보다 종료가 뒤여야 합니다.");
            var template = RecordTemplateBox.SelectedItem as ReportTemplate ?? ReportTemplateCatalog.Get(record.ReportTemplateId);
            var profile = GetProfileForTemplate(template.Id, record.ContentProfileId);
            record.ContentProfileId = profile.Id;
            record.ContentProfileName = profile.Name;
            record.AiRangeStartSeconds = start.TotalSeconds;
            record.AiRangeEndSeconds = end.TotalSeconds;
            record.AiRangeStartMinute = (int)(start.TotalMinutes);
            record.AiRangeEndMinute = (int)Math.Ceiling(end.TotalMinutes);
            record.AiSourceRange = start == TimeSpan.Zero && Math.Abs((end - record.Duration).TotalSeconds) < 1.5 ? $"전체 원문 ({FormatClock(record.Duration)})" : $"{FormatClock(start)} ~ {FormatClock(end)}";
            record.AiOrganizationMode = "상세 회의록";
            _processingCts = new CancellationTokenSource();
            SetBusy(true, $"'{template.Name}' 형식으로 다시 정리하고 있습니다…");
            await GenerateAiReportAsync(record, template, _processingCts.Token);
            RecordSummaryBox.Text = record.AiNotesText;
            RecordDetailMeta.Text = $"{record.DisplayDate} · {record.ReportTemplateName} · AI {record.AiStatus}";
            ReloadRecords();
            RecordsList.SelectedItem = _records.FirstOrDefault(x => x.Id == record.Id);
        }
        catch (ArgumentException ex) { ShowError("시간 범위를 확인하세요", ex.Message); }
        catch (OperationCanceledException) { FooterStatus.Text = "AI 재정리를 취소했습니다."; }
        finally
        {
            _processingCts?.Dispose();
            _processingCts = null;
            SetBusy(false, string.Empty);
        }
    }

    private void DemoButton_Click(object sender, RoutedEventArgs e)
    {
        var transcript = "[00:00] 김과장: 신제품 출시 일정을 8월 12일로 확정하겠습니다.\n[00:12] 이대리: 제품 소개 자료 초안은 제가 7월 25일까지 작성하겠습니다.\n[00:25] 박팀장: 법무 검토 일정이 아직 미정입니다. 다음 회의 전까지 담당자를 확인해 주세요.\n[00:39] 김과장: 좋습니다. 다음 회의는 7월 28일 오전 10시로 잡겠습니다.";
        var summary = new MeetingSummary
        {
            Overview = "신제품 출시 일정과 준비 업무를 점검했습니다. 출시일은 8월 12일로 확정되었고, 소개 자료와 법무 검토가 후속 과제로 남았습니다.",
            Topics = ["신제품 출시 일정", "제품 소개 자료", "법무 검토"],
            Decisions = ["신제품 출시일을 8월 12일로 확정", "다음 회의를 7월 28일 오전 10시에 진행"],
            ActionItems = [new ActionItem { Task = "제품 소개 자료 초안 작성", Owner = "이대리", DueDate = "7월 25일" }, new ActionItem { Task = "법무 검토 담당자 확인", Owner = "미정", DueDate = "다음 회의 전" }],
            OpenQuestions = ["법무 검토 담당자는 누구인가?"]
        };
        TranscriptBox.Text = transcript; TranscriptBox.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#172033")); TranscriptCount.Text = $"{transcript.Length:N0}자";
        SummaryBox.Text = FormatSummary(summary); SummaryBox.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#172033"));
        WorkspaceTabs.SelectedIndex = 1;
        ResultTabs.SelectedIndex = 0;
        FooterStatus.Text = "데모 데이터가 준비되었습니다";
    }

    private void ResetRecordingControls()
    {
        RecordButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        ImportButton.IsEnabled = true;
        DeviceBox.IsEnabled = true;
        RecordingStatus.Text = "녹음 완료";
        RecordingDot.Fill = new SolidColorBrush(Color.FromRgb(53, 164, 111));
        RecordingHint.Text = "녹음 파일이 안전하게 저장되었습니다";
        LevelMeter.Value = 0;
        UpdateAudioSourceUi();
    }

    private void SetBusy(bool busy, string message)
    {
        _isBusy = busy;
        ProgressBanner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ProgressText.Text = message;
        ProcessingProgressBar.IsIndeterminate = busy;
        ProcessingProgressBar.Value = 0;
        var loopback = GetComboTag(AudioSourceBox, "microphone") == "loopback";
        RecordButton.IsEnabled = !busy && (loopback || _pythonDevices.Count > 0 || AudioRecorder.GetInputDevices().Count > 0);
        ImportButton.IsEnabled = !busy;
        CancelProcessingButton.IsEnabled = busy;
        FooterStatus.Text = busy ? "음성·AI 처리 중" : FooterStatus.Text;
    }

    private void UpdateTimer()
    {
        var elapsed = DateTime.Now - _recordingStarted;
        TimerText.Text = elapsed.ToString(@"hh\:mm\:ss");
    }

    private void AppendLiveDraft(LiveDraftSegment segment)
    {
        var cleanText = TranscriptTextSanitizer.SanitizeLiveSegment(segment.Text);
        if (string.IsNullOrWhiteSpace(cleanText) || string.Equals(cleanText, _lastLiveSegmentText, StringComparison.OrdinalIgnoreCase)) return;
        _lastLiveSegmentText = cleanText;
        var currentText = LiveDraftBox.Text.TrimEnd();
        _liveDraftText.Clear();
        _liveDraftText.Append(currentText);
        var line = $"[{segment.Start.ToString(@"hh\:mm\:ss")}]  {cleanText}";
        if (_liveDraftText.Length > 0) _liveDraftText.AppendLine();
        _liveDraftText.Append(line);
        _suppressLiveDraftSync = true;
        LiveDraftBox.Text = _liveDraftText.ToString();
        _suppressLiveDraftSync = false;
        LiveDraftBox.ScrollToEnd();
        LiveDraftStatusText.Text = "앞에서부터 표시·편집·자동 저장 · 종료 후 정밀 보정";
        SaveLiveCheckpoint(DateTime.Now - _recordingStarted, "녹음 중 · 실시간 저장", false);
    }

    private void LiveDraftBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressLiveDraftSync || _activeRecord is null) return;
        _liveDraftText.Clear();
        _liveDraftText.Append(LiveDraftBox.Text);
        LiveDraftStatusText.Text = "수정한 초안 자동 저장 중 · 새 전사는 아래에 이어집니다";
        SaveLiveCheckpoint(DateTime.Now - _recordingStarted, "녹음 중 · 사용자 수정본 저장", false);
    }

    private MeetingRecord CreateRecordingCheckpoint(string audioPath, DateTime startedAt)
    {
        var profile = TranscriptionProfileCatalog.Get(GetContentProfileId());
        return new MeetingRecord
        {
            Title = string.IsNullOrWhiteSpace(MeetingTitleBox.Text) ? $"회의 {startedAt:yyyy-MM-dd HH:mm}" : MeetingTitleBox.Text.Trim(),
            MeetingType = GetMeetingType(),
            StartedAt = startedAt,
            AudioPath = audioPath,
            ProcessingStatus = "녹음 중 · 자동 저장",
            AiStatus = "녹음 중",
            SttEngine = _settings.SttEngine switch { "python-crisperwhisper" => "CrisperWhisper 2.0 / Transformers CPU", "hybrid-compare" => "이중 검증 / faster-whisper + CrisperWhisper 2.0", "csharp-whispernet" => "Whisper.net / whisper.cpp", _ => "Python faster-whisper / CTranslate2" },
            SttModel = _settings.SttEngine is "python-crisperwhisper" ? _settings.CrisperModel : _settings.SttEngine is "hybrid-compare" ? $"{_settings.WhisperModel} + {_settings.CrisperModel}" : _settings.WhisperModel,
            SttQualityProfile = _settings.SttQualityProfile,
            LiveDraftModel = _settings.EnableLiveDraft ? _settings.LiveDraftModel : string.Empty,
            ContentProfileId = profile.Id,
            ContentProfileName = profile.Name,
            LanguageMode = _settings.LanguageMode,
            PrimaryLanguage = _settings.Language,
            AudioSource = GetComboTag(AudioSourceBox, "microphone") == "loopback" ? "시스템 소리 (WASAPI 루프백)" : "마이크"
        };
    }

    private void SaveLiveCheckpoint(TimeSpan duration, string status, bool force)
    {
        if (_activeRecord is null || !string.Equals(_activeRecord.AudioPath, _currentAudioPath, StringComparison.OrdinalIgnoreCase)) return;
        _activeRecord.Duration = duration;
        _activeRecord.LiveDraftTranscript = _liveDraftText.ToString();
        _activeRecord.LiveDraftUpdatedAt = DateTime.Now;
        _activeRecord.ProcessingStatus = status;
        if (status.Contains("실패", StringComparison.Ordinal)) _activeRecord.AiStatus = "STT 재처리 필요";
        else if (status.Contains("정밀 보정 중", StringComparison.Ordinal)) _activeRecord.AiStatus = "STT 처리 중";
        if (!force && DateTime.Now - _lastLiveCheckpointSavedAt < TimeSpan.FromSeconds(5)) return;
        _repository.Save(_activeRecord);
        _lastLiveCheckpointSavedAt = DateTime.Now;
    }

    private static int ParseSpeakerCount(string text)
    {
        return int.TryParse(text, out var value) ? Math.Clamp(value, 1, 12) : 2;
    }

    private string GetMeetingType() => (MeetingTypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "자동·일반 음성";

    private static string FormatSummary(MeetingSummary summary)
    {
        var text = new StringBuilder();
        text.AppendLine("핵심 요약").AppendLine(summary.Overview).AppendLine();
        AppendList(text, "주요 안건", summary.Topics);
        AppendList(text, "결정사항", summary.Decisions);
        text.AppendLine("실행 항목");
        if (summary.ActionItems.Count == 0) text.AppendLine("• 없음");
        foreach (var item in summary.ActionItems) text.AppendLine($"• {item.Task}  |  담당: {item.Owner}  |  기한: {item.DueDate}");
        text.AppendLine();
        AppendList(text, "미해결 질문", summary.OpenQuestions);
        return text.ToString().Trim();
    }

    private static void AppendList(StringBuilder text, string title, IEnumerable<string> values)
    {
        text.AppendLine(title);
        var list = values.ToList();
        if (list.Count == 0) text.AppendLine("• 없음");
        foreach (var value in list) text.AppendLine($"• {value}");
        text.AppendLine();
    }

    private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        int rangeStart;
        int rangeEnd;
        int beamSize;
        int connectionTimeout;
        int speakerCount;
        try
        {
            rangeStart = ParseMinute(AiRangeStartBox.Text, "기본 시작 위치");
            rangeEnd = ParseMinute(AiRangeEndBox.Text, "기본 종료 위치");
            if (rangeEnd > 0 && rangeEnd <= rangeStart) throw new ArgumentException("기본 종료 위치는 시작 위치보다 커야 합니다.");
            if (!int.TryParse(SttBeamSizeBox.Text, out beamSize) || beamSize is < 1 or > 12) throw new ArgumentException("Beam Search 정확도는 1~12 사이로 입력하세요.");
            if (!int.TryParse(GeminiTimeoutBox.Text, out connectionTimeout) || connectionTimeout is < 5 or > 60) throw new ArgumentException("AI 연결 제한시간은 5~60초 사이로 입력하세요.");
            if (!int.TryParse(SpeakerCountBox.Text, out speakerCount) || speakerCount is < 1 or > 12) throw new ArgumentException("예상 화자 수는 1~12명 사이로 입력하세요.");
        }
        catch (ArgumentException ex)
        {
            ShowError("설정값을 확인하세요", ex.Message);
            return;
        }
        var apiKey = ApiKeyBox.Password.Trim();
        var openAiApiKey = OpenAiApiKeyBox.Password.Trim();
        var anthropicApiKey = AnthropicApiKeyBox.Password.Trim();
        var compatibleApiKey = CompatibleApiKeyBox.Password.Trim();
        var aiProvider = GetComboTag(AiProviderBox, "gemini");
        var compatibleEndpoint = CompatibleEndpointBox.Text.Trim();
        if (aiProvider == "compatible" && (!Uri.TryCreate(compatibleEndpoint, UriKind.Absolute, out var endpointUri) || endpointUri.Scheme is not ("http" or "https")))
        {
            ShowError("호환 서버 주소를 확인하세요", "http:// 또는 https://로 시작하는 OpenAI 호환 API 주소를 입력하세요.");
            return;
        }
        var huggingFaceToken = HuggingFaceTokenBox.Password.Trim();
        _settings = new AppSettings
        {
            ProtectedApiKey = string.IsNullOrWhiteSpace(apiKey) ? _settings.ProtectedApiKey : SettingsService.ProtectApiKey(apiKey),
            AiProvider = aiProvider,
            ProtectedOpenAiApiKey = string.IsNullOrWhiteSpace(openAiApiKey) ? _settings.ProtectedOpenAiApiKey : SettingsService.ProtectApiKey(openAiApiKey),
            ProtectedAnthropicApiKey = string.IsNullOrWhiteSpace(anthropicApiKey) ? _settings.ProtectedAnthropicApiKey : SettingsService.ProtectApiKey(anthropicApiKey),
            ProtectedCompatibleApiKey = string.IsNullOrWhiteSpace(compatibleApiKey) ? _settings.ProtectedCompatibleApiKey : SettingsService.ProtectApiKey(compatibleApiKey),
            CompatibleApiEndpoint = string.IsNullOrWhiteSpace(compatibleEndpoint) ? "http://localhost:11434/v1" : compatibleEndpoint,
            Model = string.IsNullOrWhiteSpace(ModelBox.Text) ? "gemini-3.5-flash" : ModelBox.Text.Trim(),
            Language = (LanguageBox.SelectedItem as LanguageOption)?.Code ?? "ko-KR",
            LanguageMode = GetComboTag(LanguageModeBox, "fixed"),
            AllowedLanguages = AllowedLanguagesBox.Text.Trim(),
            ReportLanguage = GetComboTag(ReportLanguageBox, "same"),
            ContentProfile = (ContentProfileBox.SelectedItem as TranscriptionProfile)?.Id ?? TranscriptionProfileCatalog.DefaultId,
            WhisperModel = GetComboTag(WhisperModelBox, "medium"),
            SttEngine = GetComboTag(SttEngineBox, "python-faster-whisper"),
            CrisperModel = GetComboTag(CrisperModelBox, "small"),
            CrisperMode = GetComboTag(CrisperModeBox, "intended"),
            CrisperChunkMinutes = _settings.CrisperChunkMinutes,
            CrisperChunkSeconds = int.Parse(GetComboTag(CrisperChunkSecondsBox, "30")),
            VadProfile = GetComboTag(VadProfileBox, "balanced"),
            KeepDualTranscripts = true,
            EnableLiveDraft = EnableLiveDraftBox.IsChecked == true,
            LiveDraftModel = GetComboTag(LiveDraftModelBox, "base"),
            SttQualityProfile = SttQualityPresetCatalog.Get(_settings.SttQualityProfile).Id,
            SttQualityConfigured = true,
            SttBeamSize = beamSize,
            SttVocabulary = SttVocabularyBox.Text.Trim(),
            UseCustomVocabulary = UseCustomVocabularyBox.IsChecked == true,
            EnableHallucinationGuard = HallucinationGuardBox.IsChecked == true,
            ShowTimelineEditor = ShowTimelineEditorBox.IsChecked == true,
            AutoSelectAvailableGeminiModel = AutoSelectGeminiModelBox.IsChecked == true,
            AutoRetryPendingAi = AutoRetryPendingAiBox.IsChecked == true,
            GeminiConnectionTimeoutSeconds = connectionTimeout,
            Temperature = TemperatureSlider.Value,
            AutoSummarize = AutoSummaryBox.IsChecked == true,
            RequireTranscriptReviewBeforeAi = RequireReviewBox.IsChecked == true,
            SpeakerDiarizationMode = GetComboTag(SpeakerModeBox, "off"),
            SpeakerCount = speakerCount,
            ProtectedHuggingFaceToken = string.IsNullOrWhiteSpace(huggingFaceToken) ? _settings.ProtectedHuggingFaceToken : SettingsService.ProtectApiKey(huggingFaceToken),
            AiOrganizationMode = GetComboTag(AiModeBox, "표준 회의록"),
            DefaultReportTemplateId = (DefaultReportTemplateBox.SelectedItem as ReportTemplate)?.Id ?? ReportTemplateCatalog.DefaultId,
            AiRangeStartMinute = rangeStart,
            AiRangeEndMinute = rangeEnd,
            SummaryPrompt = PromptBox.Text.Trim()
        };
        _settingsService.Save(_settings);
        HomeAiModeBox.SelectedIndex = AiModeBox.SelectedIndex;
        HomeReportTemplateBox.SelectedItem = ReportTemplateCatalog.Get(_settings.DefaultReportTemplateId);
        HomeSpeakerModeBox.SelectedIndex = SpeakerModeBox.SelectedIndex;
        HomeSpeakerCountBox.Text = _settings.SpeakerCount.ToString();
        HomeRangeStartBox.Text = _settings.AiRangeStartMinute.ToString();
        HomeRangeEndBox.Text = _settings.AiRangeEndMinute.ToString();
        WhisperModelStatusText.Text = _localStt.IsModelInstalled(_settings.WhisperModel) ? "선택한 모델이 설치되어 있습니다." : "첫 텍스트화 때 모델을 자동으로 다운로드합니다.";
        RefreshApiStatus();
        ApplyFeatureVisibility();
        SettingsMessage.Foreground = new SolidColorBrush(Color.FromRgb(37, 132, 90));
        SettingsMessage.Text = "설정을 저장했습니다.";
    }

    private async void TestConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsButton_Click(sender, e);
        var apiKey = GeminiService.GetApiKey(_settings);
        var providerName = GeminiService.GetProviderName(_settings.AiProvider);
        if (string.IsNullOrWhiteSpace(apiKey) && _settings.AiProvider != "compatible") { ShowError("API 키가 비어 있습니다", $"{providerName} API 키를 입력하세요."); return; }
        SettingsMessage.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#697386"));
        SettingsMessage.Text = "연결을 확인하고 있습니다…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(_settings.GeminiConnectionTimeoutSeconds, 5, 60)));
            var models = await _gemini.GetAvailableModelsAsync(_settings, apiKey, timeout.Token);
            if (models.Count == 0) throw new InvalidOperationException($"{providerName}에서 사용할 수 있는 모델을 찾지 못했습니다.");
            ModelBox.Items.Clear();
            foreach (var model in models) ModelBox.Items.Add(model);
            if (!models.Contains(_settings.Model, StringComparer.OrdinalIgnoreCase))
            {
                if (!_settings.AutoSelectAvailableGeminiModel || _settings.AiProvider != "gemini")
                    throw new InvalidOperationException($"선택한 모델 '{_settings.Model}'을 이 API 키에서 사용할 수 없습니다. 사용 가능한 모델을 선택하세요.");
                _settings.Model = models.FirstOrDefault(x => x.Equals("gemini-3.5-flash", StringComparison.OrdinalIgnoreCase))
                    ?? models.FirstOrDefault(x => x.Equals("gemini-2.5-flash", StringComparison.OrdinalIgnoreCase))
                    ?? models.First();
                ModelBox.Text = _settings.Model;
                _settingsService.Save(_settings);
            }
            try
            {
                await _gemini.TestAsync(_settings, apiKey);
            }
            catch (InvalidOperationException ex) when (_settings.AiProvider == "gemini" && _settings.AutoSelectAvailableGeminiModel
                && (ex.Message.Contains("503", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("404", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("high demand", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("no longer available", StringComparison.OrdinalIgnoreCase)))
            {
                var candidates = models.Where(x => !x.Equals(_settings.Model, StringComparison.OrdinalIgnoreCase)
                        && x.Contains("flash", StringComparison.OrdinalIgnoreCase)
                        && !x.Contains("image", StringComparison.OrdinalIgnoreCase)
                        && !x.Contains("live", StringComparison.OrdinalIgnoreCase)
                        && !x.Contains("tts", StringComparison.OrdinalIgnoreCase)
                        && !x.Contains("audio", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.Contains("3.5", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(x => x.Contains("3.1", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(x => x.Contains("3", StringComparison.OrdinalIgnoreCase))
                    .Take(3)
                    .ToList();
                Exception lastError = ex;
                var connected = false;
                foreach (var fallback in candidates)
                {
                    _settings.Model = fallback;
                    ModelBox.Text = fallback;
                    SettingsMessage.Text = $"{fallback} 모델로 전환해 다시 확인합니다…";
                    try
                    {
                        await _gemini.TestAsync(_settings, apiKey);
                        connected = true;
                        break;
                    }
                    catch (Exception fallbackError) { lastError = fallbackError; }
                }
                if (!connected) throw lastError;
                _settingsService.Save(_settings);
            }
            SettingsMessage.Foreground = new SolidColorBrush(Color.FromRgb(37, 132, 90));
            var completed = 0;
            if (_settings.AutoRetryPendingAi)
            {
                SettingsMessage.Text = $"연결 성공 · {_settings.Model} · AI 대기 작업 확인 중…";
                completed = await RetryPendingAiCoreAsync(CancellationToken.None);
            }
            SettingsMessage.Text = !_settings.AutoRetryPendingAi ? $"{providerName} 연결 성공 · {_settings.Model}" : completed == 0 ? $"{providerName} 연결에 성공했습니다. AI 대기 작업이 없습니다." : $"{providerName} 연결 성공 · 대기 작업 {completed}건을 정리했습니다.";
            ApiStatusText.Text = "연결됨";
        }
        catch (Exception ex)
        {
            SettingsMessage.Foreground = new SolidColorBrush(Color.FromRgb(180, 35, 24));
            SettingsMessage.Text = $"연결 실패 · {ex.Message}";
            ApiStatusDot.Fill = new SolidColorBrush(Color.FromRgb(220, 38, 38));
            ApiStatusText.Text = "AI 연결 실패 · 설정에서 상세 확인";
        }
    }

    private void ReloadRecords(string? query = null)
    {
        var all = _repository.LoadAll();
        if (!string.IsNullOrWhiteSpace(query))
            all = all.Where(x => x.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || x.RawTranscript.Contains(query, StringComparison.OrdinalIgnoreCase)
                || x.Transcript.Contains(query, StringComparison.OrdinalIgnoreCase)
                || x.AiNotesText.Contains(query, StringComparison.OrdinalIgnoreCase)
                || x.LiveDraftTranscript.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        _records.Clear();
        foreach (var item in all) _records.Add(item);
        RecordsList.ItemsSource = _records;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ReloadRecords(SearchBox.Text.Trim());

    private void AiProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var provider = GetComboTag(AiProviderBox, "gemini");
        ModelBox.Text = provider switch
        {
            "openai" => "gpt-5-mini",
            "anthropic" => "claude-sonnet-4-5",
            "compatible" => "local-model",
            _ => "gemini-3.5-flash"
        };
        SettingsMessage.Text = $"{GeminiService.GetProviderName(provider)}를 선택했습니다. 모델 ID와 해당 API 키를 확인하세요.";
    }

    private void RecordsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecordsList.SelectedItem is not MeetingRecord record) return;
        RecordDetailTitle.Text = record.Title;
        RecordDetailMeta.Text = $"{record.DisplayDate}  ·  {record.MeetingType}  ·  {record.DurationText}  ·  {record.ContentProfileName}  ·  Whisper {record.SttModel}  ·  {record.AiOrganizationMode} ({record.AiSourceRange})";
        var rawTranscript = string.IsNullOrWhiteSpace(record.RawTranscript) ? record.Transcript : record.RawTranscript;
        var aiNotes = string.IsNullOrWhiteSpace(record.AiNotesText) ? FormatSummary(record.Summary) : record.AiNotesText;
        var encodingWarning = rawTranscript.Contains('\uFFFD')
            ? "\n\n주의: 이 기록에는 인코딩 손상 문자(�)가 있습니다. 원본 오디오를 다시 텍스트화해야 복구할 수 있습니다."
            : string.Empty;
        RecordTemplateBox.SelectedItem = ReportTemplateCatalog.Get(record.ReportTemplateId);
        RecordRangeStartBox.Text = FormatClock(TimeSpan.FromSeconds(record.AiRangeStartSeconds > 0 ? record.AiRangeStartSeconds : record.AiRangeStartMinute * 60));
        var recordEnd = record.AiRangeEndSeconds > 0 ? TimeSpan.FromSeconds(record.AiRangeEndSeconds) : record.AiRangeEndMinute > 0 ? TimeSpan.FromMinutes(record.AiRangeEndMinute) : record.Duration;
        RecordRangeEndBox.Text = FormatClock(recordEnd);
        RecordInfoBox.Text = $"제목: {record.Title}\n회의·콘텐츠 유형: {record.MeetingType}\nSTT 콘텐츠 프로필: {record.ContentProfileName}\n시작: {record.StartedAt:yyyy-MM-dd HH:mm:ss}\n완료: {record.CompletedAt:yyyy-MM-dd HH:mm:ss}\n길이: {record.DurationText}\n녹음 소스: {record.AudioSource}\n처리 상태: {record.ProcessingStatus}\nSTT 엔진: {record.SttEngine}\nCPU 품질 프리셋: {SttQualityPresetCatalog.Get(record.SttQualityProfile).Name}\n정확도 모델: {record.SttModel}\n처리 속도: {(record.SttRealtimeFactor > 0 ? $"{record.SttRealtimeFactor:0.0}x 실시간 · {record.SttProcessingSeconds:0.0}초" : "기록 없음")}\n이중 전사 비교: {(string.IsNullOrWhiteSpace(record.SttComparisonSummary) ? "사용 안 함" : record.SttComparisonSummary)}\nSTT 경고: {(string.IsNullOrWhiteSpace(record.SttWarnings) ? "없음" : record.SttWarnings)}\n실시간 임시 자막 모델: {(string.IsNullOrWhiteSpace(record.LiveDraftModel) ? "사용 안 함" : record.LiveDraftModel)}\n전사 검토: {(record.TranscriptReviewed ? $"완료 ({record.TranscriptReviewedAt:yyyy-MM-dd HH:mm:ss})" : "대기")}\n화자 분리: {record.DiarizationStatus} · 방식 {record.SpeakerDiarizationMode} · 감지 {record.DetectedSpeakerCount}명\n화자 분리 진단: {(string.IsNullOrWhiteSpace(record.DiarizationWarning) ? "정상" : record.DiarizationWarning)}\n언어 방식: {record.LanguageMode}\n주 언어: {record.PrimaryLanguage}\n감지 언어: {(string.IsNullOrWhiteSpace(record.DetectedLanguage) ? "고정 또는 정보 없음" : $"{record.DetectedLanguage} ({record.DetectedLanguageProbability:P0})")}\n언어 진단: {(string.IsNullOrWhiteSpace(record.LanguageConstraintWarning) ? "정상" : record.LanguageConstraintWarning)}\n평균/최대 음량: {record.AudioRmsDb:0.0} / {record.AudioPeakDb:0.0} dBFS\n음질 진단: {(string.IsNullOrWhiteSpace(record.AudioQualityWarning) ? "정상" : record.AudioQualityWarning)}\n보고서 유형: {record.ReportTemplateName}\nAI 정리 수준: {record.AiOrganizationMode}\nAI 상태: {record.AiStatus}\nAI 시도 횟수: {record.AiAttemptCount}\nAI 적용 범위: {record.AiSourceRange}\n마지막 AI 오류: {(string.IsNullOrWhiteSpace(record.AiLastError) ? "없음" : record.AiLastError)}\n데이터 버전: {record.DataVersion}\n문자 인코딩: {record.TextEncoding}\n오디오: {record.AudioPath}{encodingWarning}";
        RecordInfoBox.Text += $"\n실시간 저장본: {record.LiveDraftTranscript.Length:N0}자 · 마지막 저장 {record.LiveDraftUpdatedAt:yyyy-MM-dd HH:mm:ss}\nAI 지침 버전: {record.AiPromptVersion}";
        RecordSummaryBox.Text = aiNotes;
        RecordTranscriptBox.Text = string.IsNullOrWhiteSpace(record.Transcript) ? rawTranscript : record.Transcript;
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (RecordsList.SelectedItem is not MeetingRecord record) { ShowError("내보낼 회의를 선택하세요", "왼쪽 목록에서 회의 기록을 먼저 선택하세요."); return; }
        var dialog = new SaveFileDialog { Filter = "Markdown 문서|*.md", FileName = $"{SanitizeFileName(record.Title)}.md" };
        if (dialog.ShowDialog(this) != true) return;
        var aiNotes = string.IsNullOrWhiteSpace(record.AiNotesText) ? FormatSummary(record.Summary) : record.AiNotesText;
        var rawTranscript = string.IsNullOrWhiteSpace(record.RawTranscript) ? record.Transcript : record.RawTranscript;
        var reviewedTranscript = string.IsNullOrWhiteSpace(record.Transcript) ? rawTranscript : record.Transcript;
        var content = $"# {record.Title}\n\n- 일시: {record.DisplayDate}\n- 유형: {record.MeetingType}\n- 길이: {record.DurationText}\n- STT: {record.SttEngine} / {record.SttModel}\n- 전사 검토: {(record.TranscriptReviewed ? "완료" : "대기")}\n- 화자 분리: {record.DiarizationStatus} / {record.DetectedSpeakerCount}명\n- AI 정리: {record.AiOrganizationMode} / {record.AiSourceRange}\n\n## AI 회의 노트\n\n{aiNotes}\n\n## 검토한 전사본\n\n{reviewedTranscript}\n\n## 변경되지 않는 STT 원본\n\n{rawTranscript}\n";
        if (!string.IsNullOrWhiteSpace(record.LiveDraftTranscript))
            content += $"\n## 녹음 중 자동 저장된 실시간 전사\n\n{record.LiveDraftTranscript}\n";
        if (!string.IsNullOrWhiteSpace(record.SecondaryTranscript))
            content += $"\n## 보조 STT 교차 검증본\n\n{record.SttComparisonSummary}\n\n{record.SecondaryTranscript}\n";
        File.WriteAllText(dialog.FileName, content, new UTF8Encoding(false));
        FooterStatus.Text = $"내보내기 완료 · {dialog.FileName}";
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (RecordsList.SelectedItem is not MeetingRecord record) return;
        if (MessageBox.Show(this, $"'{record.Title}' 기록을 삭제할까요?\n원본 오디오 파일은 삭제하지 않습니다.", "기록 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _repository.Delete(record.Id);
        ReloadRecords();
        RecordDetailTitle.Text = "회의를 선택하세요"; RecordDetailMeta.Text = "삭제가 완료되었습니다."; RecordInfoBox.Clear(); RecordSummaryBox.Clear(); RecordTranscriptBox.Clear();
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? "meeting-notes" : name;
    }

    private static string GetComboTag(ComboBox box, string fallback) => (box.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private string GetContentProfileId()
    {
        var type = GetMeetingType();
        if (type.Contains("드라마", StringComparison.OrdinalIgnoreCase) || type.Contains("영상", StringComparison.OrdinalIgnoreCase) || type.Contains("팟캐스트", StringComparison.OrdinalIgnoreCase)) return "media";
        if (type.Contains("기술", StringComparison.OrdinalIgnoreCase) || type.Contains("설계", StringComparison.OrdinalIgnoreCase)) return "technical";
        if (type.Contains("인터뷰", StringComparison.OrdinalIgnoreCase) || type.Contains("면접", StringComparison.OrdinalIgnoreCase)) return "interview";
        if (type.Contains("고객", StringComparison.OrdinalIgnoreCase) || type.Contains("영업", StringComparison.OrdinalIgnoreCase)) return "customer";
        if (type.Contains("뉴스", StringComparison.OrdinalIgnoreCase) || type.Contains("미디어", StringComparison.OrdinalIgnoreCase)) return "news";
        if (type.Contains("강의", StringComparison.OrdinalIgnoreCase) || type.Contains("교육", StringComparison.OrdinalIgnoreCase)) return "lecture";
        if (type.Contains("법률", StringComparison.OrdinalIgnoreCase) || type.Contains("규정", StringComparison.OrdinalIgnoreCase)) return "legal";
        if (type.Contains("업무", StringComparison.OrdinalIgnoreCase) || type.Contains("프로젝트", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("사내", StringComparison.OrdinalIgnoreCase) || type.Contains("업체", StringComparison.OrdinalIgnoreCase) || type.Contains("미팅", StringComparison.OrdinalIgnoreCase)) return "business";
        return TranscriptionProfileCatalog.DefaultId;
    }

    private static TranscriptionProfile GetProfileForTemplate(string templateId, string currentProfileId)
    {
        var profileId = templateId switch
        {
            "news-brief" => "news",
            "technical-review" => "technical",
            "interview-evaluation" => "interview",
            "training-notes" => "lecture",
            "customer-sales" => "customer",
            "project-status" or "executive-report" or "decision-brief" or "internal-meeting" or "vendor-meeting" => "business",
            "multilingual-brief" => currentProfileId,
            _ => currentProfileId
        };
        return TranscriptionProfileCatalog.Get(profileId);
    }

    private static int ParseMinute(string text, string fieldName)
    {
        if (!int.TryParse(text, out var value) || value < 0) throw new ArgumentException($"{fieldName}에는 0 이상의 분 단위를 입력하세요.");
        return value;
    }

    private static TimeSpan ParseClock(string text, string fieldName)
    {
        var value = text.Trim();
        if (TimeSpan.TryParse(value, out var time) && time >= TimeSpan.Zero) return time;
        if (double.TryParse(value, out var seconds) && seconds >= 0) return TimeSpan.FromSeconds(seconds);
        throw new ArgumentException($"{fieldName}은 HH:MM:SS 또는 초 단위로 입력하세요.");
    }

    private static string FormatClock(TimeSpan value) => value.ToString(@"hh\:mm\:ss");

    private void ShowError(string title, string message) => MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
}
