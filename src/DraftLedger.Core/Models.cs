using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace DraftLedger.Core;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class Section : Observable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "Untitled section";
    public string Purpose { get; set; } = "";
    [JsonIgnore] public string Text { get; set; } = "";
    [JsonIgnore] public string DiskHash { get; set; } = "";
    [JsonIgnore] public TextStatistics Statistics { get; set; } = TextStatistics.Empty;
    [JsonIgnore] public int Words => Statistics.Words;
    [JsonIgnore] public string TreeLabel => $"{Title}   {Words:N0}";
    public void Refresh() { Notify(nameof(Words)); Notify(nameof(TreeLabel)); }
}

public sealed class Chapter : Observable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "Untitled chapter";
    public string Summary { get; set; } = "";
    public string Status { get; set; } = "Drafting";
    public int Target { get; set; }
    public List<Section> Sections { get; set; } = [];
    [JsonIgnore] public int Words => Sections.Sum(x => x.Words);
    [JsonIgnore] public string TreeLabel => $"{Title}   {Words:N0} · {Status}";
    public void Refresh() { Notify(nameof(Words)); Notify(nameof(TreeLabel)); }
}

public sealed class Story : Observable
{
    public int FormatVersion { get; set; } = 1;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "Untitled story";
    public string Author { get; set; } = "";
    public string Synopsis { get; set; } = "";
    public string Status { get; set; } = "Drafting";
    public bool Archived { get; set; }
    public int Target { get; set; } = 50000;
    public StoryMemoryOptions Memory { get; set; } = new();
    public DateTimeOffset Modified { get; set; } = DateTimeOffset.Now;
    public List<Chapter> Chapters { get; set; } = [];
    [JsonIgnore] public string Folder { get; set; } = "";
    [JsonIgnore] public string ManifestHash { get; set; } = "";
    [JsonIgnore] public int Words => Chapters.Sum(x => x.Words);
    [JsonIgnore] public double Progress => Target > 0 ? Math.Min(100, 100.0 * Words / Target) : 0;
    [JsonIgnore] public string CardDetail => $"{Words:N0} words · {Chapters.Count} chapters";
    [JsonIgnore] public string GoalLabel => Target > 0 ? $"{Words:N0} of {Target:N0} words" : "No word target";
    [JsonIgnore] public string EditedLabel => $"Edited {Modified.LocalDateTime:g}";
    public void Refresh()
    {
        foreach (var name in new[] { nameof(Title), nameof(Words), nameof(Progress), nameof(CardDetail), nameof(GoalLabel), nameof(EditedLabel), nameof(Status) }) Notify(name);
    }
}

public sealed class Settings
{
    public List<string> ProjectFolders { get; set; } = [];
    public string StorageRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DraftLedger");
    public string Theme { get; set; } = "System";
    public string Font { get; set; } = "Georgia";
    public double FontSize { get; set; } = 19;
    public bool SpellCheck { get; set; } = true;
    public string Language { get; set; } = "en-US";
    public bool JoinHyphens { get; set; } = true;
    public bool CountNumbers { get; set; } = true;
    public int SnapshotMinutes { get; set; } = 5;
    public int SnapshotRetention { get; set; } = 50;
    public int DailyTarget { get; set; } = 500;
    public int DefaultStoryTarget { get; set; } = 50000;
}
