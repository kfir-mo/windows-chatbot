using System.Text.Json;
namespace Knowledge.Core;
public static class UsageReader
{
    // Dot-separated object properties, or a JSON Pointer for field names containing dots.
    public static long? Read(JsonElement root, string path)
    {
        var parts = path.StartsWith('/') ? path[1..].Split('/').Select(p=>p.Replace("~1","/").Replace("~0","~")) : path.Split('.');
        foreach (var part in parts)
        {
            if (root.ValueKind == JsonValueKind.Array && int.TryParse(part,out var i) && i>=0 && i<root.GetArrayLength()) root=root[i];
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(part,out var child)) root=child;
            else return null;
        }
        return root.ValueKind == JsonValueKind.Number && root.TryGetInt64(out var count) && count>=0 ? count : null;
    }
}
