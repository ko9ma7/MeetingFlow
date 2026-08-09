using System.Text.RegularExpressions;
using MeetingFlow.App.Models;

namespace MeetingFlow.App.Services;

public sealed record TranscriptComparisonResult(int ComparedSegments, int DisagreementCount, double AverageSimilarity, string Summary);

public static partial class TranscriptComparisonService
{
    public static TranscriptComparisonResult Compare(IReadOnlyList<TranscriptSegment> primary, IReadOnlyList<TranscriptSegment> secondary)
    {
        var similarities = new List<double>();
        foreach (var segment in primary)
        {
            var overlapping = secondary.Where(x => x.End > segment.Start && x.Start < segment.End).ToList();
            if (overlapping.Count == 0) continue;
            similarities.Add(Similarity(segment.Text, string.Join(' ', overlapping.Select(x => x.Text))));
        }

        if (similarities.Count == 0)
            return new(0, 0, 0, "시간이 겹치는 구간이 없어 자동 비교하지 못했습니다.");

        var disagreement = similarities.Count(x => x < 0.55);
        var average = similarities.Average();
        var summary = $"{similarities.Count}개 시간 구간 비교 · 평균 일치도 {average:P0} · 검토 필요 {disagreement}개";
        return new(similarities.Count, disagreement, average, summary);
    }

    internal static double Similarity(string first, string second)
    {
        var a = Tokenize(first);
        var b = Tokenize(second);
        if (a.Count == 0 || b.Count == 0) return 0;
        var intersection = a.Intersect(b).Count();
        var union = a.Union(b).Count();
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static HashSet<string> Tokenize(string text) =>
        WhitespaceRegex().Split(text.Trim().ToLowerInvariant())
            .Select(x => PunctuationRegex().Replace(x, string.Empty))
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex PunctuationRegex();
}
