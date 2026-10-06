using System.Security.Cryptography;
using System.Text;

namespace Knowledge.Core;

public static class AiCredentials
{
    private static string Name(AiOptions options)
    {
        var scope = new Uri(options.Endpoint).GetLeftPart(UriPartial.Authority) + "|" + options.Authentication + "|" + options.SecretEnvironmentVariable;
        return "ai-key-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))[..24] + ".bin";
    }

    public static string? Resolve(AiOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.EnvironmentFileKey)) return options.EnvironmentFileKey.Trim();
        var saved = Storage.Load<string>(Name(options));
        if (!string.IsNullOrWhiteSpace(saved)) return saved.Trim();
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            var value = Environment.GetEnvironmentVariable(options.SecretEnvironmentVariable, target);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return null;
    }

    public static void Save(AiOptions options, string key)
    {
        key = key.Trim();
        if (key.Length == 0 || key.Any(char.IsWhiteSpace)) throw new InvalidOperationException("המפתח ריק או מכיל רווחים. הדביקו רק את מפתח ה-API.");
        Storage.Save(Name(options), key);
    }
    /// <summary>Removes the saved key (a key from an environment variable or .env is not affected).</summary>
    public static void Delete(AiOptions options)
    {
        var path = Path.Combine(Storage.Root, Name(options));
        if (File.Exists(path)) File.Delete(path);
    }
}

public static class AiErrors
{
    public static string Describe(Exception error) => error switch
    {
        InvalidOperationException => error.Message,
        OperationCanceledException => "שירות ה-AI לא ענה בזמן. נסו שוב או בדקו את החיבור.",
        HttpRequestException => "לא ניתן להתחבר לשירות ה-AI. בדקו גישה לשרת המוגדר, כתובת ותעודת HTTPS.",
        System.Text.Json.JsonException or KeyNotFoundException or InvalidDataException => "שירות ה-AI החזיר תשובה בפורמט לא מתאים. בדקו את פורמט ה-API והמודל.",
        _ => "לא ניתן להשלים את בקשת ה-AI. בדקו את הגדרות החיבור (ai.config.json)."
    };
}
