using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DraftLedger.Core;

public enum MemoryMode { Off, BibleAndSummaries, FullRetrieval }

public sealed class StoryMemoryOptions
{
    public MemoryMode Mode { get; set; }
    public bool AutomaticProposals { get; set; }
    public bool RequireApproval { get; set; } = true;
    public bool IncludeOpenThreads { get; set; } = true;
    public bool ContinuityWarnings { get; set; } = true;
    public int CharacterBudget { get; set; } = 18000;
    public int RetrievedPassages { get; set; } = 6;
    [JsonIgnore] public bool Enabled => Mode != MemoryMode.Off;
}

public sealed class StoryMemoryDocument
{
    public int FormatVersion { get; set; } = 1;
    public string StorySoFar { get; set; } = "";
    public string StyleGuide { get; set; } = "";
    public List<MemoryFact> Facts { get; set; } = [];
    public List<OpenPlotThread> OpenThreads { get; set; } = [];
    public List<ChapterMemory> ChapterSummaries { get; set; } = [];
    public List<MemoryProposal> Proposals { get; set; } = [];
    public List<ContinuityWarning> Warnings { get; set; } = [];
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.Now;
    [JsonIgnore] public string DiskHash { get; set; } = "";
}

public sealed class MemoryFact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Category { get; set; } = "Other";
    public string Subject { get; set; } = "";
    public string Fact { get; set; } = "";
    public string Source { get; set; } = "";
    public Guid? SourceSectionId { get; set; }
    public bool Locked { get; set; }
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.Now;
    [JsonIgnore] public string Label => $"{(Locked ? "🔒 " : "")}{Category} · {Subject}: {Fact}";
}

public sealed class OpenPlotThread
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Status { get; set; } = "Open";
    public string Source { get; set; } = "";
    public Guid? SourceSectionId { get; set; }
    [JsonIgnore] public string Label => $"{Status} · {Title}: {Detail}";
}

public sealed class ChapterMemory
{
    public Guid ChapterId { get; set; }
    public string ChapterTitle { get; set; } = "";
    public string Summary { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.Now;
    [JsonIgnore] public string Label => $"{ChapterTitle}: {Summary}";
}

public enum MemoryProposalKind { StorySummary, StyleGuide, ChapterSummary, Fact, OpenThread, ResolveThread, Warning }
public enum MemoryProposalStatus { Proposed, Approved, Rejected }

public sealed class MemoryProposal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public MemoryProposalKind Kind { get; set; }
    public MemoryProposalStatus Status { get; set; }
    public Guid? ChapterId { get; set; }
    public Guid? SectionId { get; set; }
    public string Category { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Content { get; set; } = "";
    public string Source { get; set; } = "";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    [JsonIgnore] public string Label { get { string text = $"{Kind} · {(Subject.Length > 0 ? Subject + ": " : "")}{Content}"; return text.Length > 220 ? text[..220] + "…" : text; } }
}

public sealed class ContinuityWarning
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Subject { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Source { get; set; } = "";
    public bool Resolved { get; set; }
    [JsonIgnore] public string Label => $"{(Resolved ? "Resolved" : "Review")} · {Subject}: {Detail}";
}

public sealed record RetrievedPassage(string Chapter, string Section, Guid SectionId, string Text, double Score);
public sealed record StoryMemoryContext(string Text, IReadOnlyList<string> Included, IReadOnlyList<string> Warnings, IReadOnlyList<RetrievedPassage> Passages)
{
    public static readonly StoryMemoryContext Empty = new("", [], [], []);
}

public static partial class StoryMemoryRetrieval
{
    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'’-]*", RegexOptions.CultureInvariant)]
    private static partial Regex TermsRegex();

