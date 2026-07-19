namespace MeetingFlow.App.Models;

public sealed record SttQualityPreset(
    string Id,
    string Name,
    string Description,
    string FinalModel,
    string LiveModel,
    int BeamSize)
{
    public override string ToString() => Name;
}

public static class SttQualityPresetCatalog
{
    public const string DefaultId = "cpu-accurate";

    public static IReadOnlyList<SttQualityPreset> All { get; } =
    [
        new("cpu-fast", "빠른 확인 · CPU", "small 모델 · 최초 약 0.48GB 다운로드 · 긴 회의 빠른 확인용", "small", "small", 3),
        new("cpu-accurate", "고정밀 · CPU 권장", "medium 모델 · 최초 약 1.53GB 다운로드 · 한국어 회의 균형형", "medium", "medium", 5),
        new("cpu-maximum", "터보 고정밀 · CPU 주의", "large-v3-turbo · 최초 약 1.62GB 다운로드 · 정확도와 속도 우선", "large-v3-turbo", "medium", 5)
    ];

    public static SttQualityPreset Get(string? id)
    {
        var normalized = id switch
        {
            "korean-accurate" or "multilingual-accurate" => DefaultId,
            _ => id
        };
        return All.FirstOrDefault(x => x.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase)) ?? All[1];
    }
}
