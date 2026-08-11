using System.Text.Json;
using System.Text.Encodings.Web;
using MeetingFlow.App.Models;

namespace MeetingFlow.App.Services;

public sealed class MeetingRepository
{
    private readonly string _folder;
    private readonly object _writeLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public MeetingRepository(string? root = null)
    {
        root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetingFlow");
        _folder = Path.Combine(root, "Meetings");
        Directory.CreateDirectory(_folder);
    }

    public void Save(MeetingRecord record)
    {
        var path = Path.Combine(_folder, $"{record.Id}.json");
        var temporaryPath = path + ".tmp";
        var json = JsonSerializer.Serialize(record, JsonOptions);
        lock (_writeLock)
        {
            File.WriteAllText(temporaryPath, json, new System.Text.UTF8Encoding(false));
            File.Move(temporaryPath, path, true);
        }
    }

    public IReadOnlyList<MeetingRecord> LoadAll()
    {
        var records = new List<MeetingRecord>();
        foreach (var file in Directory.EnumerateFiles(_folder, "*.json"))
        {
            try
            {
                var record = JsonSerializer.Deserialize<MeetingRecord>(File.ReadAllText(file), JsonOptions);
                if (record is not null)
                {
                    record.SpeakerNames ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    record.RawTranscript = string.IsNullOrWhiteSpace(record.RawTranscript) ? record.Transcript : record.RawTranscript;
                    record.Transcript = string.IsNullOrWhiteSpace(record.Transcript) ? record.RawTranscript : record.Transcript;
                    var repaired = AiReportParser.TryRepair(record);
                    record.AiNotesText = string.IsNullOrWhiteSpace(record.AiNotesText) ? BuildAiNotes(record.Summary) : record.AiNotesText;
                    var template = ReportTemplateCatalog.Get(record.ReportTemplateId);
                    record.ReportTemplateId = template.Id;
                    record.ReportTemplateName = string.IsNullOrWhiteSpace(record.ReportTemplateName) ? template.Name : record.ReportTemplateName;
                    if (record.AiStatus == "사용 안 함" && !string.IsNullOrWhiteSpace(record.AiNotesText)) record.AiStatus = "완료";
                    if (repaired) Save(record);
                    records.Add(record);
                }
            }
            catch { /* 손상된 개별 기록은 건너뛴다. */ }
        }
        return records.OrderByDescending(x => x.StartedAt).ToList();
    }

    private static string BuildAiNotes(MeetingSummary summary)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(summary.Overview)) parts.Add($"핵심 요약{Environment.NewLine}{summary.Overview}");
        if (summary.Topics.Count > 0) parts.Add($"주요 안건{Environment.NewLine}{string.Join(Environment.NewLine, summary.Topics.Select(x => $"• {x}"))}");
        if (summary.Decisions.Count > 0) parts.Add($"결정사항{Environment.NewLine}{string.Join(Environment.NewLine, summary.Decisions.Select(x => $"• {x}"))}");
        return string.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    public void Delete(Guid id)
    {
        var path = Path.Combine(_folder, $"{id}.json");
        if (File.Exists(path)) File.Delete(path);
    }
}