    public static StoryMemoryContext Build(Story story, Chapter currentChapter, Section currentSection, StoryMemoryDocument memory, string prompt)
    {
        var options = story.Memory;
        if (!options.Enabled) return StoryMemoryContext.Empty;
        int budget = Math.Clamp(options.CharacterBudget, 2000, 64000);
        var included = new List<string>(); var warnings = new List<string>(); var blocks = new List<(string Name, string Text, int Priority)>();
        void Add(string name, string text, int priority)
        {
            text = text.Trim(); if (text.Length == 0) return;
            blocks.Add((name, text, priority));
        }
        Add("Story so far", memory.StorySoFar, 115);
        Add("Style guide", memory.StyleGuide, 95);
        if (memory.Facts.Count > 0) Add("Canonical story bible", string.Join("\n", memory.Facts.OrderByDescending(f => f.Locked).ThenBy(f => f.Category).ThenBy(f => f.Subject).Select(f => $"- [{f.Category}] {f.Subject}: {f.Fact}")), 120);
        if (options.IncludeOpenThreads && memory.OpenThreads.Any(t => t.Status.Equals("Open", StringComparison.OrdinalIgnoreCase)))
            Add("Open plot threads", string.Join("\n", memory.OpenThreads.Where(t => t.Status.Equals("Open", StringComparison.OrdinalIgnoreCase)).Select(t => $"- {t.Title}: {t.Detail}")), 110);
        var currentSummary = memory.ChapterSummaries.FirstOrDefault(c => c.ChapterId == currentChapter.Id);
        if (currentSummary is not null) Add("Current chapter summary", currentSummary.Summary, 105);
        var priorSummaries = memory.ChapterSummaries.Where(c => c.ChapterId != currentChapter.Id).ToList();
        if (priorSummaries.Count > 0) Add("Earlier chapter summaries", string.Join("\n", priorSummaries.Select(c => $"- {c.ChapterTitle}: {c.Summary}")), 80);

        var passages = options.Mode == MemoryMode.FullRetrieval ? Retrieve(story, currentSection, prompt, memory, options.RetrievedPassages) : [];
        if (passages.Count > 0) Add("Retrieved manuscript passages", string.Join("\n\n", passages.Select(p => $"[{p.Chapter} / {p.Section}]\n{p.Text}")), 100);

        var output = new StringBuilder();
        foreach (var block in blocks.OrderByDescending(b => b.Priority))
        {
            string header = $"## {block.Name}\n";
            int available = budget - output.Length - header.Length - 2;
            if (available <= 80) { warnings.Add($"Memory budget omitted: {block.Name}."); continue; }
            string value = block.Text;
            if (value.Length > available) { value = value[..available]; warnings.Add($"Memory budget truncated: {block.Name}."); }
            output.Append(header).AppendLine(value).AppendLine(); included.Add(block.Name);
        }
        foreach (var summary in memory.ChapterSummaries)
        {
            var source = story.Chapters.FirstOrDefault(c => c.Id == summary.ChapterId);
            if (source is not null && summary.SourceHash.Length > 0 && summary.SourceHash != ChapterHash(source)) warnings.Add($"Chapter summary may be stale: {source.Title}.");
        }
        return new(output.ToString().Trim(), included, warnings.Distinct().ToList(), passages);
    }

    public static List<RetrievedPassage> Retrieve(Story story, Section currentSection, string prompt, StoryMemoryDocument memory, int maximum)
    {
        maximum = Math.Clamp(maximum, 1, 20);
        string queryText = prompt + " " + currentSection.Text[^Math.Min(currentSection.Text.Length, 5000)..];
        var query = Terms(queryText);
        foreach (var fact in memory.Facts) { if (queryText.Contains(fact.Subject, StringComparison.OrdinalIgnoreCase)) foreach (string term in Terms(fact.Subject + " " + fact.Fact)) query.Add(term); }
        var chunks = new List<(RetrievedPassage Passage, int Order)>(); int order = 0; bool reachedCurrent = false;
        foreach (var chapter in story.Chapters)
        {
            foreach (var section in chapter.Sections)
            {
                if (section.Id == currentSection.Id) { reachedCurrent = true; break; }
                foreach (string chunk in Chunks(section.Text))
                {
                    var terms = Terms(chunk); int overlap = terms.Count(t => query.Contains(t));
                    double exact = query.Where(t => t.Length >= 5 && chunk.Contains(t, StringComparison.OrdinalIgnoreCase)).Sum(t => Math.Min(3, t.Length / 5.0));
                    double recency = order / 1000.0;
                    double entity = memory.Facts.Count(f => f.Subject.Length > 1 && queryText.Contains(f.Subject, StringComparison.OrdinalIgnoreCase) && chunk.Contains(f.Subject, StringComparison.OrdinalIgnoreCase)) * 4;
                    double score = overlap + exact + entity + recency;
                    if (score > 0) chunks.Add((new(chapter.Title, section.Title, section.Id, chunk, score), order));
                    order++;
                }
            }
            if (reachedCurrent) break;
        }
        return chunks.OrderByDescending(x => x.Passage.Score).ThenByDescending(x => x.Order).Take(maximum).OrderBy(x => x.Order).Select(x => x.Passage).ToList();
    }

