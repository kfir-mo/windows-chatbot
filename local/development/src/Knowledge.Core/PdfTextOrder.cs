using System.Text.RegularExpressions;

namespace Knowledge.Core;
public static class PdfTextOrder
{
    private static readonly HashSet<string> Common = new("של את על עם שם לאחר לפני ניתן כדי אין יש הוא היא אם לא או זה זו עבור מתוך מספר קובץ קבצים תיקייה מערכת הגדרות מדריך בדיקה שאלה תשובה שלבי ביצוע נדרש בדקו בחר שמור כניסה זמן דקות שעות ימים מסמך הרשאות משתמש סיסמה חיבור רשת".Split(' '));
    public static string Normalize(string text)
    {
        // Some PDFs store Hebrew in visual order. Detect this at page level; never reverse Latin identifiers.
        var words = Regex.Matches(text, @"[\u0590-\u05FF]+").Select(m => m.Value).ToArray();
        var normal = words.Count(Common.Contains);
        var reversed = words.Count(w => Common.Contains(new string(w.Reverse().ToArray())));
        if (reversed < normal + 3) return text;
        var result = Regex.Replace(text, @"[\u0590-\u05FF \t.,:;!?־–]+", match =>
        {
            if (!match.Value.Any(c => c >= '\u0590' && c <= '\u05FF')) return match.Value;
            var core = match.Value.Trim();
            var start = match.Value.Length - match.Value.TrimStart().Length;
            return match.Value[..start] + new string(core.Reverse().ToArray()) + match.Value[(start + core.Length)..];
        });
        return Regex.Replace(result, @"(?<=[\u0590-\u05FF])(?=[A-Za-z0-9])|(?<=[A-Za-z0-9])(?=[\u0590-\u05FF])", " ");
    }
}
