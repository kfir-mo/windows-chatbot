using System.Security.Claims;
using System.Text.Json;
using Knowledge.Core;
namespace WindowsChat.Server;
public sealed class ServerSettings
{
    public string Url { get; set; } = "http://127.0.0.1:5187";
    public bool LocalDevelopment { get; set; } = true;
    public string CertificatePath { get; set; } = "";
    public string[] AiGroups { get; set; } = [];
    public string[] AdminGroups { get; set; } = [];
    public string[] PersonalGroups { get; set; } = [];
    public string LogDirectory { get; set; } = "";
    public string TimeZoneId { get; set; } = "Israel Standard Time";
    public List<ManagedAssistant> Assistants { get; set; } = [];
}
public sealed class ManagedAssistant
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Project { get; set; } = "";
    public string Category { get; set; } = "";
    public string Prompt { get; set; } = "";
    public List<string> Paths { get; set; } = [];
    public string[] Groups { get; set; } = [];
    public bool Enabled { get; set; } = true;
    public ChatType ToChatType() => new() { Id = "org:" + Id, Name = Name, Prompt = Prompt, Paths = Paths };
}
public static class Permissions
{
    public static bool InGroups(ClaimsPrincipal user, IEnumerable<string> groups) => groups.Any(g => !string.IsNullOrWhiteSpace(g) && user.IsInRole(g.Trim()));
    public static List<CatalogEntry> Catalog(ServerSettings settings, ClaimsPrincipal user) => !InGroups(user, settings.AiGroups) ? [] : settings.Assistants.Where(a => a.Enabled && InGroups(user, a.Groups)).Select(a => new CatalogEntry("org:" + a.Id, a.Name, a.Project, a.Category)).ToList();
}
public sealed class SettingsStore(string root)
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly object gate = new();
    public ServerSettings Read() { lock(gate) return JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(Path.Combine(root,"settings.json")),Json)!; }
    public void Save(ServerSettings value)
    {
        Validate(value);
        lock(gate) { var path=Path.Combine(root,"settings.json"); File.WriteAllText(path+".tmp",JsonSerializer.Serialize(value,Json)); File.Move(path+".tmp",path,true); }
    }
    public static void Validate(ServerSettings s)
    {
        if (s.Assistants.Count > 200) throw new InvalidDataException("Invalid limits");
        if (s.Assistants.Any(a=>string.IsNullOrWhiteSpace(a.Name)||a.Name.Length>80||a.Prompt.Length>6000||!System.Text.RegularExpressions.Regex.IsMatch(a.Id,"^[a-zA-Z0-9_-]{1,80}$")) || s.Assistants.Select(a=>a.Id).Distinct().Count()!=s.Assistants.Count) throw new InvalidDataException("Invalid assistant");
        if(!string.IsNullOrWhiteSpace(s.LogDirectory)) s.LogDirectory=ChatType.NormalizePaths([s.LogDirectory]).Single();
        _=TimeZoneInfo.FindSystemTimeZoneById(s.TimeZoneId);
        foreach(var a in s.Assistants) a.Paths=ChatType.NormalizePaths(a.Paths);
    }
}

