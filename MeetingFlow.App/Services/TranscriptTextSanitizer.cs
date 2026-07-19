using System.Text.RegularExpressions;

namespace MeetingFlow.App.Services;

public static partial class TranscriptTextSanitizer
{
    public static string SanitizeLiveSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var text = value.Replace('\uFFFD', ' ').Trim();
        var originalMeaningful = text.Where(char.IsLetterOrDigit).ToArray();
        if (originalMeaningful.Length >= 8 && originalMeaningful.Distinct().Count() <= 2) return string.Empty;
        text = KoreanReactionRun().Replace(text, " ");
        text = LongCharacterRun().Replace(text, match => new string(match.Value[0], 3));
        text = RepeatedShortPhrase().Replace(text, match => match.Groups["unit"].Value.Trim());
        text = InlineWhitespace().Replace(text, " ").Trim();

        if (text.Length == 0 || ReactionOnly().IsMatch(text)) return string.Empty;

        var meaningful = text.Where(char.IsLetterOrDigit).ToArray();
        if (meaningful.Length == 0) return string.Empty;
        if (meaningful.Length >= 8 && meaningful.Distinct().Count() <= 2) return string.Empty;
        return text;
    }

    [GeneratedRegex("[ㅋㅎㅠㅜ]{4,}", RegexOptions.CultureInvariant)]
    private static partial Regex KoreanReactionRun();

    [GeneratedRegex("(?<char>[^\\s])\\k<char>{7,}", RegexOptions.CultureInvariant)]
    private static partial Regex LongCharacterRun();

    [GeneratedRegex("(?<unit>[^\\s]{1,6})(?:\\s*\\k<unit>){4,}", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedShortPhrase();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex InlineWhitespace();

    [GeneratedRegex("^[ㅋㅎㅠㅜ!?.~,\\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ReactionOnly();
}
