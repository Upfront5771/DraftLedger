using System.Text;
using System.Text.RegularExpressions;

namespace DraftLedger.Core;

public static class LoreMatcher
{
    public static LoreSelection Select(IEnumerable<Lorebook> books, string text, int budget)
    {
        var before = new StringBuilder(); var after = new StringBuilder(); var names = new List<string>(); var warnings = new List<string>(); int used = 0;
        foreach (var item in books.SelectMany(b => b.Entries.Select(e => (Book: b.Name, Entry: e))).OrderByDescending(x => x.Entry.Order))
        {
            var entry = item.Entry; if (!entry.Enabled || string.IsNullOrWhiteSpace(entry.Content)) continue;
            bool Match(string key)
            {
                if (string.IsNullOrWhiteSpace(key)) return false;
                try
                {
                    int last = key.LastIndexOf('/');
                    if (key.StartsWith('/') && last > 0)
                    {
                        string flags = key[(last + 1)..];
                        if (flags.Any(c => c is not ('i' or 'm' or 's'))) { warnings.Add($"Unsupported regex flags in {item.Book}/{entry.Name}."); return false; }
                        var options = RegexOptions.CultureInvariant;
                        if (flags.Contains('i')) options |= RegexOptions.IgnoreCase; if (flags.Contains('m')) options |= RegexOptions.Multiline; if (flags.Contains('s')) options |= RegexOptions.Singleline;
                        return Regex.IsMatch(text, key[1..last], options, TimeSpan.FromMilliseconds(30));
                    }
                    if (!entry.WholeWords) return text.Contains(key, entry.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                    return Regex.IsMatch(text, @"(?<![\p{L}\p{N}_])" + Regex.Escape(key) + @"(?![\p{L}\p{N}_])", RegexOptions.CultureInvariant | (entry.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromMilliseconds(30));
                }
                catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException) { warnings.Add($"Invalid or slow keyword regex skipped in {item.Book}/{entry.Name}."); return false; }
            }
            bool active = entry.Constant;
            if (!active && entry.Keys.Any(Match))
            {
                active = true;
                if (entry.Selective && entry.SecondaryKeys.Count > 0)
                {
                    var matches = entry.SecondaryKeys.Select(Match).ToList();
                    active = entry.SelectiveLogic switch { 0 => matches.Any(x => x), 1 => !matches.All(x => x), 2 => !matches.Any(x => x), 3 => matches.All(x => x), _ => false };
                }
            }
            if (!active) continue;
            string content = $"[{item.Book}: {entry.Name}]\n{entry.Content}\n\n";
            if (used + content.Length > budget) { warnings.Add($"Lore budget skipped {item.Book}/{entry.Name}."); continue; }
            used += content.Length; (entry.Position == 0 ? before : after).Append(content); names.Add($"{item.Book}: {entry.Name}");
        }
        return new(before.ToString(), after.ToString(), names, warnings.Distinct().ToList());
    }
}
