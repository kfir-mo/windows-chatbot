namespace Knowledge.Core;

public sealed class ChatType
{
    public const string GeneralId = "builtin:general";
    public bool IsGeneral => Id == GeneralId;
    public bool IsManaged => Id.StartsWith("org:", StringComparison.Ordinal);
    public string Project { get; set; } = "";
    public string Category { get; set; } = "";
    public static ChatType General() => new() { Id = GeneralId, Name = "כללי" };
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Prompt { get; set; } = "";
    public List<string> Paths { get; set; } = [];
    public static List<string> NormalizePaths(IEnumerable<string> paths) => paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p =>
    {
        p = p.Trim();
        if (!Path.IsPathFullyQualified(p) || p.Contains("://")) throw new InvalidDataException("יש להזין נתיב תיקייה מקומי מלא או נתיב רשת UNC.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
    }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    public Config Apply(Config defaults) => new()
    {
        GeneralChat = IsGeneral || Paths.Count == 0,
        AssistantPrompt = IsGeneral ? "" : Prompt,
        Ai = defaults.Ai, History = defaults.History, Ui = defaults.Ui, Logging = defaults.Logging, AnswerPrompt = defaults.AnswerPrompt,
        Documents = new() { Root = IsGeneral ? "" : Paths.FirstOrDefault() ?? "", Roots = IsGeneral ? [] : Paths.ToArray(), SearchSubfolders = defaults.Documents.SearchSubfolders,
            IntervalMinutes = defaults.Documents.IntervalMinutes, ChunkCharacters = defaults.Documents.ChunkCharacters,
            MaxFileMegabytes = defaults.Documents.MaxFileMegabytes, MaxPassages = defaults.Documents.MaxPassages,
            EvidenceCharacters = defaults.Documents.EvidenceCharacters, ExcerptCharacters = defaults.Documents.ExcerptCharacters }
    };
    public static void RedactOutsideFolders(Conversation conversation, DocumentOptions options)
    {
        var roots = options.EffectiveRoots.Select(Path.GetFullPath).ToArray();
        bool Allowed(string path)
        {
            try
            {
                var parent = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
                return roots.Any(root => parent.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)
                    || (options.SearchSubfolders && parent.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
            }
            catch { return false; }
        }
        for (int i = 0; i < conversation.Messages.Count; i++)
            if (conversation.Messages[i].Sources.Any(s => !Allowed(s.Path)))
            {
                conversation.Messages[i] = conversation.Messages[i] with { Text = "המקור אינו כלול עוד בתיקיות של סוג הצ׳אט הזה.", Sources = [] };
                conversation.Summary = ""; conversation.SummarizedCount = 0;
            }
    }
}

