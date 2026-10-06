using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Knowledge.Core;

public static class DotEnv
{
    public static Dictionary<string, string> Read(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadLines(path))
        {
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#')) continue;
            if (text.StartsWith("export ", StringComparison.Ordinal)) text = text[7..].TrimStart();
            var equals = text.IndexOf('=');
            if (equals <= 0) throw new InvalidDataException("שורה לא תקינה בקובץ .env. השתמשו במבנה NAME=value.");
            var name = text[..equals].Trim();
            var value = text[(equals + 1)..].Trim();
            if (value.StartsWith('"'))
            {
                try { value = JsonSerializer.Deserialize<string>(value) ?? ""; }
                catch (JsonException) { throw new InvalidDataException("ערך מצוטט לא תקין בקובץ .env. בנתיבי Windows השתמשו ב-/ או ב-\\\\."); }
            }
            else if (value.StartsWith('\'') && value.EndsWith('\'') && value.Length >= 2) value = value[1..^1];
            result[name] = value;
        }
        return result;
    }

    public static void Apply(Config config, IReadOnlyDictionary<string, string> values)
    {
        foreach (var group in typeof(Config).GetProperties())
        {
            if (group.PropertyType == typeof(string))
            {
                if (values.TryGetValue(Key(group.Name), out var text)) group.SetValue(config, text);
                continue;
            }
            var target = group.GetValue(config)!;
            foreach (var property in group.PropertyType.GetProperties().Where(p => p.CanWrite && p.Name != nameof(AiOptions.EnvironmentFileKey)))
            {
                var key = Key(group.Name) + "_" + Key(property.Name);
                if (!values.TryGetValue(key, out var value)) continue;
                try
                {
                    object converted = property.PropertyType == typeof(string) ? value
                        : property.PropertyType == typeof(string[]) ? value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        : property.PropertyType == typeof(bool) ? bool.Parse(value)
                        : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    property.SetValue(target, converted);
                }
                catch { throw new InvalidDataException($"ערך לא תקין בהגדרה {key} בקובץ .env."); }
            }
        }
        config.Ai.EnvironmentFileKey = values.GetValueOrDefault("AI_API_KEY");
    }
    private static string Key(string name) => Regex.Replace(name, "([a-z0-9])([A-Z])", "$1_$2").ToUpperInvariant();
    public static void SetValue(string path, string name, string value)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        lines.RemoveAll(line =>
        {
            var text = line.TrimStart();
            if (text.StartsWith("export ")) text = text[7..].TrimStart();
            var equals = text.IndexOf('=');
            return equals > 0 && text[..equals].Trim().Equals(name, StringComparison.OrdinalIgnoreCase);
        });
        lines.Add(name + "=" + JsonSerializer.Serialize(value));
        var temporary = path + ".tmp";
        File.WriteAllLines(temporary, lines);
        File.Move(temporary, path, true);
    }
}
