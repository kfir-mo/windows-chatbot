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

/// <summary>No API key for a model that needs one: the chat offers a link to the AI settings.</summary>
public sealed class MissingKeyException() : InvalidOperationException("לא הוגדר מפתח API. הזינו מפתח בהגדרות ה־AI.");

/// <summary>The server's own 429: there is no budget in Chaty, so this is where answering stops until the server's limit resets.</summary>
public sealed class RateLimitException(string message, DateTimeOffset? resetsAt) : InvalidOperationException(message)
{
    public DateTimeOffset? ResetsAt { get; } = resetsAt;
    /// <summary>Reads the limit and the reset time out of a LiteLLM-style message ("… Current limit: 15, Remaining: 0. Limit
    /// resets at: 2026-10-07 11:32:10 UTC"); the message itself is never shown because it names the API key.</summary>
    public static RateLimitException From(string? serverMessage, System.Net.Http.Headers.RetryConditionHeaderValue? retry, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        DateTimeOffset? reset = retry?.Delta is { } delta ? at + delta : retry?.Date;
        var text = serverMessage ?? "";
        var resetMatch = System.Text.RegularExpressions.Regex.Match(text, @"resets at:\s*(\d{4}-\d{2}-\d{2}[ T]\d{1,2}:\d{2}(:\d{2}(\.\d+)?)?)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (resetMatch.Success && DateTimeOffset.TryParse(resetMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)) reset = parsed;
        var limitMatch = System.Text.RegularExpressions.Regex.Match(text, @"Current limit:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var message = "הגעתם למגבלת הבקשות של שרת ה־AI" + (limitMatch.Success ? $" (עד {limitMatch.Groups[1].Value} בקשות)" : "") + ". ";
        var wait = reset - at;
        message += wait is not { } left || left <= TimeSpan.Zero ? "נסו שוב בעוד דקה."
            : left < TimeSpan.FromMinutes(2) ? $"אפשר לשלוח שוב בעוד כ־{Math.Max(1, (int)Math.Ceiling(left.TotalSeconds))} שניות."
            : reset!.Value.ToLocalTime().Date == at.ToLocalTime().Date ? $"אפשר לשלוח שוב בשעה {reset.Value.ToLocalTime():HH:mm}."
            : $"אפשר לשלוח שוב ב־{reset.Value.ToLocalTime():dd/MM} בשעה {reset.Value.ToLocalTime():HH:mm}.";
        return new(message, reset);
    }
}
