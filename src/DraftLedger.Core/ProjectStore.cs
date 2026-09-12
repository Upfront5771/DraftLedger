using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace DraftLedger.Core;

public sealed class ProjectStore(Settings settings)
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Dictionary<Guid, DateTimeOffset> lastSnapshot = [];
    public string SectionPath(Story story, Chapter chapter, Section section) => Path.Combine(story.Folder, "chapters", chapter.Id.ToString("N"), section.Id.ToString("N") + ".md");

    public List<Story> LoadLibrary(out List<string> errors)
    {
        Directory.CreateDirectory(settings.StorageRoot);
        var stories = new List<Story>(); errors = [];
        foreach (string folder in Directory.EnumerateDirectories(settings.StorageRoot).Concat(settings.ProjectFolders).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(Path.Combine(folder, "project.json"))) continue;
            try { stories.Add(Load(folder)); }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(folder)}: {ex.Message}"); }
        }
        return stories;
    }

    public Story Load(string folder)
    {
        var manifest = File.ReadAllText(Path.Combine(folder, "project.json"));
        var story = JsonSerializer.Deserialize<Story>(manifest, Json) ?? throw new InvalidDataException("Empty project metadata.");
        if (story.FormatVersion != 1) throw new InvalidDataException("Unsupported project version.");
        story.Memory ??= new();
        if (story.Chapters is null || story.Chapters.Any(c => c.Sections is null)) throw new InvalidDataException("Invalid chapter list.");
        var ids = new HashSet<Guid> { story.Id };
        story.Folder = Path.GetFullPath(folder); story.ManifestHash = AtomicFile.Hash(manifest);
        foreach (var chapter in story.Chapters)
        {
            if (!ids.Add(chapter.Id)) throw new InvalidDataException("Duplicate chapter ID.");
            foreach (var section in chapter.Sections)
            {
                if (!ids.Add(section.Id)) throw new InvalidDataException("Duplicate section ID.");
                string path = SectionPath(story, chapter, section);
                if (!File.Exists(path)) throw new FileNotFoundException($"Missing manuscript: {path}. Restore it from backups before opening.");
                section.Text = File.ReadAllText(path);
                section.DiskHash = AtomicFile.Hash(section.Text);
                section.Statistics = WordCounter.Analyze(section.Text, settings.JoinHyphens, settings.CountNumbers);
            }
        }
        return story;
    }

    public Story Create(string title)
    {
        var story = new Story { Title = title, Target = settings.DefaultStoryTarget };
        var safe = string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        if (safe.Length > 50) safe = safe[..50];
        story.Folder = Path.Combine(settings.StorageRoot, $"{safe}-{story.Id:N}");
        foreach (var name in new[] { "chapters", "notes", "characters", "backups", "recovery", "memory" }) Directory.CreateDirectory(Path.Combine(story.Folder, name));
        var chapter = new Chapter { Title = "Chapter 1" };
        story.Chapters.Add(chapter);
        AddSection(story, chapter, "Opening", "");
        SaveMetadata(story);
        return story;
    }

    public Section AddSection(Story story, Chapter chapter, string title, string text)
    {
        var section = new Section { Title = title, Text = text, Statistics = WordCounter.Analyze(text, settings.JoinHyphens, settings.CountNumbers) };
        AtomicFile.Write(SectionPath(story, chapter, section), text);
        section.DiskHash = AtomicFile.Hash(text);
        chapter.Sections.Add(section);
        return section;
    }

    public void SaveMetadata(Story story)
    {
        string path = Path.Combine(story.Folder, "project.json");
        if (File.Exists(path))
        {
            string previous = File.ReadAllText(path);
            if (AtomicFile.Hash(previous) != story.ManifestHash) throw new IOException("Project metadata changed outside DraftLedger. Reopen the project before changing its structure.");
            SnapshotText(story, "project", ".json", previous, false);
        }
        else if (story.ManifestHash.Length > 0) throw new IOException("Project metadata was removed externally. Saving is paused.");
        story.Modified = DateTimeOffset.Now;
        string serialized = JsonSerializer.Serialize(story, Json);
        AtomicFile.Write(path, serialized);
        story.ManifestHash = AtomicFile.Hash(serialized);
        story.Refresh();
    }

    public void SaveSection(Story story, Chapter chapter, Section section)
    {
        string path = SectionPath(story, chapter, section);
        if (AtomicFile.Hash(section.Text) == section.DiskHash) return;
        if (!File.Exists(path) || AtomicFile.Hash(File.ReadAllText(path)) != section.DiskHash)
        {
            string recovery = Path.Combine(story.Folder, "recovery", $"{section.Id:N}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.md");
            AtomicFile.Write(recovery, section.Text);
            throw new IOException($"This section changed outside DraftLedger. Your current text was saved to:\n{recovery}\n\nUse Reload from disk to open the external version, or copy your text elsewhere first.");
        }
        if (!lastSnapshot.TryGetValue(section.Id, out var time) || DateTimeOffset.Now - time >= TimeSpan.FromMinutes(settings.SnapshotMinutes))
        {
            SnapshotText(story, section.Id.ToString("N"), ".md", File.ReadAllText(path), true);
            lastSnapshot[section.Id] = DateTimeOffset.Now;
        }
        AtomicFile.Write(path, section.Text);
        section.DiskHash = AtomicFile.Hash(section.Text);
        SaveMetadata(story);
    }

    private void SnapshotText(Story story, string prefix, string extension, string text, bool prune)
    {
        string folder = Path.Combine(story.Folder, "backups", prefix);
        Directory.CreateDirectory(folder);
        AtomicFile.Write(Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfffffff") + extension), text);
        if (prune || prefix == "project")
            foreach (var path in Directory.GetFiles(folder, "*" + extension).OrderDescending().Skip(settings.SnapshotRetention)) File.Delete(path);
    }

    public void SnapshotNow(Story story, Section section) => SnapshotText(story, section.Id.ToString("N"), ".md", section.Text, true);
    public string[] Snapshots(Story story, Section section)
    {
        string folder = Path.Combine(story.Folder, "backups", section.Id.ToString("N"));
        return Directory.Exists(folder) ? Directory.GetFiles(folder, "*.md").OrderDescending().ToArray() : [];
    }

    public static string ImportText(string path)
    {
        if (!new[] { ".txt", ".md" }.Contains(Path.GetExtension(path).ToLowerInvariant())) throw new InvalidDataException("Only .txt and .md are supported in this release.");
        // StreamReader recognizes UTF-8/16/32 BOMs. Invalid unmarked UTF-8 fails instead of silently damaging text.
        using var reader = new StreamReader(path, new UTF8Encoding(false, true), true);
        return reader.ReadToEnd();
    }

    public static string Export(Story story, Chapter? chapter, Section? section, bool markdown)
    {
        if (section is not null) return section.Text;
        var result = new StringBuilder();
        if (chapter is null) { result.AppendLine(markdown ? "# " + story.Title : story.Title); if (story.Author.Length > 0) result.AppendLine(story.Author); result.AppendLine(); }
        foreach (var c in chapter is null ? story.Chapters : new List<Chapter> { chapter })
        {
            result.AppendLine(markdown ? "## " + c.Title : c.Title); result.AppendLine();
            foreach (var s in c.Sections) { result.AppendLine(s.Text.TrimEnd()); result.AppendLine(); }
        }
        return result.ToString();
    }

    public static void ExportZip(Story story, string destination)
    {
        string root = Path.GetFullPath(story.Folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(destination).StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Choose a ZIP destination outside this project folder.");
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { ZipFile.CreateFromDirectory(story.Folder, temp, CompressionLevel.Optimal, true); File.Move(temp, destination, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
