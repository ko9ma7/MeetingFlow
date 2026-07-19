using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace MeetingFlow.App.Services;

public sealed record AudioPreviewChunk(string Path, double StartSeconds, double EndSeconds);

public sealed class AudioRecorder : IDisposable
{
    private const int PreviewChunkSeconds = 8;
    private IWaveIn? _capture;
    private WaveFileWriter? _writer;
    private WaveFileWriter? _previewWriter;
    private string _previewPath = string.Empty;
    private long _previewBytes;
    private double _previewStartSeconds;
    private bool _livePreview;
    public event EventHandler<float>? LevelChanged;
    public event EventHandler<AudioPreviewChunk>? PreviewChunkReady;
    public bool IsRecording => _capture is not null;

    public static IReadOnlyList<string> GetInputDevices() => Enumerable.Range(0, WaveIn.DeviceCount)
        .Select(i => WaveIn.GetCapabilities(i).ProductName).ToList();

    public static string GetSystemOutputDevice()
    {
        using var devices = new MMDeviceEnumerator();
        return devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).FriendlyName;
    }

    public static TimeSpan GetAudioDuration(string path)
    {
        using var reader = new MediaFoundationReader(path);
        return reader.TotalTime;
    }

    public void Start(string path, int deviceNumber, bool livePreview = false)
    {
        if (IsRecording) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        StartCapture(path, new WaveInEvent { DeviceNumber = deviceNumber, WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 100 }, livePreview);
    }

    public void StartLoopback(string path, bool livePreview = false)
    {
        if (IsRecording) return;
        StartCapture(path, new WasapiLoopbackCapture(), livePreview);
    }

    private void StartCapture(string path, IWaveIn capture, bool livePreview)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _capture = capture;
        _livePreview = livePreview;
        _previewStartSeconds = 0;
        _writer = new WaveFileWriter(path, capture.WaveFormat);
        if (_livePreview) OpenPreviewChunk(capture.WaveFormat);
        capture.DataAvailable += OnDataAvailable;
        capture.StartRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _writer?.Write(e.Buffer, 0, e.BytesRecorded);
        if (_livePreview && _capture is not null)
        {
            _previewWriter?.Write(e.Buffer, 0, e.BytesRecorded);
            _previewBytes += e.BytesRecorded;
            if (_previewBytes >= (long)_capture.WaveFormat.AverageBytesPerSecond * PreviewChunkSeconds)
            {
                CompletePreviewChunk(_capture.WaveFormat);
                OpenPreviewChunk(_capture.WaveFormat);
            }
        }
        var max = 0f;
        for (var i = 0; i < e.BytesRecorded; i += 2)
        {
            var sample = Math.Abs(BitConverter.ToInt16(e.Buffer, i) / 32768f);
            if (sample > max) max = sample;
        }
        LevelChanged?.Invoke(this, max);
    }

    public void Stop()
    {
        if (_capture is null) return;
        _capture.StopRecording();
        if (_livePreview) CompletePreviewChunk(_capture.WaveFormat);
        _capture.DataAvailable -= OnDataAvailable;
        _capture.Dispose();
        _writer?.Dispose();
        _capture = null;
        _writer = null;
        _livePreview = false;
    }

    private void OpenPreviewChunk(WaveFormat format)
    {
        var folder = Path.Combine(Path.GetTempPath(), "MeetingFlow", "LiveDraft");
        Directory.CreateDirectory(folder);
        _previewPath = Path.Combine(folder, $"chunk-{Guid.NewGuid():N}.wav");
        _previewWriter = new WaveFileWriter(_previewPath, format);
        _previewBytes = 0;
    }

    private void CompletePreviewChunk(WaveFormat format)
    {
        if (_previewWriter is null) return;
        _previewWriter.Dispose();
        _previewWriter = null;
        if (_previewBytes >= format.AverageBytesPerSecond)
        {
            var duration = _previewBytes / (double)format.AverageBytesPerSecond;
            PreviewChunkReady?.Invoke(this, new AudioPreviewChunk(_previewPath, _previewStartSeconds, _previewStartSeconds + duration));
            _previewStartSeconds += duration;
        }
        else
        {
            try { File.Delete(_previewPath); } catch { }
        }
        _previewBytes = 0;
    }

    public void Dispose() => Stop();
}
