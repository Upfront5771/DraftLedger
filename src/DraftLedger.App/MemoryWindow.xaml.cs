using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DraftLedger.Core;

namespace DraftLedger.App;

public partial class MemoryWindow : Window
{
    private readonly Story story;
    private readonly Chapter chapter;
    private readonly Section section;
    private readonly ProjectStore projectStore;
    private readonly StoryMemoryStore memoryStore = new();
    private readonly AiSettingsFile aiFile;
    private StoryMemoryDocument memory;
    private readonly ChatApiClient client = new();
    private CancellationTokenSource? cancellation;
    private bool busy;

    public MemoryWindow(Story story, Chapter chapter, Section section, ProjectStore projectStore, string aiSettingsPath)
    {
        this.story = story; this.chapter = chapter; this.section = section; this.projectStore = projectStore; aiFile = new(aiSettingsPath);
        memory = memoryStore.Load(story);
        InitializeComponent();
        ModeSelector.ItemsSource = new[] { "Off", "Story bible and summaries", "Full memory with retrieval" };
        LoadControls(); RefreshLists();
    }

    private void LoadControls()
    {
        IntroText.Text = $"{story.Title} · Memory is {(story.Memory.Enabled ? "enabled" : "off")}. Canonical changes require your approval.";
        StorySoFarBox.Text = memory.StorySoFar; StyleGuideBox.Text = memory.StyleGuide;
        ModeSelector.SelectedIndex = (int)story.Memory.Mode; AutomaticProposals.IsChecked = story.Memory.AutomaticProposals;
        RequireApproval.IsChecked = true; IncludeThreads.IsChecked = story.Memory.IncludeOpenThreads; ContinuityWarnings.IsChecked = story.Memory.ContinuityWarnings;
        BudgetBox.Text = story.Memory.CharacterBudget.ToString(); PassagesBox.Text = story.Memory.RetrievedPassages.ToString();
    }

    private void CaptureControls()
    {
        memory.StorySoFar = StorySoFarBox.Text; memory.StyleGuide = StyleGuideBox.Text;
        story.Memory.Mode = ModeSelector.SelectedIndex is >= 0 and <= 2 ? (MemoryMode)ModeSelector.SelectedIndex : MemoryMode.Off;
        story.Memory.AutomaticProposals = AutomaticProposals.IsChecked == true; story.Memory.RequireApproval = true;
        story.Memory.IncludeOpenThreads = IncludeThreads.IsChecked == true; story.Memory.ContinuityWarnings = ContinuityWarnings.IsChecked == true;
        if (!int.TryParse(BudgetBox.Text, out int budget) || budget is < 2000 or > 64000) throw new InvalidDataException("Memory budget must be 2,000 to 64,000 characters.");
        if (!int.TryParse(PassagesBox.Text, out int passages) || passages is < 1 or > 20) throw new InvalidDataException("Retrieved passages must be 1 to 20.");
        story.Memory.CharacterBudget = budget; story.Memory.RetrievedPassages = passages;
    }

    private void SaveAll()
    {
        CaptureControls(); memoryStore.Save(story, memory); projectStore.SaveMetadata(story);
        IntroText.Text = $"{story.Title} · Memory is {(story.Memory.Enabled ? "enabled" : "off")}. Canonical changes require your approval.";
        StatusText.Text = "Memory and story settings saved locally.";
    }