    private static HashSet<string> Terms(string text) => TermsRegex().Matches(text).Select(m => m.Value.ToLowerInvariant()).Where(t => t.Length > 2 && !StopWords.Contains(t)).ToHashSet();
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal) { "the", "and", "that", "with", "this", "from", "into", "then", "they", "their", "have", "was", "were", "for", "but", "not", "you", "her", "his", "she", "him", "had", "what", "when", "where", "who", "how" };
    private static IEnumerable<string> Chunks(string text)
    {
        var paragraphs = Regex.Split(text, @"\r?\n\s*\r?\n"); var buffer = new StringBuilder();
        foreach (var paragraph in paragraphs.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            string clean = paragraph.Trim();
            if (buffer.Length > 0 && buffer.Length + clean.Length + 2 > 1800) { yield return buffer.ToString(); buffer.Clear(); }
            if (clean.Length > 2400) { for (int i = 0; i < clean.Length; i += 1800) yield return clean.Substring(i, Math.Min(1800, clean.Length - i)); }
            else { if (buffer.Length > 0) buffer.AppendLine().AppendLine(); buffer.Append(clean); }
        }
        if (buffer.Length > 0) yield return buffer.ToString();
    }

    public static string ChapterHash(Chapter chapter) => AtomicFile.Hash(string.Join("\n\u001e\n", chapter.Sections.Select(s => s.Text)));
}

public sealed class StoryMemoryStore
{
    public string Folder(Story story) => Path.Combine(story.Folder, "memory");
    public string PathFor(Story story) => Path.Combine(Folder(story), "memory.json");

    public StoryMemoryDocument Load(Story story)
    {
        string path = PathFor(story);
        if (!File.Exists(path)) return new();
        string json = File.ReadAllText(path);
        var value = JsonSerializer.Deserialize<StoryMemoryDocument>(json, ProjectStore.Json) ?? throw new InvalidDataException("Story memory file is empty.");
        if (value.FormatVersion != 1) throw new InvalidDataException("Unsupported story memory version.");
        value.Facts ??= []; value.OpenThreads ??= []; value.ChapterSummaries ??= []; value.Proposals ??= []; value.Warnings ??= [];
        value.DiskHash = AtomicFile.Hash(json); return value;
    }

    public void Save(Story story, StoryMemoryDocument memory)
    {
        string path = PathFor(story); Directory.CreateDirectory(Folder(story));
        if (File.Exists(path) && memory.DiskHash.Length > 0 && AtomicFile.Hash(File.ReadAllText(path)) != memory.DiskHash)
            throw new IOException("Story memory changed outside DraftLedger. Reopen Memory & Continuity before saving.");
        memory.Updated = DateTimeOffset.Now;
        string json = JsonSerializer.Serialize(memory, ProjectStore.Json); AtomicFile.Write(path, json); memory.DiskHash = AtomicFile.Hash(json);
        WriteReadableFiles(story, memory);
    }

