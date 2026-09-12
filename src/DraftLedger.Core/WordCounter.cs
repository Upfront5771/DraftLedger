using System.Text.RegularExpressions;

namespace DraftLedger.Core;

public sealed record TextStatistics(int Words, int Characters, int CharactersWithoutSpaces, int Paragraphs, int Sentences)
{
    public static TextStatistics Empty { get; } = new(0, 0, 0, 0, 0);
    public double Pages => Words / 250.0;
    public double ReadingMinutes => Words / 250.0;
    public double SpeakingMinutes => Words / 130.0;
}

public static class WordCounter
{
    private static readonly Regex Joined = new(@"[\p{L}\p{N}][\p{L}\p{N}\p{M}]*(?:['’\-‐‑][\p{L}\p{N}][\p{L}\p{N}\p{M}]*)*(?:[.,]\p{N}+)*", RegexOptions.Compiled);
    private static readonly Regex Split = new(@"[\p{L}\p{N}][\p{L}\p{N}\p{M}]*(?:['’][\p{L}\p{N}][\p{L}\p{N}\p{M}]*)*(?:[.,]\p{N}+)*", RegexOptions.Compiled);
    private static readonly Regex Letter = new(@"\p{L}", RegexOptions.Compiled);
    private static readonly Regex ParagraphBreak = new(@"\r?\n[\t ]*\r?\n", RegexOptions.Compiled);
    private static readonly Regex SentenceBreak = new(@"[.!?]+(?:[""'’”)]*)?(?=\s|$)", RegexOptions.Compiled);

    public static TextStatistics Analyze(string text, bool joinHyphens = true, bool countNumbers = true)
    {
        int words = Count(text, joinHyphens, countNumbers);
        var paragraphs = ParagraphBreak.Split(text).Count(x => Count(x, joinHyphens, countNumbers) > 0);
        var sentences = SentenceBreak.Split(text).Count(x => Count(x, joinHyphens, countNumbers) > 0);
        return new(words, text.Length, text.Count(c => !char.IsWhiteSpace(c)), paragraphs, sentences);
    }

    public static int Count(string text, bool joinHyphens = true, bool countNumbers = true)
    {
        var matches = (joinHyphens ? Joined : Split).Matches(text);
        return countNumbers ? matches.Count : matches.Cast<Match>().Count(m => Letter.IsMatch(m.Value));
    }
}
