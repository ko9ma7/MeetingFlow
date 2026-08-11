using System.Text.RegularExpressions;
using MeetingFlow.App.Models;

namespace MeetingFlow.App.Services;

public static partial class SpeakerLabelService
{
    public static string Format(MeetingRecord record) => string.Join(Environment.NewLine,
        record.TranscriptSegments.Select(segment => FormatSegment(record, segment)));

    public static string FormatSegment(MeetingRecord record, TranscriptSegment segment)
    {
        var label = NormalizeLabel(segment.Speaker);
        var prefix = string.Empty;
        if (!string.IsNullOrWhiteSpace(label))
        {
            record.SpeakerNames.TryGetValue(label, out var name);
            prefix = string.IsNullOrWhiteSpace(name) ? $"화자 {label}: " : $"화자 {label}({name.Trim()}): ";
        }
        return $"[{segment.Timestamp}] {prefix}{segment.Text.Trim()}";
    }

    public static List<TranscriptSegment> Parse(string transcript, TimeSpan duration, IDictionary<string, string> speakerNames)
    {
        var parsed = new List<TranscriptSegment>();
        foreach (var line in transcript.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = TranscriptLine().Match(line);
            if (!match.Success || !TimeSpan.TryParse(match.Groups["time"].Value, out var start)) continue;
            var label = NormalizeLabel(match.Groups["speaker"].Value);
            var name = match.Groups["name"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(name)) speakerNames[label] = name;
            parsed.Add(new TranscriptSegment { Start = start, Speaker = label, Text = match.Groups["text"].Value.Trim() });
        }
        for (var i = 0; i < parsed.Count; i++)
            parsed[i].End = i + 1 < parsed.Count ? parsed[i + 1].Start : duration > parsed[i].Start ? duration : parsed[i].Start + TimeSpan.FromSeconds(5);
        return parsed;
    }

    private static string NormalizeLabel(string value)
    {
        var label = value.Trim();
        return label.Length <= 4 ? label.ToUpperInvariant() : label;
    }

    [GeneratedRegex(@"^\[(?<time>\d{1,2}:\d{2}(?::\d{2})?)\]\s*(?:화자\s+(?<speaker>[^:()]+)(?:\((?<name>[^)]+)\))?:\s*)?(?<text>.+)$")]
    private static partial Regex TranscriptLine();
}
