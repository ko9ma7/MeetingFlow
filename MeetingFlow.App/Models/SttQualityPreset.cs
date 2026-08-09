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
        new("cpu-fast", "빠른 확인 · CPU", "small 확정 + tiny 실시간 · 긴 회의 빠른 확인용", "small", "tiny", 3),
        new("cpu-accurate", "고정밀 · CPU 권장", "medium 확정 + base 실시간 · 한국어 회의 균형형", "medium", "base", 5),
        new("cpu-maximum", "터보 고정밀 · CPU 주의", "large-v3-turbo 확정 + small 실시간 · 정확도와 속도 우선", "large-v3-turbo", "small", 5)
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