    private void Guard(Action action) { try { action(); } catch (Exception ex) { StatusText.Text = ex.Message; MessageBox.Show(this, ex.Message, "Memory & continuity", MessageBoxButton.OK, MessageBoxImage.Warning); } }
    private bool Confirm(string text) => MessageBox.Show(this, text, "Memory & continuity", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    private void RefreshLists()
    {
        FactsList.ItemsSource = null; FactsList.ItemsSource = memory.Facts.OrderByDescending(f => f.Locked).ThenBy(f => f.Category).ThenBy(f => f.Subject).ToList();
        ThreadsList.ItemsSource = null; ThreadsList.ItemsSource = memory.OpenThreads.OrderBy(t => t.Status).ThenBy(t => t.Title).ToList();
        ChaptersList.ItemsSource = null; ChaptersList.ItemsSource = memory.ChapterSummaries.OrderBy(s => story.Chapters.FindIndex(c => c.Id == s.ChapterId)).ToList();
        ProposalsList.ItemsSource = null; ProposalsList.ItemsSource = memory.Proposals.Where(p => p.Status == MemoryProposalStatus.Proposed).OrderBy(p => p.Created).ToList();
        WarningsList.ItemsSource = null; WarningsList.ItemsSource = memory.Warnings.OrderBy(w => w.Resolved).ThenBy(w => w.Subject).ToList();
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Guard(SaveAll);
    private void AddFact_Click(object sender, RoutedEventArgs e) => EditFact(new());
    private void EditFact_Click(object sender, RoutedEventArgs e) { if (FactsList.SelectedItem is MemoryFact fact) EditFact(fact); }
    private void EditFact(MemoryFact fact)
    {
        var values = Dialogs.Form(this, "Story-bible fact", [new("Category", fact.Category, ["Character", "Relationship", "Location", "World", "Object", "Timeline", "Other"]), new("Subject", fact.Subject), new("Canonical fact", fact.Fact, Multiline: true), new("Source", fact.Source), new("Locked", fact.Locked ? "Yes" : "No", ["No", "Yes"])], v => string.IsNullOrWhiteSpace(v["Subject"]) || string.IsNullOrWhiteSpace(v["Canonical fact"]) ? "Enter a subject and fact." : null);
        if (values is null) return; bool add = !memory.Facts.Contains(fact); fact.Category = values["Category"]; fact.Subject = values["Subject"].Trim(); fact.Fact = values["Canonical fact"].Trim(); fact.Source = values["Source"]; fact.Locked = values["Locked"] == "Yes"; fact.Updated = DateTimeOffset.Now; if (add) memory.Facts.Add(fact); RefreshLists();
    }
    private void DeleteFact_Click(object sender, RoutedEventArgs e) { if (FactsList.SelectedItem is MemoryFact fact && Confirm("Delete this canonical fact?")) { memory.Facts.Remove(fact); RefreshLists(); } }
    private void LockFact_Click(object sender, RoutedEventArgs e) { if (FactsList.SelectedItem is MemoryFact fact) { fact.Locked = !fact.Locked; RefreshLists(); } }

    private void AddThread_Click(object sender, RoutedEventArgs e) => EditThread(new());
    private void EditThread_Click(object sender, RoutedEventArgs e) { if (ThreadsList.SelectedItem is OpenPlotThread thread) EditThread(thread); }
    private void EditThread(OpenPlotThread thread)
    {
        var values = Dialogs.Form(this, "Plot thread", [new("Title", thread.Title), new("Detail", thread.Detail, Multiline: true), new("Status", thread.Status, ["Open", "Resolved"]), new("Source", thread.Source)], v => string.IsNullOrWhiteSpace(v["Title"]) ? "Enter a title." : null);
        if (values is null) return; bool add = !memory.OpenThreads.Contains(thread); thread.Title = values["Title"].Trim(); thread.Detail = values["Detail"].Trim(); thread.Status = values["Status"]; thread.Source = values["Source"]; if (add) memory.OpenThreads.Add(thread); RefreshLists();
    }
    private void ToggleThread_Click(object sender, RoutedEventArgs e) { if (ThreadsList.SelectedItem is OpenPlotThread t) { t.Status = t.Status == "Open" ? "Resolved" : "Open"; RefreshLists(); } }
    private void DeleteThread_Click(object sender, RoutedEventArgs e) { if (ThreadsList.SelectedItem is OpenPlotThread t && Confirm("Delete this plot thread?")) { memory.OpenThreads.Remove(t); RefreshLists(); } }

    private void EditChapterSummary_Click(object sender, RoutedEventArgs e)
    {
        if (ChaptersList.SelectedItem is not ChapterMemory summary) return;
        var values = Dialogs.Form(this, "Chapter summary", [new("Summary", summary.Summary, Multiline: true)]); if (values is null) return;
        summary.Summary = values["Summary"].Trim(); var source = story.Chapters.FirstOrDefault(c => c.Id == summary.ChapterId); if (source is not null) summary.SourceHash = StoryMemoryRetrieval.ChapterHash(source); summary.Updated = DateTimeOffset.Now; RefreshLists();
    }
    private void RefreshChapter_Click(object sender, RoutedEventArgs e) { if (ChaptersList.SelectedItem is ChapterMemory summary && story.Chapters.FirstOrDefault(c => c.Id == summary.ChapterId) is Chapter target) _ = AnalyzeAsync([target], "Refresh this chapter's summary and propose any continuity changes."); }

    private void ProposalSelection_Changed(object sender, SelectionChangedEventArgs e) { ProposalContent.Text = ProposalsList.SelectedItem is MemoryProposal p ? p.Content : ""; }
    private void ApproveProposal_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (ProposalsList.SelectedItem is not MemoryProposal proposal) return; proposal.Content = ProposalContent.Text.Trim();
        if (proposal.Content.Length == 0) throw new InvalidDataException("The proposal cannot be blank."); StoryMemoryAnalysis.Approve(story, memory, proposal); RefreshLists(); StatusText.Text = "Proposal approved. Save to write it to disk.";
    });
    private void RejectProposal_Click(object sender, RoutedEventArgs e) { if (ProposalsList.SelectedItem is MemoryProposal p) { p.Status = MemoryProposalStatus.Rejected; RefreshLists(); } }
    private void ToggleWarning_Click(object sender, RoutedEventArgs e) { if (WarningsList.SelectedItem is ContinuityWarning warning) { warning.Resolved = !warning.Resolved; RefreshLists(); } }
    private void DeleteWarning_Click(object sender, RoutedEventArgs e) { if (WarningsList.SelectedItem is ContinuityWarning warning) { memory.Warnings.Remove(warning); RefreshLists(); } }

