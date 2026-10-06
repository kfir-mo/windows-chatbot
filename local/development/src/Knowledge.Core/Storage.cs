using System.Security.Cryptography;
using System.Text.Json;
namespace Knowledge.Core;
public static class Storage
{
    public static string Root { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsChat");
    public static void Save<T>(string name, T value)
    {
        WriteProtected(name, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(value), null, DataProtectionScope.CurrentUser));
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
    /// <summary>Like <see cref="Save{T}"/> but compressed first: for large values such as document indexes.</summary>
    public static void SaveCompressed<T>(string name, T value)
    {
        using var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionLevel.Fastest, true)) JsonSerializer.Serialize(zip, value);
        WriteProtected(name, ProtectedData.Protect(buffer.ToArray(), null, DataProtectionScope.CurrentUser));
    }
    public static T? LoadCompressed<T>(string name)
    {
        try
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(Root, name)), null, DataProtectionScope.CurrentUser);
            using var zip = new System.IO.Compression.GZipStream(new MemoryStream(bytes), System.IO.Compression.CompressionMode.Decompress);
            return JsonSerializer.Deserialize<T>(zip);
        }
        catch (Exception e) when (e is IOException or CryptographicException or JsonException or InvalidDataException) { return default; }
    }
    private static void WriteProtected(string name, byte[] bytes)
    {
        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, name);
        var temp = path + "." + Guid.NewGuid() + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
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
/// <summary>NotFound marks an answer without supporting evidence, so the UI can offer a deep-search retry.</summary>
public record ChatMessage(string Role, string Text, DateTimeOffset Time, List<Source> Sources, bool NotFound = false);
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
    public static bool SafeDocument(string path) => System.IO.Path.IsPathFullyQualified(path) && DocumentIndex.Extensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());
}
