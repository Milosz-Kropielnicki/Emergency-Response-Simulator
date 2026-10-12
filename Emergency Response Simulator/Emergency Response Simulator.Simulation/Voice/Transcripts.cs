using System.Text.RegularExpressions;

namespace Emergency_Response_Simulator.Simulation.Voice;

/// <summary>
/// Turns what a speech recogniser heard into radio text the simulation understands (Phase 11 M7): call signs said as
/// words ("engine four", "Ambulance fourteen") become the call signs crews answer to ("Engine 4", "Ambulance 14").
/// </summary>
public static partial class Transcripts
{
    private static readonly string[] Units = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];

    private static readonly string[] Tens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    /// <summary>The prompt that biases the recogniser towards this session's call signs.</summary>
    public static string Prompt(IEnumerable<string> callsigns) =>
        "Fire service radio traffic. Control. Call signs: " + string.Join(", ", callsigns.Distinct().Take(80)) + ". Over.";

    public static string Normalise(string text, IEnumerable<string> callsigns)
    {
        var result = Whitespace().Replace(text.Trim(), " ");
        var known = callsigns.Distinct().ToList();
        // Numbers only after a call-sign word ("Engine four"), so "no one" stays as it was said.
        foreach (var prefix in known.Select(c => TrailingNumber().Replace(c, "").Trim()).Where(p => p.Length > 0).Distinct())
            result = Regex.Replace(result, $@"\b({Regex.Escape(prefix)})[\s\-]+({NumberWords()})\b",
                m => Number(m.Groups[2].Value) is { } number ? $"{m.Groups[1].Value} {number}" : m.Value, RegexOptions.IgnoreCase);
        foreach (var callsign in known.OrderByDescending(c => c.Length))
        {
            // "engine 4", "Engine-4", "engine4" → "Engine 4"
            var parts = callsign.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape);
            var pattern = @"\b" + string.Join(@"[\s\-]*", parts) + @"\b";
            result = Regex.Replace(result, pattern, callsign, RegexOptions.IgnoreCase);
        }
        return result;
    }

    /// <summary>"four" → 4, "twenty one" / "twenty-one" → 21; null for anything else.</summary>
    private static int? Number(string words)
    {
        var parts = words.ToLowerInvariant().Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries);
        var total = 0;
        foreach (var part in parts)
        {
            var unit = Array.IndexOf(Units, part);
            var ten = Array.IndexOf(Tens, part);
            if (unit >= 0) total += unit;
            else if (ten >= 2) total += ten * 10;
            else return null;
        }
        return total;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\s*\d+$")]
    private static partial Regex TrailingNumber();

    [GeneratedRegex(@"\b(?:(?:twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety)(?:[\s\-](?:one|two|three|four|five|six|seven|eight|nine))?|zero|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex NumberWords();
}
