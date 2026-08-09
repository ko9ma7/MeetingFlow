using System.Diagnostics;
using System.Text.Json;
using MeetingFlow.App.Models;

namespace MeetingFlow.App.Services;

public sealed record PythonAudioDevice(int Index, string Name);
public sealed record PythonHealth(string Python, bool PyAudio, bool FasterWhisper, bool Pyannote)
{
    public bool Ready => PyAudio && FasterWhisper;
}
public sealed record CrisperHealth(string Python, bool CrisperWhisper);
public sealed record LiveDraftSegment(TimeSpan Start, TimeSpan End, string Text);

public sealed class PythonSttService : IDisposable
{
    private readonly string _scriptPath;
    private readonly string _pythonPath;
    private readonly string _crisperPythonPath;
    private Process? _recordingProcess;
    private Task? _recordingReader;
    private Process? _liveProcess;
    private Task? _liveReader;
    private readonly SemaphoreSlim _liveInputLock = new(1, 1);
    private int _liveChunkOutstanding;
    private int _liveChunksSkipped;
    public event EventHandler<float>? LevelChanged;
    public event EventHandler<LiveDraftSegment>? LiveDraftReceived;
    public event EventHandler<string>? LiveDraftStatusChanged;
    public bool IsRecording => _recordingProcess is { HasExited: false };
    public bool IsLivePreviewRunning => _liveProcess is { HasExited: false };

