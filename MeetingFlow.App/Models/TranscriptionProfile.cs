namespace MeetingFlow.App.Models;

public sealed record LanguageOption(string Code, string WhisperCode, string Name)
{
    public override string ToString() => $"{Name} ({Code})";
}

public static class LanguageCatalog
{
    public static IReadOnlyList<LanguageOption> All { get; } =
    [
        new("ko-KR", "ko", "한국어"),
        new("en-US", "en", "English"),
        new("zh-CN", "zh", "中文"),
        new("ja-JP", "ja", "日本語"),
        new("de-DE", "de", "Deutsch"),
        new("fr-FR", "fr", "Français"),
        new("es-ES", "es", "Español"),
        new("it-IT", "it", "Italiano"),
        new("pt-BR", "pt", "Português"),
        new("ru-RU", "ru", "Русский")
    ];

    public static LanguageOption Get(string? code) => All.FirstOrDefault(x => x.Code.Equals(code, StringComparison.OrdinalIgnoreCase)) ?? All[0];
    public static string ToWhisperCode(string? code) => code?.Equals("auto", StringComparison.OrdinalIgnoreCase) == true ? "auto" : Get(code).WhisperCode;
    public static string GetName(string? code) => Get(code).Name;

    public static IReadOnlySet<string> ParseAllowed(string? value)
    {
        var aliases = All.ToDictionary(x => x.Code, x => x.WhisperCode, StringComparer.OrdinalIgnoreCase);
        return (value ?? string.Empty).Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => aliases.TryGetValue(x, out var mapped) ? mapped : x.Split('-')[0].ToLowerInvariant())
            .Where(x => All.Any(option => option.WhisperCode == x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record TranscriptionProfile(string Id, string Name, string Description, string PromptContext, string RecommendedTemplateId)
{
    public override string ToString() => Name;
}

public static class TranscriptionProfileCatalog
{
    public const string DefaultId = "general";

    public static IReadOnlyList<TranscriptionProfile> All { get; } =
    [
        new("general", "일반 회의·대화", "일상적인 회의와 여러 사람의 자연스러운 대화", "자연스러운 대화이며 문장부호, 숫자, 고유명사를 정확히 기록합니다.", "standard-minutes"),
        new("media", "드라마·영상·팟캐스트", "영화, 드라마, 예능, 팟캐스트와 유튜브 대화", "방송 또는 영상의 자연스러운 대화이며 대사를 임의로 요약하지 않고 들리는 순서대로 기록합니다.", "standard-minutes"),
        new("news", "뉴스·미디어", "TV·유튜브 뉴스, 시사 방송, 다큐멘터리", "뉴스 방송이며 인명, 지명, 기관명, 직함, 날짜, 수치와 직접 인용을 정확히 기록합니다.", "news-brief"),
        new("business", "업무·프로젝트", "업무 회의, 일정, 의사결정과 실행 항목", "업무 회의이며 프로젝트명, 일정, 금액, 담당자와 결정사항을 정확히 기록합니다.", "project-status"),
        new("technical", "기술·설계", "제품, 설계, 제조 공정과 전문 용어", "기술 검토이며 규격, 치수, 단위, 제품명, 부품명과 영문 약어를 정확히 기록합니다.", "technical-review"),
        new("interview", "인터뷰·면접", "질문과 답변이 구분되는 대화", "질문과 답변 형식의 대화이며 화자의 질문, 답변, 고유명사와 경력을 정확히 기록합니다.", "interview-evaluation"),
        new("lecture", "강의·교육", "강연, 수업, 세미나와 설명형 콘텐츠", "강의이며 핵심 개념, 용어, 예시, 수치와 단계별 설명을 정확히 기록합니다.", "training-notes"),
        new("customer", "고객·영업 상담", "고객 요구, 제안, 가격과 후속 약속", "고객 상담이며 요구사항, 제품명, 가격, 일정과 약속 내용을 정확히 기록합니다.", "customer-sales"),
        new("legal", "법률·규정", "계약, 법률 검토, 규정과 공식 발언", "법률 또는 규정 관련 발언이며 조항, 기관명, 날짜와 인용을 임의로 바꾸지 않고 정확히 기록합니다.", "detailed-minutes")
    ];

    public static TranscriptionProfile Get(string? id) => All.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static string BuildPrompt(string language, string profileId, string allowedLanguages, bool useVocabulary, string vocabulary)
    {
        var profile = Get(profileId);
        var languageName = LanguageCatalog.GetName(language);
        var allowed = string.Join(", ", LanguageCatalog.ParseAllowed(allowedLanguages).Select(code => LanguageCatalog.All.First(x => x.WhisperCode == code).Name));
        var vocabularyText = useVocabulary && !string.IsNullOrWhiteSpace(vocabulary) ? $" 사용자 지정 중요 용어: {vocabulary.Trim()}" : string.Empty;
        return $"다음 음성의 주 언어는 {languageName}입니다. {profile.PromptContext} 허용된 대화 언어: {(string.IsNullOrWhiteSpace(allowed) ? languageName : allowed)}.{vocabularyText}";
    }
}