    private void WriteReadableFiles(Story story, StoryMemoryDocument memory)
    {
        string folder = Folder(story); Directory.CreateDirectory(folder);
        AtomicFile.Write(Path.Combine(folder, "story-so-far.md"), "# Story so far\n\n" + memory.StorySoFar.Trim() + "\n");
        var bible = new StringBuilder("# Story bible\n\n");
        if (memory.StyleGuide.Length > 0) bible.Append("## Style guide\n\n").AppendLine(memory.StyleGuide.Trim()).AppendLine();
        foreach (var group in memory.Facts.GroupBy(f => f.Category).OrderBy(g => g.Key)) { bible.Append("## ").AppendLine(group.Key).AppendLine(); foreach (var fact in group) bible.Append("- **").Append(fact.Subject).Append(":** ").AppendLine(fact.Fact); bible.AppendLine(); }
        AtomicFile.Write(Path.Combine(folder, "story-bible.md"), bible.ToString());
        AtomicFile.Write(Path.Combine(folder, "open-threads.md"), "# Open plot threads\n\n" + string.Join("\n", memory.OpenThreads.Select(t => $"- **{t.Status} · {t.Title}:** {t.Detail}")) + "\n");
        string chapters = Path.Combine(folder, "chapter-summaries"); Directory.CreateDirectory(chapters);
        foreach (var summary in memory.ChapterSummaries) AtomicFile.Write(Path.Combine(chapters, summary.ChapterId.ToString("N") + ".md"), $"# {summary.ChapterTitle}\n\n{summary.Summary.Trim()}\n");
    }

    public string DeleteRecoverably(Story story)
    {
        string folder = Folder(story); if (!Directory.Exists(folder)) return "";
        string recovery = Path.Combine(story.Folder, "recovery", "memory-deleted-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(recovery)!); Directory.Move(folder, recovery); return recovery;
    }
}

public static class StoryMemoryAnalysis
{
    public static PreparedChat BuildRequest(Story story, IEnumerable<Chapter> chapters, StoryMemoryDocument existing, string instruction)
    {
        var manuscript = new StringBuilder();
        foreach (var chapter in chapters)
        {
            manuscript.Append("# ").AppendLine(chapter.Title);
            foreach (var section in chapter.Sections) manuscript.Append("## ").AppendLine(section.Title).AppendLine(section.Text).AppendLine();
        }
        string system = "Analyze fiction continuity. Return only one valid JSON object, without Markdown fences. Do not invent facts. Proposed memory must be supported by the supplied manuscript.";
        string schema = "Return: {\"storySummary\":\"concise story-so-far summary\",\"styleGuide\":\"voice and style observations\",\"chapterSummaries\":[{\"chapterId\":\"GUID\",\"summary\":\"...\"}],\"facts\":[{\"category\":\"Character|Relationship|Location|World|Object|Timeline|Other\",\"subject\":\"...\",\"fact\":\"...\"}],\"openThreads\":[{\"title\":\"...\",\"detail\":\"...\"}],\"resolvedThreads\":[{\"title\":\"...\",\"detail\":\"...\"}],\"warnings\":[{\"subject\":\"...\",\"detail\":\"...\"}]}";
        string user = $"Story: {story.Title}\nAuthor instruction: {instruction}\n\nExisting canonical memory:\nStory so far: {existing.StorySoFar}\nStyle guide: {existing.StyleGuide}\nFacts:\n{string.Join("\n", existing.Facts.Select(f => f.Subject + ": " + f.Fact))}\nChapter summaries:\n{string.Join("\n", existing.ChapterSummaries.Select(c => c.ChapterTitle + ": " + c.Summary))}\nOpen threads:\n{string.Join("\n", existing.OpenThreads.Where(t => t.Status == "Open").Select(t => t.Title + ": " + t.Detail))}\n\n{schema}\n\nManuscript to analyze:\n{manuscript}";
        if (user.Length > 220000) user = user[..220000];
        return new([new("system", system), new("user", user)], [], [], system.Length + user.Length, StoryMemoryContext.Empty);
    }

