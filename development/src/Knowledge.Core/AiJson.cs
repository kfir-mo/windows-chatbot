using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Knowledge.Core;

// Model-facing JSON is text inside a message. Keep Hebrew readable instead of feeding
// the model literal backslash-u sequences. HTTP transport JSON is decoded separately.
public static class AiJson
{
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