    private void AnalyzeChapter_Click(object sender, RoutedEventArgs e) => _ = AnalyzeAsync([chapter], "Summarize this chapter and propose story-bible, plot-thread, and continuity updates.");
    private void AnalyzeStory_Click(object sender, RoutedEventArgs e)
    {
        var pending = story.Chapters.Where(c => memory.ChapterSummaries.FirstOrDefault(s => s.ChapterId == c.Id) is not ChapterMemory summary || summary.SourceHash != StoryMemoryRetrieval.ChapterHash(c)).ToList();
        if (pending.Count == 0) { if (Confirm("All chapter summaries match the current manuscript. Synthesize a new story-so-far overview and style guide from the approved summaries? This sends one API request.")) _ = AnalyzeAsync([], "Synthesize the complete story-so-far summary and style guide from the approved chapter summaries. Propose any cross-chapter plot threads or continuity warnings. Do not invent details."); return; }
        if (Confirm($"Analyze {pending.Count:N0} new or changed chapters? DraftLedger sends one request per chapter, stops on the first provider error, and keeps every completed proposal. This may use API credits.")) _ = AnalyzeWholeStoryAsync(pending);
    }
    private async Task AnalyzeAsync(IReadOnlyCollection<Chapter> chapters, string instruction)
    {
        if (busy) return;
        try
        {
            SaveAll(); var ai = aiFile.Load(); var connection = ai.Connections.FirstOrDefault(c => c.Id == ai.LastConnection) ?? throw new InvalidOperationException("Choose an AI connection in AI Writing first.");
            var preset = ai.Presets.FirstOrDefault(p => p.Id == ai.LastPreset) ?? ai.Presets.FirstOrDefault() ?? throw new InvalidOperationException("Create an AI preset first.");
            if (string.IsNullOrWhiteSpace(preset.Model)) throw new InvalidOperationException("Choose a model in AI Writing first.");
            var analysisPreset = JsonSerializer.Deserialize<ModelPreset>(JsonSerializer.Serialize(preset, ProjectStore.Json), ProjectStore.Json)!;
            analysisPreset.Stream = false; analysisPreset.Temperature = 0.2; analysisPreset.MaxTokens = Math.Max(4096, preset.MaxTokens); analysisPreset.Thinking = ThinkingMode.Default;
            var prepared = StoryMemoryAnalysis.BuildRequest(story, chapters, memory, instruction); var body = ChatApiClient.BuildRequest(connection, analysisPreset, prepared, connection.Models.FirstOrDefault(m => m.Id == analysisPreset.Model));
            body["stream"] = false; var resultText = new StringBuilder(); cancellation = new(); busy = true; SetBusy(true); StatusText.Text = $"Analyzing with {analysisPreset.Model}...";
            var result = await client.GenerateAsync(connection, AiSettingsFile.Unprotect(connection), body, text => resultText.Append(text), cancellation.Token);
            var proposals = StoryMemoryAnalysis.Parse(result.Text, story, chapters, section.Id);
            if (chapters.Count == 1) proposals.RemoveAll(p => p.Kind is MemoryProposalKind.StorySummary or MemoryProposalKind.StyleGuide);
            AddUniqueProposals(proposals); memoryStore.Save(story, memory); RefreshLists(); StatusText.Text = $"Added {proposals.Count:N0} proposed updates. Review and approve them before they become canonical.";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; MessageBox.Show(this, ex.Message, "Memory analysis", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { busy = false; cancellation?.Dispose(); cancellation = null; SetBusy(false); }
    }
    private async Task AnalyzeWholeStoryAsync(IReadOnlyList<Chapter> chapters)
    {
        if (busy) return;
        int complete = 0;
        try
        {
            SaveAll(); var ai = aiFile.Load(); var connection = ai.Connections.FirstOrDefault(c => c.Id == ai.LastConnection) ?? throw new InvalidOperationException("Choose an AI connection in AI Writing first.");
            var preset = ai.Presets.FirstOrDefault(p => p.Id == ai.LastPreset) ?? ai.Presets.FirstOrDefault() ?? throw new InvalidOperationException("Create an AI preset first.");
            if (string.IsNullOrWhiteSpace(preset.Model)) throw new InvalidOperationException("Choose a model in AI Writing first.");
            var analysisPreset = JsonSerializer.Deserialize<ModelPreset>(JsonSerializer.Serialize(preset, ProjectStore.Json), ProjectStore.Json)!; analysisPreset.Stream = false; analysisPreset.Temperature = 0.2; analysisPreset.MaxTokens = Math.Max(4096, preset.MaxTokens); analysisPreset.Thinking = ThinkingMode.Default;
            cancellation = new(); busy = true; SetBusy(true);
            foreach (var item in chapters)
            {
                StatusText.Text = $"Analyzing chapter {complete + 1:N0} of {chapters.Count:N0}: {item.Title}";
                var prepared = StoryMemoryAnalysis.BuildRequest(story, [item], memory, "Summarize this chapter and propose only manuscript-supported story-bible, plot-thread, and continuity updates. Leave storySummary and styleGuide blank.");
                var body = ChatApiClient.BuildRequest(connection, analysisPreset, prepared, connection.Models.FirstOrDefault(m => m.Id == analysisPreset.Model)); body["stream"] = false;
                var result = await client.GenerateAsync(connection, AiSettingsFile.Unprotect(connection), body, _ => { }, cancellation.Token);
                var proposals = StoryMemoryAnalysis.Parse(result.Text, story, [item], item.Sections.LastOrDefault()?.Id); proposals.RemoveAll(p => p.Kind is MemoryProposalKind.StorySummary or MemoryProposalKind.StyleGuide); AddUniqueProposals(proposals); complete++; memoryStore.Save(story, memory);
            }
            StatusText.Text = $"Analyzed {complete:N0} chapters and saved proposed updates. Approve chapter summaries, then run analysis again later to refresh the story-so-far overview."; RefreshLists();
        }
        catch (Exception ex) { StatusText.Text = $"Stopped after {complete:N0} completed chapters. Saved proposals were kept. {ex.Message}"; MessageBox.Show(this, StatusText.Text, "Memory analysis", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { busy = false; cancellation?.Dispose(); cancellation = null; SetBusy(false); }
    }
    private void AddUniqueProposals(IEnumerable<MemoryProposal> proposals)
    {
        foreach (var proposal in proposals)
            if (!memory.Proposals.Any(p => p.Status == MemoryProposalStatus.Proposed && p.Kind == proposal.Kind && p.ChapterId == proposal.ChapterId && p.Subject.Equals(proposal.Subject, StringComparison.OrdinalIgnoreCase) && p.Content.Equals(proposal.Content, StringComparison.OrdinalIgnoreCase))) memory.Proposals.Add(proposal);
    }
    private void SetBusy(bool value) { AnalyzeChapterButton.IsEnabled = AnalyzeStoryButton.IsEnabled = !value; }

    private void PreviewContext_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        CaptureControls(); var context = StoryMemoryRetrieval.Build(story, chapter, section, memory, section.Text[^Math.Min(section.Text.Length, 2000)..]);
        var window = new Window { Owner = this, Icon = Icon, Title = "Memory context preview", Width = 800, Height = 650, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new TextBox { Text = $"Included: {string.Join(", ", context.Included)}\nRetrieved: {string.Join("; ", context.Passages.Select(p => p.Chapter + " / " + p.Section))}\nWarnings: {string.Join("; ", context.Warnings)}\n\n{context.Text}", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new("Consolas"), Margin = new Thickness(16) } }; window.ShowDialog();
    });
    private void OpenFiles_Click(object sender, RoutedEventArgs e) => Guard(() => { SaveAll(); Process.Start(new ProcessStartInfo(memoryStore.Folder(story)) { UseShellExecute = true }); });
    private void DeleteMemory_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (!Confirm("Turn memory off and move all saved memory files to the recovery folder?")) return; story.Memory.Mode = MemoryMode.Off; projectStore.SaveMetadata(story); string recovered = memoryStore.DeleteRecoverably(story); memory = new(); LoadControls(); RefreshLists(); StatusText.Text = recovered.Length > 0 ? "Memory moved to: " + recovered : "No saved memory files existed.";
    });
    private void Window_Closing(object? sender, CancelEventArgs e) { if (busy) { e.Cancel = true; cancellation?.Cancel(); return; } try { SaveAll(); client.Dispose(); } catch (Exception ex) { e.Cancel = true; MessageBox.Show(this, ex.Message, "Could not save memory", MessageBoxButton.OK, MessageBoxImage.Warning); } }
}
