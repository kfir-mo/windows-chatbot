using System.Security.Cryptography;
using System.Text.Json;
namespace Knowledge.Core;
public static class Storage
{
    public static string Root { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsChat");
    public static void Save<T>(string name, T value)
    {
        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, name);
        var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(value), null, DataProtectionScope.CurrentUser);
        var temp = path + "." + Guid.NewGuid() + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static T? Load<T>(string name)
    {
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(Root, name));
            return JsonSerializer.Deserialize<T>(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is IOException or CryptographicException or JsonException) { return default; }
    }
    public static void Log(Config config, string code)
    {
        if (!config.Logging.Enabled)
            return;
        Directory.CreateDirectory(Root);
        File.AppendAllText(Path.Combine(Root, "events.log"), $"{DateTimeOffset.Now:O} {code}\n");
    }
}
public record Source(string Path, string Location);
public record ChatMessage(string Role, string Text, DateTimeOffset Time, List<Source> Sources);
public sealed class Conversation
{
    public string ChatTypeId { get; set; } = "default";
    public string ChatTypeName { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string HistoryMetadata => (string.IsNullOrWhiteSpace(ChatTypeName) ? (ChatTypeId == ChatType.GeneralId ? "כללי" : "צ׳אט") : ChatTypeName) + " · " + LastActivityText;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Summary { get; set; } = "";
    public int SummarizedCount
    {
        get; set;
    }
    public List<ChatMessage> Messages { get; set; } = [];
    public string Title => Messages.FirstOrDefault(m => m.Role == "user" && !string.IsNullOrWhiteSpace(m.Text))?.Text ?? "שיחה חדשה";
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasUserMessages => Messages.Any(m => m.Role == "user" && !string.IsNullOrWhiteSpace(m.Text));
    [System.Text.Json.Serialization.JsonIgnore]
    public string LastActivityText => Messages.LastOrDefault() is { } last ? (last.Time.LocalDateTime.Date == DateTime.Today ? "היום" : last.Time.LocalDateTime.Date == DateTime.Today.AddDays(-1) ? "אתמול" : last.Time.ToLocalTime().ToString("dd/MM/yyyy")) + " · " + last.Time.ToLocalTime().ToString("HH:mm") : "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string HistoryPreview => Messages.LastOrDefault(m => m.Role == "user")?.Text.Replace('\r', ' ').Replace('\n', ' ') ?? "";
}
public static class Access
{
    public static bool CanRead(string path)
    {
        try
        {
            using var f = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch { return false; }
    }
    public static bool SafeDocument(string path) => System.IO.Path.IsPathFullyQualified(path) && new[] { ".docx", ".pptx", ".pdf", ".xlsx", ".txt", ".md" }.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());
}
