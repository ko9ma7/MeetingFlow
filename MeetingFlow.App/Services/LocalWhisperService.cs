using NAudio.Wave;
using Whisper.net;
using Whisper.net.Ggml;
using MeetingFlow.App.Models;

namespace MeetingFlow.App.Services;

public sealed record SttProgress(string Stage, double Percent, string PartialTranscript = "");

public sealed class LocalWhisperService
{
    private readonly string _modelFolder;

    public LocalWhisperService(string? root = null)
    {
        root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetingFlow");
        _modelFolder = Path.Combine(root, "Models");
        Directory.CreateDirectory(_modelFolder);
    }

    public bool IsModelInstalled(string modelName) => File.Exists(GetModelPath(modelName));

    public async Task<LocalTranscript> TranscribeAsync(string audioPath, string modelName, string language, IProgress<SttProgress>? progress = null, CancellationToken token = default)
    {
        var modelPath = await EnsureModelAsync(modelName, progress, token);
        var normalizedPath = await NormalizeAudioAsync(audioPath, progress, token);
        var deleteNormalized = !string.Equals(normalizedPath, audioPath, StringComparison.OrdinalIgnoreCase);
        try
        {
            using var reader = new WaveFileReader(normalizedPath);
            var duration = reader.TotalTime.TotalMilliseconds;
            using var factory = WhisperFactory.FromPath(modelPath);
            using var processor = factory.CreateBuilder()
                .WithLanguage(ToWhisperLanguage(language))
                .Build();
            using var stream = File.OpenRead(normalizedPath);
            var transcript = new LocalTranscript();
            await foreach (var result in processor.ProcessAsync(stream, token))
            {
                var segment = new TranscriptSegment { Start = result.Start, End = result.End, Text = result.Text.Trim() };
                if (segment.Text.Length == 0) continue;
                transcript.Segments.Add(segment);
                var percent = duration <= 0 ? 0 : Math.Clamp(result.End.TotalMilliseconds / duration, 0, 1);
                progress?.Report(new SttProgress("로컬 음성 인식 중", percent, transcript.Text));
            }
            progress?.Report(new SttProgress("로컬 전사 완료", 1, transcript.Text));
            return transcript;
        }
        finally
        {
            if (deleteNormalized && File.Exists(normalizedPath)) File.Delete(normalizedPath);
        }
    }

    public static IReadOnlyList<TranscriptSegment> SelectRange(IEnumerable<TranscriptSegment> segments, int startMinute, int endMinute)
    {
        var start = TimeSpan.FromMinutes(Math.Max(0, startMinute));
        var end = endMinute <= 0 ? TimeSpan.MaxValue : TimeSpan.FromMinutes(endMinute);
        if (end <= start) throw new ArgumentException("AI 정리 종료 시간은 시작 시간보다 커야 합니다.");
        return segments.Where(x => x.End >= start && x.Start < end).ToList();
    }

    public static IReadOnlyList<TranscriptSegment> SelectRangeBySeconds(IEnumerable<TranscriptSegment> segments, double startSeconds, double endSeconds)
    {
        var start = TimeSpan.FromSeconds(Math.Max(0, startSeconds));
        var end = endSeconds <= 0 ? TimeSpan.MaxValue : TimeSpan.FromSeconds(endSeconds);
        if (end <= start) throw new ArgumentException("AI 정리 종료 시간은 시작 시간보다 커야 합니다.");
        return segments.Where(x => x.End >= start && x.Start < end).ToList();
    }

    private async Task<string> EnsureModelAsync(string modelName, IProgress<SttProgress>? progress, CancellationToken token)
    {
        var path = GetModelPath(modelName);
        if (File.Exists(path)) return path;
        progress?.Report(new SttProgress($"Whisper {modelName} 모델을 처음 한 번 다운로드 중", 0));
        var type = modelName.ToLowerInvariant() switch
        {
            "tiny" => GgmlType.Tiny,
            "base" => GgmlType.Base,
            "small" => GgmlType.Small,
            "medium" => GgmlType.Medium,
            "large-v3" => GgmlType.LargeV3,
            "large-v3-turbo" => GgmlType.LargeV3Turbo,
            _ => GgmlType.Small
        };
        await using var source = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(type, cancellationToken: token);
        await using var target = File.Create(path);
        await source.CopyToAsync(target, token);
        progress?.Report(new SttProgress("Whisper 모델 다운로드 완료", 1));
        return path;
    }

    private static async Task<string> NormalizeAudioAsync(string audioPath, IProgress<SttProgress>? progress, CancellationToken token)
    {
        await Task.Yield();
        token.ThrowIfCancellationRequested();
        if (Path.GetExtension(audioPath).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            using var wave = new WaveFileReader(audioPath);
            if (wave.WaveFormat.SampleRate == 16000 && wave.WaveFormat.Channels == 1 && wave.WaveFormat.BitsPerSample == 16) return audioPath;
        }
        progress?.Report(new SttProgress("오디오를 16kHz 모노로 변환 중", 0));
        var output = Path.Combine(Path.GetTempPath(), $"meetingflow-{Guid.NewGuid():N}.wav");
        using var source = new MediaFoundationReader(audioPath);
        var targetFormat = new WaveFormat(16000, 16, 1);
        using var resampler = new MediaFoundationResampler(source, targetFormat) { ResamplerQuality = 60 };
        WaveFileWriter.CreateWaveFile(output, resampler);
        return output;
    }

    private string GetModelPath(string modelName) => Path.Combine(_modelFolder, $"ggml-{modelName.ToLowerInvariant()}.bin");
    private static string ToWhisperLanguage(string language) => LanguageCatalog.ToWhisperCode(language);
}