    public bool IsModelPrepared(string modelName)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetingFlow", "Models", "faster-whisper");
        if (!Directory.Exists(folder)) return false;
        return Directory.EnumerateFiles(folder, "model.bin", SearchOption.AllDirectories)
            .Any(path => path.Contains(modelName, StringComparison.OrdinalIgnoreCase) && new FileInfo(path).Length > 1_000_000);
    }

    public PythonSttService()
    {
        var outputScript = Path.Combine(AppContext.BaseDirectory, "python-stt", "meetingflow_stt.py");
        var workspaceFolder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "python-stt"));
        _scriptPath = File.Exists(outputScript) ? outputScript : Path.Combine(workspaceFolder, "meetingflow_stt.py");
        var workspacePython = Path.Combine(workspaceFolder, ".venv", "Scripts", "python.exe");
        var outputPython = Path.Combine(AppContext.BaseDirectory, "python-stt", ".venv", "Scripts", "python.exe");
        _pythonPath = File.Exists(workspacePython) ? workspacePython : File.Exists(outputPython) ? outputPython : "py";
        var workspaceCrisperPython = Path.Combine(workspaceFolder, ".crisper-venv", "Scripts", "python.exe");
        var outputCrisperPython = Path.Combine(AppContext.BaseDirectory, "python-stt", ".crisper-venv", "Scripts", "python.exe");
        _crisperPythonPath = File.Exists(workspaceCrisperPython) ? workspaceCrisperPython : File.Exists(outputCrisperPython) ? outputCrisperPython : string.Empty;
    }

    public async Task<PythonHealth> GetHealthAsync(CancellationToken token = default)
    {
        using var document = await RunSingleEventAsync("health", token);
        var root = document.RootElement;
        return new PythonHealth(
            root.TryGetProperty("python", out var python) ? python.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("pyaudio", out var pyaudio) && pyaudio.GetBoolean(),
            root.TryGetProperty("faster_whisper", out var whisper) && whisper.GetBoolean(),
            root.TryGetProperty("pyannote", out var pyannote) && pyannote.GetBoolean());
    }

    public async Task<CrisperHealth> GetCrisperHealthAsync(CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(_crisperPythonPath)) return new(string.Empty, false);
        using var document = await RunSingleEventAsync("crisper-health", token, _crisperPythonPath);
        var root = document.RootElement;
        return new(
            root.TryGetProperty("python", out var python) ? python.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("crisperwhisper", out var crisper) && crisper.GetBoolean());
    }

    public async Task<IReadOnlyList<PythonAudioDevice>> GetDevicesAsync(CancellationToken token = default)
    {
        using var document = await RunSingleEventAsync("devices", token);
        return document.RootElement.GetProperty("devices").EnumerateArray()
            .Select(x => new PythonAudioDevice(x.GetProperty("index").GetInt32(), x.GetProperty("name").GetString() ?? "입력 장치"))
            .ToList();
    }

    public async Task StartRecordingAsync(string outputPath, int deviceIndex, bool livePreview, CancellationToken token = default)
    {
        if (IsRecording) return;
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var arguments = new List<string> { "record", "--output", outputPath, "--device", deviceIndex.ToString() };
        if (livePreview) arguments.Add("--live-preview");
        var info = CreateStartInfo(arguments.ToArray());
        _recordingProcess = Process.Start(info) ?? throw new InvalidOperationException("Python 녹음 프로세스를 시작하지 못했습니다.");
        _ = _recordingProcess.StandardError.ReadToEndAsync(token);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _recordingReader = Task.Run(async () =>
        {
            while (await _recordingProcess.StandardOutput.ReadLineAsync(token) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var eventName = root.GetProperty("event").GetString();
                if (eventName == "recording_started") started.TrySetResult();
                else if (eventName == "level") LevelChanged?.Invoke(this, root.GetProperty("value").GetSingle());
                else if (eventName == "preview_chunk")
                    await SubmitLiveChunkAsync(root.GetProperty("path").GetString() ?? string.Empty, root.GetProperty("start").GetDouble(), token);
                else if (eventName == "error") started.TrySetException(new InvalidOperationException(root.GetProperty("message").GetString()));
            }
        }, token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
    }

    public async Task StopRecordingAsync(CancellationToken token = default)
    {
        if (!IsRecording || _recordingProcess is null) return;
        await _recordingProcess.StandardInput.WriteLineAsync("stop");
        await _recordingProcess.StandardInput.FlushAsync();
        await _recordingProcess.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(10), token);
        if (_recordingReader is not null) await _recordingReader;
        _recordingProcess.Dispose();
        _recordingProcess = null;
    }

    public async Task<LocalTranscript> TranscribeAsync(string audioPath, string modelName, string language, string languageMode, string allowedLanguages, string contentProfileId, string qualityProfile, int beamSize, string vocabulary, bool useCustomVocabulary, bool hallucinationGuard, string diarizationMode, int speakerCount, string huggingFaceToken, IProgress<SttProgress>? progress = null, CancellationToken token = default)
    {
        var modelFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetingFlow", "Models", "faster-whisper");
        Directory.CreateDirectory(modelFolder);
        var initialPrompt = useCustomVocabulary && !string.IsNullOrWhiteSpace(vocabulary)
            ? $"중요 용어: {vocabulary.Trim()}"
            : string.Empty;
        var arguments = new List<string>
        {
            "transcribe", "--input", audioPath, "--model", modelName, "--language", LanguageCatalog.ToWhisperCode(language),
            "--language-mode", languageMode, "--model-dir", modelFolder, "--beam-size", Math.Clamp(beamSize, 1, 12).ToString(),
            "--initial-prompt", initialPrompt, "--hotwords", useCustomVocabulary ? vocabulary.Trim() : string.Empty,
            "--content-profile", contentProfileId, "--quality-profile", SttQualityPresetCatalog.Get(qualityProfile).Id, "--diarization-mode", diarizationMode,
            "--speaker-count", Math.Clamp(speakerCount, 1, 12).ToString()
        };
        if (hallucinationGuard) arguments.Add("--hallucination-guard");
        var info = CreateStartInfo(arguments.ToArray());
        if (!string.IsNullOrWhiteSpace(huggingFaceToken)) info.Environment["MEETINGFLOW_HF_TOKEN"] = huggingFaceToken;
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Python STT 프로세스를 시작하지 못했습니다.");
        using var cancellation = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch { }
        });
        var standardError = process.StandardError.ReadToEndAsync(token);
        var transcript = new LocalTranscript();
        while (await process.StandardOutput.ReadLineAsync(token) is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var eventName = root.GetProperty("event").GetString();
            if (eventName == "stage") progress?.Report(new SttProgress(root.GetProperty("name").GetString() ?? "모델 준비", root.GetProperty("percent").GetDouble(), transcript.Text));
            else if (eventName == "audio_quality")
            {
                transcript.AudioRmsDb = root.TryGetProperty("rms_db", out var rms) ? rms.GetDouble() : 0;
                transcript.AudioPeakDb = root.TryGetProperty("peak_db", out var peak) ? peak.GetDouble() : 0;
                transcript.AudioQualityWarning = root.TryGetProperty("warning", out var warning) ? warning.GetString() ?? string.Empty : string.Empty;
                var stage = string.IsNullOrWhiteSpace(transcript.AudioQualityWarning)
                    ? $"오디오 품질 확인 · 평균 {transcript.AudioRmsDb:0.0} dBFS · 최대 {transcript.AudioPeakDb:0.0} dBFS"
                    : $"오디오 품질 주의 · {transcript.AudioQualityWarning}";
                progress?.Report(new SttProgress(stage, 0.01, transcript.Text));
            }
            else if (eventName == "segment")
            {
                transcript.Segments.Add(new TranscriptSegment
                {
                    Start = TimeSpan.FromSeconds(root.GetProperty("start").GetDouble()),
                    End = TimeSpan.FromSeconds(root.GetProperty("end").GetDouble()),
                    Text = root.GetProperty("text").GetString() ?? string.Empty,
                    Speaker = root.TryGetProperty("speaker", out var speaker) ? speaker.GetString() ?? string.Empty : string.Empty
                });
                progress?.Report(new SttProgress("Python faster-whisper 로컬 인식 중", root.GetProperty("percent").GetDouble(), transcript.Text));
            }
            else if (eventName == "complete")
            {
                transcript.DetectedLanguage = root.TryGetProperty("language", out var detected) ? detected.GetString() ?? string.Empty : string.Empty;
                transcript.LanguageProbability = root.TryGetProperty("language_probability", out var probability) ? probability.GetDouble() : 0;
                var allowed = LanguageCatalog.ParseAllowed(allowedLanguages);
                if (languageMode == "mixed" && allowed.Count > 0 && !allowed.Contains(transcript.DetectedLanguage))
                    transcript.LanguageConstraintWarning = $"감지 언어 '{transcript.DetectedLanguage}'가 허용 언어({string.Join(", ", allowed)})에 없습니다. 주 언어를 고정해 다시 처리하세요.";
                else if (languageMode != "fixed" && transcript.LanguageProbability is > 0 and < 0.65)
                    transcript.LanguageConstraintWarning = $"언어 감지 신뢰도가 {transcript.LanguageProbability:P0}로 낮습니다. 주 언어 고정을 권장합니다.";
                transcript.ProcessingSeconds = root.TryGetProperty("processing_seconds", out var processing) ? processing.GetDouble() : 0;
                transcript.RealtimeFactor = root.TryGetProperty("realtime_factor", out var factor) ? factor.GetDouble() : 0;
            }
            else if (eventName == "warning")
            {
                var warningText = root.TryGetProperty("message", out var warning) ? warning.GetString() ?? string.Empty : string.Empty;
                if (!string.IsNullOrWhiteSpace(warningText)) transcript.Warnings.Add(warningText);
                progress?.Report(new SttProgress($"전사 진단 · {warningText}", 0.94, transcript.Text));
            }
            else if (eventName == "diarization")
            {
                transcript.DetectedSpeakerCount = root.TryGetProperty("speaker_count", out var count) ? count.GetInt32() : 0;
                transcript.DiarizationStatus = root.TryGetProperty("status", out var status) ? status.GetString() ?? "미실행" : "미실행";
                transcript.DiarizationWarning = root.TryGetProperty("warning", out var warning) ? warning.GetString() ?? string.Empty : string.Empty;
                progress?.Report(new SttProgress($"화자 분리 · {transcript.DiarizationStatus}", 0.96, transcript.Text));
            }
            else if (eventName == "error") throw new InvalidOperationException(root.GetProperty("message").GetString());
        }
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0) throw new InvalidOperationException(await standardError);
        return transcript;
    }

    public async Task<LocalTranscript> TranscribeCrisperAsync(string audioPath, string modelName, string language, string mode, int chunkMinutes, IProgress<SttProgress>? progress = null, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(_crisperPythonPath))
            throw new InvalidOperationException("CrisperWhisper 전용 Python 3.12 환경이 없습니다. scripts/setup-python.ps1을 실행하세요.");
        var info = CreateStartInfo(["crisper-transcribe", "--input", audioPath, "--model", modelName, "--language", LanguageCatalog.ToWhisperCode(language), "--mode", mode, "--chunk-minutes", Math.Clamp(chunkMinutes, 1, 10).ToString()], _crisperPythonPath);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("CrisperWhisper 프로세스를 시작하지 못했습니다.");
        using var cancellation = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
        var standardError = process.StandardError.ReadToEndAsync(token);
        var transcript = new LocalTranscript { DiarizationStatus = "Crisper 단독 전사 · 화자 구분 없음" };
        while (await process.StandardOutput.ReadLineAsync(token) is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var eventName = root.GetProperty("event").GetString();
            if (eventName == "stage") progress?.Report(new SttProgress(root.GetProperty("name").GetString() ?? "CrisperWhisper 준비", root.GetProperty("percent").GetDouble(), transcript.Text));
            else if (eventName == "audio_quality")
            {
                transcript.AudioRmsDb = root.TryGetProperty("rms_db", out var rms) ? rms.GetDouble() : 0;
                transcript.AudioPeakDb = root.TryGetProperty("peak_db", out var peak) ? peak.GetDouble() : 0;
                transcript.AudioQualityWarning = root.TryGetProperty("warning", out var warning) ? warning.GetString() ?? string.Empty : string.Empty;
            }
            else if (eventName == "segment")
            {
                transcript.Segments.Add(new TranscriptSegment { Start = TimeSpan.FromSeconds(root.GetProperty("start").GetDouble()), End = TimeSpan.FromSeconds(root.GetProperty("end").GetDouble()), Text = root.GetProperty("text").GetString() ?? string.Empty });
                progress?.Report(new SttProgress("CrisperWhisper 2.0 정밀 전사 중", root.GetProperty("percent").GetDouble(), transcript.Text));
            }
            else if (eventName == "warning")
            {
                var warningText = root.TryGetProperty("message", out var warning) ? warning.GetString() ?? string.Empty : string.Empty;
                if (!string.IsNullOrWhiteSpace(warningText)) transcript.Warnings.Add(warningText);
                progress?.Report(new SttProgress($"Crisper 진단 · {warningText}", 0.97, transcript.Text));
            }
            else if (eventName == "complete")
            {
                transcript.DetectedLanguage = root.TryGetProperty("language", out var detected) ? detected.GetString() ?? string.Empty : string.Empty;
                transcript.ProcessingSeconds = root.TryGetProperty("processing_seconds", out var processing) ? processing.GetDouble() : 0;
                transcript.RealtimeFactor = root.TryGetProperty("realtime_factor", out var factor) ? factor.GetDouble() : 0;
            }
            else if (eventName == "error") throw new InvalidOperationException(root.GetProperty("message").GetString());
        }
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0) throw new InvalidOperationException(await standardError);
        return transcript;
    }

    public Task StartLivePreviewAsync(string modelName, string language, string languageMode, string qualityProfile, CancellationToken token = default)
    {
        if (IsLivePreviewRunning) return Task.CompletedTask;
        Interlocked.Exchange(ref _liveChunkOutstanding, 0);
        Interlocked.Exchange(ref _liveChunksSkipped, 0);
        var modelFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetingFlow", "Models", "faster-whisper");
        Directory.CreateDirectory(modelFolder);
        _liveProcess = Process.Start(CreateStartInfo("live", "--model", modelName, "--language", LanguageCatalog.ToWhisperCode(language), "--language-mode", languageMode, "--model-dir", modelFolder, "--quality-profile", SttQualityPresetCatalog.Get(qualityProfile).Id))
            ?? throw new InvalidOperationException("실시간 STT 초안 프로세스를 시작하지 못했습니다.");
        _ = _liveProcess.StandardError.ReadToEndAsync(token);
        _liveReader = Task.Run(async () =>
        {
            while (_liveProcess is { } process && await process.StandardOutput.ReadLineAsync(token) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var eventName = root.GetProperty("event").GetString();
                if (eventName is "live_loading" or "live_ready")
                    LiveDraftStatusChanged?.Invoke(this, root.TryGetProperty("message", out var message) ? message.GetString() ?? string.Empty : string.Empty);
                else if (eventName == "live_segment")
                    LiveDraftReceived?.Invoke(this, new LiveDraftSegment(
                        TimeSpan.FromSeconds(root.GetProperty("start").GetDouble()),
                        TimeSpan.FromSeconds(root.GetProperty("end").GetDouble()),
                        root.GetProperty("text").GetString() ?? string.Empty));
                else if (eventName == "live_chunk_done")
                {
                    Interlocked.Exchange(ref _liveChunkOutstanding, 0);
                    var rtf = root.TryGetProperty("realtime_factor", out var factor) ? factor.GetDouble() : 0;
                    var skipped = Volatile.Read(ref _liveChunksSkipped);
                    LiveDraftStatusChanged?.Invoke(this, skipped == 0 ? $"실시간 초안 · 처리 속도 {rtf:0.0}x" : $"실시간 초안 · {rtf:0.0}x · 지연 구간 {skipped}개는 종료 후 확정");
                }
                else if (eventName == "error")
                    LiveDraftStatusChanged?.Invoke(this, $"실시간 초안 중지 · {root.GetProperty("message").GetString()}");
            }
        }, token);
        return Task.CompletedTask;
    }

    public async Task SubmitLiveChunkAsync(string path, double startSeconds, CancellationToken token = default)
    {
        if (!IsLivePreviewRunning || string.IsNullOrWhiteSpace(path) || _liveProcess is null) { TryDelete(path); return; }
        if (Interlocked.CompareExchange(ref _liveChunkOutstanding, 1, 0) != 0)
        {
            Interlocked.Increment(ref _liveChunksSkipped);
            TryDelete(path);
            LiveDraftStatusChanged?.Invoke(this, "실시간 처리보다 음성이 빨라 초안 구간을 건너뜁니다 · 녹음 원본은 보존되고 종료 후 확정됩니다");
            return;
        }
        try { await _liveInputLock.WaitAsync(token); }
        catch
        {
            Interlocked.Exchange(ref _liveChunkOutstanding, 0);
            TryDelete(path);
            throw;
        }
        try
        {
            var payload = JsonSerializer.Serialize(new { command = "chunk", path, start = startSeconds });
            await _liveProcess.StandardInput.WriteLineAsync(payload);
            await _liveProcess.StandardInput.FlushAsync();
        }
        catch
        {
            Interlocked.Exchange(ref _liveChunkOutstanding, 0);
            TryDelete(path);
            throw;
        }
        finally { _liveInputLock.Release(); }
    }

    public async Task StopLivePreviewAsync(CancellationToken token = default)
    {
        if (_liveProcess is null) return;
        if (_liveProcess.HasExited)
        {
            _liveProcess.Dispose();
            _liveProcess = null;
            _liveReader = null;
            Interlocked.Exchange(ref _liveChunkOutstanding, 0);
            return;
        }
        try
        {
            await _liveProcess.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { command = "stop" }));
            await _liveProcess.StandardInput.FlushAsync();
            await _liveProcess.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(15), token);
            if (_liveReader is not null) await _liveReader;
        }
        catch (TimeoutException) { if (!_liveProcess.HasExited) _liveProcess.Kill(true); }
        finally
        {
            _liveProcess.Dispose();
            _liveProcess = null;
            _liveReader = null;
            Interlocked.Exchange(ref _liveChunkOutstanding, 0);
        }
    }

    private async Task<JsonDocument> RunSingleEventAsync(string command, CancellationToken token, string? pythonPath = null)
    {
        using var process = Process.Start(CreateStartInfo([command], pythonPath ?? _pythonPath)) ?? throw new InvalidOperationException("Python 프로세스를 시작하지 못했습니다.");
        var line = await process.StandardOutput.ReadLineAsync(token) ?? throw new InvalidOperationException("Python에서 응답하지 않았습니다.");
        await process.WaitForExitAsync(token);
        var document = JsonDocument.Parse(line);
        if (document.RootElement.GetProperty("event").GetString() == "error")
        {
            var message = document.RootElement.GetProperty("message").GetString();
            document.Dispose();
            throw new InvalidOperationException(message);
        }
        return document;
    }

    private ProcessStartInfo CreateStartInfo(params string[] arguments)
        => CreateStartInfo(arguments, _pythonPath);

    private ProcessStartInfo CreateStartInfo(IReadOnlyList<string> arguments, string pythonPath)
    {
        if (!File.Exists(_scriptPath)) throw new FileNotFoundException("Python STT 사이드카를 찾지 못했습니다.", _scriptPath);
        var info = new ProcessStartInfo
        {
            FileName = pythonPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        if (pythonPath.Equals("py", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add("-3.13");
        info.Environment["PYTHONUTF8"] = "1";
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1";
        info.ArgumentList.Add(_scriptPath);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    private static void TryDelete(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.Delete(path); }
        catch { }
    }

    public void Dispose()
    {
        if (_recordingProcess is { HasExited: false })
        {
            try
            {
                _recordingProcess.StandardInput.WriteLine("stop");
                _recordingProcess.StandardInput.Flush();
                if (!_recordingProcess.WaitForExit(3000)) _recordingProcess.Kill(true);
            }
            catch
            {
                if (!_recordingProcess.HasExited) _recordingProcess.Kill(true);
            }
        }
        _recordingProcess?.Dispose();
        if (_liveProcess is { HasExited: false })
        {
            try { _liveProcess.Kill(true); } catch { }
        }
        _liveProcess?.Dispose();
        _liveInputLock.Dispose();
    }
}
