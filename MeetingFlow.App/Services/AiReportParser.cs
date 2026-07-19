using System.Text.Json;
using MeetingFlow.App.Models;

namespace MeetingFlow.App.Services;

public static class AiReportParser
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static AiReportResult Parse(string response)
    {
        if (TryParse(response, out var result)) return result;
        throw new JsonException("Gemini가 완성된 보고서 형식으로 응답하지 않았습니다. 원문 JSON은 저장하지 않고 AI 재처리 대기 상태로 보존합니다.");
    }

    public static bool TryParse(string? response, out AiReportResult result)
    {
        result = new AiReportResult();
        if (string.IsNullOrWhiteSpace(response)) return false;
        return TryParseCore(StripCodeFence(response), 0, out result);
    }

    public static bool TryRepair(MeetingRecord record)
    {
        if (!LooksLikeStructuredResponse(record.AiNotesText) || !TryParse(record.AiNotesText, out var parsed)) return false;
        record.AiNotesText = parsed.ReportMarkdown;
        record.Summary = parsed.Summary;
        record.AiStatus = "완료";
        record.AiLastError = string.Empty;
        record.ProcessingStatus = "완료";
        record.DataVersion = Math.Max(record.DataVersion, 4);
        return true;
    }

    private static bool TryParseCore(string candidate, int depth, out AiReportResult result)
    {
        result = new AiReportResult();
        if (depth > 3) return false;

        foreach (var json in EnumerateJsonCandidates(candidate))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<AiReportResult>(json, JsonOptions);
                if (parsed is null || string.IsNullOrWhiteSpace(parsed.ReportMarkdown)) continue;
                Normalize(parsed);
                if (LooksLikeStructuredResponse(parsed.ReportMarkdown)
                    && TryParseCore(parsed.ReportMarkdown, depth + 1, out var nested))
                {
                    result = nested;
                    return true;
                }
                result = parsed;
                return true;
            }
            catch (JsonException) { }
        }

        if (TryDecodeEscapedContainer(candidate, out var decoded)
            && !string.Equals(decoded, candidate, StringComparison.Ordinal)
            && TryParseCore(decoded, depth + 1, out result)) return true;

        return false;
    }

    private static IEnumerable<string> EnumerateJsonCandidates(string text)
    {
        var trimmed = text.Trim();
        yield return trimmed;
        for (var start = 0; start < trimmed.Length; start++)
        {
            if (trimmed[start] != '{') continue;
            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var index = start; index < trimmed.Length; index++)
            {
                var current = trimmed[index];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (current == '\\') escaped = true;
                    else if (current == '"') inString = false;
                    continue;
                }
                if (current == '"') inString = true;
                else if (current == '{') depth++;
                else if (current == '}' && --depth == 0)
                {
                    yield return trimmed[start..(index + 1)];
                    break;
                }
            }
        }
    }

    private static bool TryDecodeEscapedContainer(string text, out string decoded)
    {
        decoded = text;
        var trimmed = text.Trim();
        if (!trimmed.Contains("\\\"reportMarkdown\\\"", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var wrapped = "\"" + trimmed.Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
            decoded = JsonSerializer.Deserialize<string>(wrapped) ?? text;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static void Normalize(AiReportResult result)
    {
        result.ReportMarkdown = result.ReportMarkdown.Trim();
        if (!result.ReportMarkdown.Contains('\n') && result.ReportMarkdown.Contains("\\n", StringComparison.Ordinal))
            result.ReportMarkdown = result.ReportMarkdown.Replace("\\n", Environment.NewLine, StringComparison.Ordinal);
        result.Summary ??= new MeetingSummary();
        result.Summary.Topics ??= [];
        result.Summary.Decisions ??= [];
        result.Summary.ActionItems ??= [];
        result.Summary.OpenQuestions ??= [];
        if (string.IsNullOrWhiteSpace(result.Summary.Overview))
            result.Summary.Overview = result.ReportMarkdown.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.TrimStart('#', ' ') ?? "보고서 생성 완료";
    }

    private static string StripCodeFence(string value) => value.Trim()
        .Replace("```json", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("```", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Trim();

    private static bool LooksLikeStructuredResponse(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Contains("reportMarkdown", StringComparison.OrdinalIgnoreCase)
        && (value.TrimStart().StartsWith('{') || value.TrimStart().StartsWith("\\{"));
}