    public static List<MemoryProposal> Parse(string raw, Story story, IReadOnlyCollection<Chapter> chapters, Guid? sectionId = null)
    {
        string json = raw.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal)) { int first = json.IndexOf('\n'); int last = json.LastIndexOf("```", StringComparison.Ordinal); if (first >= 0 && last > first) json = json[(first + 1)..last].Trim(); }
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Memory analysis did not return a JSON object.");
        var proposals = new List<MemoryProposal>();
        void Single(string property, MemoryProposalKind kind)
        {
            if (root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) proposals.Add(new() { Kind = kind, Content = value.GetString()!.Trim(), SectionId = sectionId, Source = "AI analysis" });
        }
        Single("storySummary", MemoryProposalKind.StorySummary); Single("styleGuide", MemoryProposalKind.StyleGuide);
        if (root.TryGetProperty("chapterSummaries", out var summaries) && summaries.ValueKind == JsonValueKind.Array)
            foreach (var item in summaries.EnumerateArray()) { Guid.TryParse(Get(item, "chapterId"), out var id); var chapter = chapters.FirstOrDefault(c => c.Id == id); string content = Get(item, "summary"); if (chapter is not null && content.Length > 0) proposals.Add(new() { Kind = MemoryProposalKind.ChapterSummary, ChapterId = id, Subject = chapter.Title, Content = content, SectionId = sectionId, Source = chapter.Title }); }
        AddArray("facts", MemoryProposalKind.Fact, "subject", "fact", "category");
        AddArray("openThreads", MemoryProposalKind.OpenThread, "title", "detail", null);
        AddArray("resolvedThreads", MemoryProposalKind.ResolveThread, "title", "detail", null);
        AddArray("warnings", MemoryProposalKind.Warning, "subject", "detail", null);
        return proposals.Where(p => p.Content.Length > 0).Take(500).ToList();

        void AddArray(string property, MemoryProposalKind kind, string subjectName, string contentName, string? categoryName)
        {
            if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array) return;
            foreach (var item in array.EnumerateArray()) proposals.Add(new() { Kind = kind, Subject = Get(item, subjectName), Content = Get(item, contentName), Category = categoryName is null ? "" : Get(item, categoryName), SectionId = sectionId, Source = "AI analysis" });
        }
    }

    private static string Get(JsonElement item, string property) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" : "";

    public static void Approve(Story story, StoryMemoryDocument memory, MemoryProposal proposal)
    {
        proposal.Status = MemoryProposalStatus.Approved;
        switch (proposal.Kind)
        {
            case MemoryProposalKind.StorySummary: memory.StorySoFar = proposal.Content; break;
            case MemoryProposalKind.StyleGuide: memory.StyleGuide = proposal.Content; break;
            case MemoryProposalKind.ChapterSummary:
                var chapter = story.Chapters.FirstOrDefault(c => c.Id == proposal.ChapterId); if (chapter is null) break;
                var summary = memory.ChapterSummaries.FirstOrDefault(c => c.ChapterId == chapter.Id); if (summary is null) { summary = new() { ChapterId = chapter.Id }; memory.ChapterSummaries.Add(summary); }
                summary.ChapterTitle = chapter.Title; summary.Summary = proposal.Content; summary.SourceHash = StoryMemoryRetrieval.ChapterHash(chapter); summary.Updated = DateTimeOffset.Now; break;
            case MemoryProposalKind.Fact:
                var conflict = memory.Facts.FirstOrDefault(f => f.Subject.Equals(proposal.Subject, StringComparison.OrdinalIgnoreCase) && f.Category.Equals(proposal.Category, StringComparison.OrdinalIgnoreCase) && !f.Fact.Equals(proposal.Content, StringComparison.OrdinalIgnoreCase));
                if (conflict is not null) memory.Warnings.Add(new() { Subject = proposal.Subject, Detail = $"Existing: {conflict.Fact}\nProposed: {proposal.Content}", Source = proposal.Source });
                memory.Facts.Add(new() { Category = proposal.Category.Length > 0 ? proposal.Category : "Other", Subject = proposal.Subject, Fact = proposal.Content, Source = proposal.Source, SourceSectionId = proposal.SectionId }); break;
            case MemoryProposalKind.OpenThread: memory.OpenThreads.Add(new() { Title = proposal.Subject, Detail = proposal.Content, Source = proposal.Source, SourceSectionId = proposal.SectionId }); break;
            case MemoryProposalKind.ResolveThread:
                var thread = memory.OpenThreads.FirstOrDefault(t => t.Title.Equals(proposal.Subject, StringComparison.OrdinalIgnoreCase)); if (thread is not null) thread.Status = "Resolved"; break;
            case MemoryProposalKind.Warning: memory.Warnings.Add(new() { Subject = proposal.Subject, Detail = proposal.Content, Source = proposal.Source }); break;
        }
    }
}
