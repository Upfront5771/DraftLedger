using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using DraftLedger.Core;
using Microsoft.Win32;

namespace DraftLedger.App;

public partial class MainWindow : Window
{
    private static readonly string ConfigFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DraftLedger");
    private static readonly string ConfigPath = Path.Combine(ConfigFolder, "settings.json");
    private static readonly string AiConfigPath = Path.Combine(ConfigFolder, "ai-settings.json");
    private Settings settings;
    private ProjectStore store;
    private List<Story> stories = [];
    private LibraryIndex? index;
    private Story? story;
    private Chapter? chapter;
    private Section? section;
    private bool loading = true, textDirty, metadataDirty, focusMode, saving;
    private int sessionDelta;
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private Point dragStart;
    private string? renderedSource;
    private object? dragNode;
    private GridLength detailsWidth = new(360);
    private static readonly string[] Statuses = ["Planned", "Drafting", "Revising", "Complete"];
    private static readonly string[] EditorFonts = ["Georgia", "Segoe UI", "Aptos", "Arial", "Calibri", "Cambria", "Cascadia Mono", "Consolas", "Courier New", "Garamond", "Palatino Linotype", "Tahoma", "Times New Roman", "Trebuchet MS", "Verdana"];
    private AiWritingWindow? aiWritingSurface;
    private readonly ChatApiClient synopsisClient = new();
    private CancellationTokenSource? synopsisCancellation;
    private bool synopsisBusy;

    public MainWindow()
    {
        settings = File.Exists(ConfigPath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(ConfigPath), ProjectStore.Json) ?? new() : new();
        settings.Theme = ThemeCatalog.NormalizeName(settings.Theme);
        settings.SnapshotRetention = Math.Clamp(settings.SnapshotRetention, 1, 1000);
        settings.SnapshotMinutes = Math.Clamp(settings.SnapshotMinutes, 1, 1440);
        settings.FontSize = Math.Clamp(settings.FontSize, 10, 48);
        store = new(settings);
        InitializeComponent();
        InitializeInlineSettings();
        ApplyAppearance();
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SavePending(); };
        LoadLibrary(); loading = false; RefreshLibrary();
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) { SaveStatus.Text = "Action needs attention"; MessageBox.Show(this, ex.Message, "DraftLedger", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private bool Confirm(string message) => MessageBox.Show(this, message, "DraftLedger", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    private void SaveSettings() => AtomicFile.Write(ConfigPath, JsonSerializer.Serialize(settings, ProjectStore.Json));

    private void LoadLibrary()
    {
        stories = store.LoadLibrary(out var errors);
        if (errors.Count > 0) Dispatcher.BeginInvoke(new Action(() => MessageBox.Show(this, "These projects could not be loaded. Their files were left in place:\n\n" + string.Join("\n\n", errors), "Project recovery", MessageBoxButton.OK, MessageBoxImage.Warning)));
        RefreshIndex();
    }

    private void RefreshIndex()
    {
        try { index ??= new(Path.Combine(ConfigFolder, "library.sqlite")); index.Rebuild(stories); }
        catch (Exception ex) { index?.Dispose(); index = null; SaveStatus.Text = "Library cache unavailable; manuscripts remain accessible"; Debug.WriteLine(ex); }
    }

    private void RefreshLibrary()
    {
        if (LibrarySearch is null) return;
        string query = LibrarySearch.Text.Trim();
        var shown = stories.Where(s => s.Archived == (ShowArchived.IsChecked == true) && (s.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || s.Synopsis.Contains(query, StringComparison.OrdinalIgnoreCase))).OrderByDescending(s => s.Modified).ToList();
        StoryCards.ItemsSource = shown;
        EmptyLibrary.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LibrarySummary.Text = $"{stories.Count(s => !s.Archived)} active stories   ·   {stories.Sum(s => s.Words):N0} words across your library";
        StatusCounts.Text = $"Library: {stories.Sum(s => s.Words):N0} words";
    }

    private bool SavePending(bool showError = true)
    {
        if (loading || saving || story is null) return true;
        saveTimer.Stop(); saving = true;
        try
        {
            if (textDirty && section is not null && chapter is not null)
            {
                store.SaveSection(story, chapter, section); textDirty = false;
            }
            if (metadataDirty) { store.SaveMetadata(story); metadataDirty = false; }
            SaveStatus.Text = "Saved locally · " + DateTime.Now.ToString("HH:mm");
            return true;
        }
        catch (Exception ex)
        {
            SaveStatus.Text = "Save paused · your draft is still open";
            if (showError) MessageBox.Show(this, ex.Message, "Could not save", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { saving = false; }
    }

    private void ScheduleSave() { SaveStatus.Text = "Unsaved changes…"; if (!saveTimer.IsEnabled) saveTimer.Start(); }
    private void CommitStructure(Action change, object? select = null)
    {
        if (story is null || !SavePending()) return;
        change(); metadataDirty = true;
        if (!SavePending()) return;
        RefreshOutline(select); RefreshStatistics();
    }

    private void OpenStory(Story target)
    {
        if (!SavePending()) return;
        if (aiWritingSurface is not null && !aiWritingSurface.TryShutdown()) return; AiWritingHost.Content = null; aiWritingSurface = null;
        var fresh = store.Load(target.Folder);
        int i = stories.IndexOf(target); if (i >= 0) stories[i] = fresh;
        story = fresh; chapter = null; section = null;
        textDirty = metadataDirty = false; sessionDelta = 0;
        LibraryPanel.Visibility = Visibility.Collapsed; WorkspacePanel.Visibility = Visibility.Visible;
        StoryTitle.Text = story.Title; Title = story.Title + " | DraftLedger";
        RefreshMemorySummary();
        RefreshOutline();
        var firstChapter = story.Chapters.FirstOrDefault();
        SetSection(firstChapter, firstChapter?.Sections.FirstOrDefault());
        LoadStoryDetailsPanel(); InitializeAiWritingSurface();
        SelectNode(section ?? (object?)firstChapter); RefreshStatistics();
    }

    private void RefreshOutline(object? select = null)
    {
        loading = true; Outline.ItemsSource = null; Outline.ItemsSource = story?.Chapters; loading = false;
        foreach (var c in story?.Chapters ?? []) { c.Refresh(); foreach (var s in c.Sections) s.Refresh(); }
        story?.Refresh();
        if (select is not null) SelectNode(select);
    }

    private void SelectNode(object? node)
    {
        if (node is null) return;
        Outline.UpdateLayout();
        foreach (var c in story?.Chapters ?? [])
        {
            if (Outline.ItemContainerGenerator.ContainerFromItem(c) is not TreeViewItem parent) continue;
            if (ReferenceEquals(c, node)) { parent.IsSelected = true; parent.BringIntoView(); return; }
            if (node is Section s && c.Sections.Contains(s))
            {
                parent.IsExpanded = true; parent.UpdateLayout();
                if (parent.ItemContainerGenerator.ContainerFromItem(s) is TreeViewItem child) { child.IsSelected = true; child.BringIntoView(); }
                return;
            }
        }
    }

    private void SetSection(Chapter? nextChapter, Section? nextSection)
    {
        loading = true;
        chapter = nextChapter; section = nextSection;
        Editor.IsUndoEnabled = false; Editor.Text = section?.Text ?? ""; Editor.IsUndoEnabled = true; Editor.IsEnabled = section is not null;
        Reader.IsEnabled = section is not null; renderedSource = null;
        SceneNotes.Text = section?.Purpose ?? ""; SceneNotes.IsEnabled = section is not null;
        SectionTitle.Text = section?.Title ?? "Add a section to start writing";
        Breadcrumb.Text = chapter?.Title ?? "";
        loading = false; if (chapter is not null && section is not null) aiWritingSurface?.SetTarget(chapter, section); if (ReadTab.IsSelected) RefreshReading(); RefreshStatistics();
    }

    private void InitializeAiWritingSurface()
    {
        if (story is null || chapter is null || section is null) { AiWritingHost.Content = null; return; }
        aiWritingSurface = new AiWritingWindow(AiConfigPath, BuildWritingContext, AppendGeneratedText, Path.Combine(story.Folder, "recovery"), story, chapter, section, store);
        AiWritingHost.Content = aiWritingSurface.DetachWritingSurface(settings.Font, settings.FontSize, settings.SpellCheck, settings.Language);
        RefreshAiConfigurationSummary();
    }

    private void RefreshAiConfigurationSummary()
    {
        try
        {
            var ai = new AiSettingsFile(AiConfigPath).Load();
            var connection = ai.Connections.FirstOrDefault(c => c.Id == ai.LastConnection) ?? ai.Connections.FirstOrDefault();
            var presetValue = ai.Presets.FirstOrDefault(p => p.Id == ai.LastPreset) ?? ai.Presets.FirstOrDefault();
            ApiConnectionSummary.Text = connection?.Name ?? "No connection selected";
            ApiPresetSummary.Text = presetValue is null ? "No preset selected" : $"{presetValue.Name} · {presetValue.Model}";
        }
        catch (Exception ex)
        {
            ApiConnectionSummary.Text = "API settings need attention";
            ApiPresetSummary.Text = ex.Message;
        }
    }

    private void RefreshStatistics()
    {
        if (story is null) return;
        var stats = section?.Statistics ?? TextStatistics.Empty;
        BigWords.Text = stats.Words.ToString("N0");
        DetailStats.Text = $"{stats.Characters:N0} characters\n{stats.CharactersWithoutSpaces:N0} without whitespace\n{stats.Paragraphs:N0} paragraphs\n{stats.Sentences:N0} sentences\n{stats.Pages:N1} estimated pages\n{stats.ReadingMinutes:N1} min to read\n{stats.SpeakingMinutes:N1} min to speak";
        UpdateSelectionCount();
        GoalText.Text = story.GoalLabel; GoalProgress.Value = story.Progress;
        SessionText.Text = $"Session: {sessionDelta:+#,0;-#,0;0} words (net)\nDaily target: {settings.DailyTarget:N0} words";
        StatusCounts.Text = $"Section: {stats.Words:N0}  |  Chapter: {chapter?.Words ?? 0:N0}  |  Story: {story.Words:N0}  |  Library: {stories.Sum(x => x.Words):N0}";
        chapter?.Refresh(); section?.Refresh(); story.Refresh();
        RefreshMemorySummary();
    }

    private void RefreshMemorySummary()
    {
        if (MemoryEnabled is null) return;
        loading = true; MemoryEnabled.IsChecked = story?.Memory.Enabled == true; loading = false;
        if (story is null) { MemorySummary.Text = "Off for this story"; return; }
        if (!story.Memory.Enabled) { MemorySummary.Text = "Off. No memory is added to AI requests."; return; }
        try
        {
            var memory = new StoryMemoryStore().Load(story);
            int pending = memory.Proposals.Count(p => p.Status == MemoryProposalStatus.Proposed);
            int stale = memory.ChapterSummaries.Count(s => story.Chapters.FirstOrDefault(c => c.Id == s.ChapterId) is Chapter c && s.SourceHash.Length > 0 && s.SourceHash != StoryMemoryRetrieval.ChapterHash(c));
            string mode = story.Memory.Mode == MemoryMode.FullRetrieval ? "Full retrieval" : "Bible and summaries";
            MemorySummary.Text = $"{mode}: {memory.Facts.Count:N0} facts, {memory.OpenThreads.Count(t => t.Status == "Open"):N0} open threads, {pending:N0} proposed updates" + (stale > 0 ? $", {stale:N0} stale summaries" : "");
        }
        catch (Exception ex) { MemorySummary.Text = "Memory needs attention: " + ex.Message; }
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (loading || section is null) return;
        int before = section.Words; section.Text = Editor.Text;
        section.Statistics = WordCounter.Analyze(section.Text, settings.JoinHyphens, settings.CountNumbers);
        sessionDelta += section.Words - before; textDirty = true;
        if (ReadTab.IsSelected) RefreshReading();
        RefreshStatistics(); ScheduleSave();
    }
    private void Editor_SelectionChanged(object sender, RoutedEventArgs e) { if (!loading) UpdateSelectionCount(); }
    private void Reader_SelectionChanged(object sender, RoutedEventArgs e) { if (!loading) UpdateSelectionCount(); }
    private void UpdateSelectionCount()
    {
        if (SelectionStats is null) return;
        string selectionText = ReadTab.IsSelected ? Reader.Selection.Text : Editor.SelectedText;
        SelectionStats.Text = $"Selection: {WordCounter.Count(selectionText, settings.JoinHyphens, settings.CountNumbers):N0} words";
    }
    private void WritingTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || !ReferenceEquals(e.Source, WritingTabs)) return;
        if (ReadTab.IsSelected)
        {
            FindPanel.Visibility = Visibility.Collapsed;
            RefreshReading();
            EditorHint.Text = "Preview · Select and copy formatted text. Switch to Editor to make changes.";
        }
        else if (AiTab.IsSelected) EditorHint.Text = "AI output remains separate until you choose Append to section."; else EditorHint.Text = "Use *italics* or **bold**. Your draft saves automatically.";
        UpdateSelectionCount();
    }
    private void RefreshReading()
    {
        if (renderedSource == Editor.Text) return;
        try
        {
            Reader.Document = ReadingRenderer.Render(Editor.Text, settings.Font, settings.FontSize, (Brush)FindResource("InkBrush"));
            Reader.Document.Language = XmlLanguage.GetLanguage(settings.Language);
            renderedSource = Editor.Text;
        }
        catch (Exception ex)
        {
            // Keep both modes usable even if a particular document cannot be formatted.
            Reader.Document = new System.Windows.Documents.FlowDocument(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(Editor.Text)))
            { FontFamily = new FontFamily(settings.Font), FontSize = settings.FontSize, FontWeight = FontWeights.Normal, Foreground = (Brush)FindResource("InkBrush"), PagePadding = new Thickness(44, 30, 44, 54) };
            SaveStatus.Text = "Preview formatting unavailable; showing original text";
            Debug.WriteLine(ex);
        }
        Reader.ScrollToHome();
    }
    private void FocusWritingPane() { if (AiTab.IsSelected) aiWritingSurface?.FocusInput(); else if (ReadTab.IsSelected) Reader.Focus(); else Editor.Focus(); }
    private void SceneNotes_TextChanged(object sender, TextChangedEventArgs e) { if (!loading && section is not null) { section.Purpose = SceneNotes.Text; metadataDirty = true; ScheduleSave(); } }
    private void Outline_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (loading || story is null) return;
        if (!SavePending()) { loading = true; SelectNode(section ?? (object?)chapter); loading = false; return; }
        if (e.NewValue is Chapter c) SetSection(c, c.Sections.FirstOrDefault());
        else if (e.NewValue is Section s) SetSection(story.Chapters.First(c => c.Sections.Contains(s)), s);
    }

    private void LibraryFilter_Changed(object sender, RoutedEventArgs e) { if (!loading) RefreshLibrary(); }
    private void Library_Click(object sender, RoutedEventArgs e)
    {
        if (!SavePending()) return;
        if (focusMode) ToggleFocus();
        WorkspacePanel.Visibility = Visibility.Collapsed; LibraryPanel.Visibility = Visibility.Visible;
        RefreshIndex(); RefreshLibrary(); Title = "DraftLedger";
    }
    private void NewStory_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (!SavePending()) return;
        string? name = Dialogs.Name(this, "New story"); if (name is null) return;
        var created = store.Create(name); stories.Add(created); OpenStory(created);
    });
    private void OpenCard_Click(object sender, RoutedEventArgs e) => Guard(() => { if (((Button)sender).Tag is Story s) OpenStory(s); });
    private void StoryCards_DoubleClick(object sender, MouseButtonEventArgs e) => Guard(() => { if (StoryCards.SelectedItem is Story s && FindAncestor<Button>(e.OriginalSource as DependencyObject) is null) OpenStory(s); });
    private void ArchiveCard_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (!SavePending() || ((Button)sender).Tag is not Story s) return;
        s.Archived = !s.Archived;
        try { store.SaveMetadata(s); } catch { s.Archived = !s.Archived; throw; }
        RefreshLibrary(); RefreshIndex();
    });

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (!SavePending()) return;
        var dialog = new OpenFolderDialog { Title = "Choose a project folder containing project.json" };
        if (dialog.ShowDialog(this) != true) return;
        var loaded = store.Load(dialog.FolderName);
        var existing = stories.FirstOrDefault(s => s.Id == loaded.Id);
        if (existing is not null && !string.Equals(existing.Folder, loaded.Folder, StringComparison.OrdinalIgnoreCase)) throw new IOException("This story is already registered at another location. Use that copy, or change your storage location before opening a moved library.");
        if (existing is null) { stories.Add(loaded); settings.ProjectFolders.Add(loaded.Folder); SaveSettings(); }
        OpenStory(existing ?? loaded);
    });

    private void AddChapter_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (story is null) return; var name = Dialogs.Name(this, "New chapter", $"Chapter {story.Chapters.Count + 1}"); if (name is null) return;
        var c = new Chapter { Title = name };
        CommitStructure(() => { story.Chapters.Add(c); store.AddSection(story, c, "New section", ""); }, c);
    });
    private void AddSection_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (story is null || chapter is null) { MessageBox.Show(this, "Add a chapter first."); return; }
        var name = Dialogs.Name(this, "New section"); if (name is null) return;
        Section? added = null; var c = chapter;
        CommitStructure(() => added = store.AddSection(story, c, name, ""));
        if (added is not null) SelectNode(added);
    });
    private object? SelectedNode => Outline.SelectedItem ?? (object?)section ?? chapter;
    private void Rename_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        var node = SelectedNode; if (node is null) return;
        var name = Dialogs.Name(this, "Rename", node is Chapter c ? c.Title : ((Section)node).Title); if (name is null) return;
        CommitStructure(() => { if (node is Chapter c) c.Title = name; else ((Section)node).Title = name; }, node);
        SectionTitle.Text = section?.Title ?? ""; Breadcrumb.Text = chapter?.Title ?? "";
    });
    private void NodeDetails_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (SelectedNode is Chapter c)
        {
            var v = Dialogs.Form(this, "Chapter details", [new("Status", c.Status, Statuses), new("Word target", c.Target.ToString()), new("Summary", c.Summary, Multiline: true)], v => ValidNonnegative(v["Word target"]) ? null : "Enter a word target from 0 to 10,000,000.");
            if (v is not null) CommitStructure(() => { c.Status = v["Status"]; c.Target = int.Parse(v["Word target"]); c.Summary = v["Summary"]; }, c);
        }
        else if (SelectedNode is Section) SceneNotes.Focus();
    });
    private void Duplicate_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (story is null) return;
        object? added = null; var node = SelectedNode;
        CommitStructure(() =>
        {
            if (node is Chapter c)
            {
                var copy = new Chapter { Title = c.Title + " (copy)", Summary = c.Summary, Target = c.Target, Status = c.Status };
                story.Chapters.Insert(story.Chapters.IndexOf(c) + 1, copy);
                foreach (var s in c.Sections) { var clone = store.AddSection(story, copy, s.Title, s.Text); clone.Purpose = s.Purpose; }
                added = copy;
            }
            else if (node is Section s && chapter is not null)
            {
                var clone = store.AddSection(story, chapter, s.Title + " (copy)", s.Text); clone.Purpose = s.Purpose; added = clone;
            }
        });
        if (added is not null) SelectNode(added);
    });
    private void Delete_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (story is null || SelectedNode is not object node || !SavePending()) return;
        string title = node is Chapter c ? c.Title : ((Section)node).Title;
        if (!Confirm($"Remove '{title}' from the manuscript? Its files and a metadata snapshot will remain in the project folder for recovery.")) return;
        CommitStructure(() => { if (node is Chapter c) story.Chapters.Remove(c); else chapter?.Sections.Remove((Section)node); });
        var next = story.Chapters.FirstOrDefault(); SetSection(next, next?.Sections.FirstOrDefault()); SelectNode(section ?? (object?)next);
    });
    private void MoveUp_Click(object sender, RoutedEventArgs e) => Guard(() => MoveNode(-1));
    private void MoveDown_Click(object sender, RoutedEventArgs e) => Guard(() => MoveNode(1));
    private void MoveNode(int offset)
    {
        if (story is null) return; var node = SelectedNode;
        CommitStructure(() =>
        {
            if (node is Chapter c) { int i = story.Chapters.IndexOf(c), j = i + offset; if (j >= 0 && j < story.Chapters.Count) { story.Chapters.RemoveAt(i); story.Chapters.Insert(j, c); } }
            else if (node is Section s && chapter is not null) { int i = chapter.Sections.IndexOf(s), j = i + offset; if (j >= 0 && j < chapter.Sections.Count) { chapter.Sections.RemoveAt(i); chapter.Sections.Insert(j, s); } }
        }, node);
    }

    private void StoryDetails_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (story is null) return; StoryDetailsExpander.IsExpanded = true; StoryDetailsExpander.BringIntoView(); StoryTitleBox.Focus();
    });
    private static bool ValidNonnegative(string value) => int.TryParse(value, out var n) && n is >= 0 and <= 10000000;
    private void LoadStoryDetailsPanel() { if (story is null) return; bool wasLoading = loading; loading = true; StoryStatusSelector.ItemsSource = Statuses; StoryTitleBox.Text = story.Title; StoryAuthorBox.Text = story.Author; StoryStatusSelector.SelectedItem = story.Status; StoryTargetBox.Text = story.Target.ToString(CultureInfo.InvariantCulture); StorySynopsisBox.Text = story.Synopsis; StoryDetailsStatus.Text = "Changes stay here until you save them."; loading = wasLoading; }
    private void SaveStoryDetails_Click(object sender, RoutedEventArgs e) => Guard(() => { if (story is null) return; if (string.IsNullOrWhiteSpace(StoryTitleBox.Text)) { StoryDetailsStatus.Text = "Enter a story title."; return; } if (!ValidNonnegative(StoryTargetBox.Text)) { StoryDetailsStatus.Text = "Enter a valid word target."; return; } story.Title = StoryTitleBox.Text.Trim(); story.Author = StoryAuthorBox.Text.Trim(); story.Status = StoryStatusSelector.SelectedItem?.ToString() ?? "Drafting"; story.Target = int.Parse(StoryTargetBox.Text); story.Synopsis = StorySynopsisBox.Text.Trim(); metadataDirty = true; if (!SavePending()) return; StoryTitle.Text = story.Title; Title = story.Title + " | DraftLedger"; StoryDetailsStatus.Text = "Story details saved locally."; RefreshStatistics(); });
    private async void GenerateSynopsis_Click(object sender, RoutedEventArgs e)
    {
        if (story is null || synopsisBusy) return;
        try
        {
            if (!SavePending()) return; var ai = new AiSettingsFile(AiConfigPath).Load(); var connection = ai.Connections.FirstOrDefault(c => c.Id == ai.LastConnection) ?? throw new InvalidOperationException("Choose an API connection first."); var preset = ai.Presets.FirstOrDefault(p => p.Id == ai.LastPreset) ?? throw new InvalidOperationException("Choose an AI preset first.");
            string manuscript = ProjectStore.Export(story, null, null, false); var messages = new List<ChatMessage> { new("system", "Write one concise paragraph that accurately summarizes the supplied story. Do not invent details. Return only the synopsis."), new("user", manuscript.Length <= 210000 ? manuscript : manuscript[..105000] + "\n\n" + manuscript[^105000..]) }; var prepared = new PreparedChat(messages, [], [], messages.Sum(m => m.Content.Length));
            var requestPreset = JsonSerializer.Deserialize<ModelPreset>(JsonSerializer.Serialize(preset, ProjectStore.Json), ProjectStore.Json)!; requestPreset.Stream = false; requestPreset.Temperature = 0.3; requestPreset.MaxTokens = Math.Max(700, Math.Min(2000, preset.MaxTokens)); var body = ChatApiClient.BuildRequest(connection, requestPreset, prepared, connection.Models.FirstOrDefault(m => m.Id == requestPreset.Model)); body["stream"] = false;
            synopsisCancellation = new(); synopsisBusy = true; GenerateSynopsisButton.IsEnabled = false; var result = await synopsisClient.GenerateAsync(connection, AiSettingsFile.Unprotect(connection), body, _ => { }, synopsisCancellation.Token); StorySynopsisBox.Text = result.Text.Trim(); StoryDetailsStatus.Text = "Synopsis generated. Edit it, then save story details.";
        }
        catch (Exception ex) { StoryDetailsStatus.Text = ex.Message; }
        finally { synopsisBusy = false; synopsisCancellation?.Dispose(); synopsisCancellation = null; GenerateSynopsisButton.IsEnabled = true; }
    }

    private void Import_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (!SavePending()) return;
        var file = new OpenFileDialog { Filter = "Text and Markdown|*.txt;*.md", Title = "Import manuscript text" };
        if (file.ShowDialog(this) != true) return;
        string content = ProjectStore.ImportText(file.FileName);
        string name = Path.GetFileNameWithoutExtension(file.FileName);
        bool inStory = WorkspacePanel.Visibility == Visibility.Visible && story is not null;
        var options = new List<string> { "Create new story" };
        if (inStory) { options.Add("Add new chapter"); if (chapter is not null) options.Add("Add section to current chapter"); if (section is not null) options.Add("Replace current section"); }
        var v = Dialogs.Form(this, "Import text", [new("Destination", inStory && chapter is not null ? "Add section to current chapter" : options[0], options.ToArray()), new("Title", name)], v => string.IsNullOrWhiteSpace(v["Title"]) ? "Enter a title." : null);
        if (v is null) return;
        if (v["Destination"] == "Create new story")
        {
            var created = store.Create(v["Title"]); var c = created.Chapters[0]; var s = c.Sections[0]; s.Title = v["Title"]; s.Text = content; s.Statistics = WordCounter.Analyze(content, settings.JoinHyphens, settings.CountNumbers); store.SaveSection(created, c, s); store.SaveMetadata(created); stories.Add(created); OpenStory(created);
        }
        else if (v["Destination"] == "Replace current section")
        {
            if (!Confirm("Replace this section's text? A snapshot of the current draft will be kept.")) return;
            store.SnapshotNow(story!, section!); Editor.Text = content; SavePending();
        }
        else
        {
            Section? added = null;
            CommitStructure(() =>
            {
                var c = chapter;
                if (v["Destination"] == "Add new chapter") { c = new Chapter { Title = v["Title"] }; story!.Chapters.Add(c); }
                added = store.AddSection(story!, c!, v["Title"], content);
            });
            if (added is not null) SelectNode(added);
        }
    });

    private void Export_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (story is null || !SavePending()) return;
        var scopes = new List<string> { "Complete story" }; if (chapter is not null) scopes.Add("Current chapter"); if (section is not null) scopes.Add("Current section");
        var v = Dialogs.Form(this, "Export manuscript", [new("Scope", scopes[0], scopes.ToArray()), new("Format", "Markdown (.md)", ["Markdown (.md)", "Plain text (.txt)"])]);
        if (v is null) return; bool md = v["Format"].StartsWith("Markdown");
        var file = new SaveFileDialog { FileName = SafeFilename(story.Title), DefaultExt = md ? ".md" : ".txt", Filter = md ? "Markdown|*.md" : "Plain text|*.txt" };
        if (file.ShowDialog(this) != true) return;
        EnsureExportOutsideManuscript(file.FileName);
        AtomicFile.Write(file.FileName, ProjectStore.Export(story, v["Scope"] == "Complete story" ? null : chapter, v["Scope"] == "Current section" ? section : null, md)); SaveStatus.Text = "Exported " + Path.GetFileName(file.FileName);
    });
    private static string SafeFilename(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    private void EnsureExportOutsideManuscript(string path)
    {
        string full = Path.GetFullPath(path);
        if (stories.Any(s => full.StartsWith(Path.GetFullPath(s.Folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) throw new IOException("Export to a location outside your project folders to protect the original manuscript.");
    }
    private void Backup_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (story is null || !SavePending()) return;
        var file = new SaveFileDialog { FileName = SafeFilename(story.Title) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmm"), DefaultExt = ".zip", Filter = "Project backup|*.zip" };
        if (file.ShowDialog(this) == true) { ProjectStore.ExportZip(story, file.FileName); SaveStatus.Text = "Project backup created"; }
    });
    private void ShowFiles_Click(object sender, RoutedEventArgs e) => Guard(() => { if (story is not null) Process.Start(new ProcessStartInfo(story.Folder) { UseShellExecute = true }); });
    private void Snapshot_Click(object sender, RoutedEventArgs e) => Guard(() => { if (story is not null && section is not null && SavePending()) { store.SnapshotNow(story, section); SaveStatus.Text = "Snapshot saved"; } });
    private void History_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (story is null || section is null || !SavePending()) return;
        var files = store.Snapshots(story, section);
        if (files.Length == 0) { MessageBox.Show(this, "No snapshots yet. Use Snapshot now to save a version."); return; }
        var chosen = Dialogs.Select(this, "Section version history (UTC)", files, path => Path.GetFileNameWithoutExtension(path), File.ReadAllText);
        if (chosen is null || !Confirm("Restore this version? The current draft will be saved as a snapshot first.")) return;
        string restored = File.ReadAllText(chosen); store.SnapshotNow(story, section); Editor.Text = restored; SavePending();
    });
    private void Reload_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (story is null || !Confirm("Reload this project from disk? Any unsaved editor text will first be copied to the recovery folder.")) return;
        if (section is not null && textDirty) AtomicFile.Write(Path.Combine(story.Folder, "recovery", $"{section.Id:N}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.md"), Editor.Text);
        if (metadataDirty) AtomicFile.Write(Path.Combine(story.Folder, "recovery", $"project-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.json"), JsonSerializer.Serialize(story, ProjectStore.Json));
        var target = story; textDirty = metadataDirty = false; saveTimer.Stop(); OpenStory(target);
    });

    private void Find_Click(object sender, RoutedEventArgs e) { EditorTab.IsSelected = true; FindPanel.Visibility = Visibility.Visible; FindText.Focus(); }
    private void CloseFind_Click(object sender, RoutedEventArgs e) { FindPanel.Visibility = Visibility.Collapsed; FocusWritingPane(); }
    private void FindNext_Click(object sender, RoutedEventArgs e) => FindNext();
    private void FindNext()
    {
        if (FindText.Text.Length == 0) return;
        int start = Math.Min(Editor.Text.Length, Editor.SelectionStart + Editor.SelectionLength);
        int position = Editor.Text.IndexOf(FindText.Text, start, StringComparison.OrdinalIgnoreCase);
        if (position < 0) position = Editor.Text.IndexOf(FindText.Text, StringComparison.OrdinalIgnoreCase);
        FindMessage.Text = position < 0 ? "No match in this section." : "Match in current section.";
        if (position >= 0) { Editor.Select(position, FindText.Text.Length); Editor.ScrollToLine(Editor.GetLineIndexFromCharacterIndex(position)); Editor.Focus(); }
    }
    private void Replace_Click(object sender, RoutedEventArgs e)
    {
        if (section is null || FindText.Text.Length == 0) return;
        if (string.Equals(Editor.SelectedText, FindText.Text, StringComparison.OrdinalIgnoreCase)) Editor.SelectedText = ReplaceText.Text;
        FindNext();
    }
    private void ReplaceAll_Click(object sender, RoutedEventArgs e)
    {
        if (section is null || FindText.Text.Length == 0) return;
        string replaced = Editor.Text.Replace(FindText.Text, ReplaceText.Text, StringComparison.OrdinalIgnoreCase);
        if (replaced == Editor.Text) { FindMessage.Text = "No matching text to replace."; return; }
        Editor.BeginChange(); try { Editor.SelectAll(); Editor.SelectedText = replaced; } finally { Editor.EndChange(); }
        FindMessage.Text = "Replaced all matches in this section. Ctrl+Z to undo.";
    }
    private sealed record SearchHit(Chapter Chapter, Section Section, int Position, string Snippet);
    private void SearchStory_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (story is null || FindText.Text.Length == 0 || !SavePending()) return;
        var hits = new List<SearchHit>(); string query = FindText.Text;
        foreach (var c in story.Chapters) foreach (var s in c.Sections)
        {
            if (hits.Count >= 1000) break;
            int p = 0;
            while ((p = s.Text.IndexOf(query, p, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int begin = Math.Max(0, p - 35), length = Math.Min(120, s.Text.Length - begin);
                hits.Add(new(c, s, p, s.Text.Substring(begin, length).ReplaceLineEndings(" "))); p += query.Length;
                if (hits.Count >= 1000) break;
            }
            if (hits.Count >= 1000) break;
        }
        FindMessage.Text = $"{hits.Count:N0} matches" + (hits.Count >= 1000 ? " (first 1,000 shown)" : "");
        if (hits.Count == 0) return;
        var hit = Dialogs.Select(this, "Search this story", hits, h => $"{h.Chapter.Title} / {h.Section.Title}: {h.Snippet}");
        if (hit is not null) { SelectNode(hit.Section); Editor.Select(hit.Position, query.Length); Editor.ScrollToLine(Editor.GetLineIndexFromCharacterIndex(hit.Position)); Editor.Focus(); }
    });

    private void Focus_Click(object sender, RoutedEventArgs e) => ToggleFocus();
    private void AiWriting_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (story is null || chapter is null || section is null)
        {
            MessageBox.Show(this, "Open a story section before using AI writing.", "AI writing", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!SavePending()) return;
        aiWritingSurface?.SaveState();
        var window = new AiWritingWindow(AiConfigPath, BuildWritingContext, AppendGeneratedText, Path.Combine(story.Folder, "recovery"), story, chapter, section, store) { Owner = this };
        window.ShowDialog();
        aiWritingSurface?.ReloadConfiguration();
        RefreshAiConfigurationSummary();
        RefreshStatistics();
    });

    private void MemoryEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (loading || story is null) return;
        Guard(() =>
        {
            story.Memory.Mode = MemoryEnabled.IsChecked == true ? (story.Memory.Mode == MemoryMode.Off ? MemoryMode.FullRetrieval : story.Memory.Mode) : MemoryMode.Off;
            metadataDirty = true; SavePending();
            if (story.Memory.Enabled) { var memoryStore = new StoryMemoryStore(); var memory = memoryStore.Load(story); memoryStore.Save(story, memory); }
            RefreshMemorySummary();
        });
    }

    private void Memory_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (story is null || chapter is null || section is null) { MessageBox.Show(this, "Open a story section first."); return; }
        if (!SavePending()) return;
        var window = new MemoryWindow(story, chapter, section, store, AiConfigPath) { Owner = this }; window.ShowDialog(); RefreshMemorySummary();
    });

    private void MemoryPreview_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (story is null || chapter is null || section is null) return;
        if (!story.Memory.Enabled) { MessageBox.Show(this, "Enable long-story memory for this story first.", "Memory & continuity"); return; }
        var memory = new StoryMemoryStore().Load(story); var context = StoryMemoryRetrieval.Build(story, chapter, section, memory, section.Text[^Math.Min(section.Text.Length, 2000)..]);
        var preview = new Window { Owner = this, Icon = Icon, Title = "Memory context preview", Width = 800, Height = 650, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new TextBox { Text = $"Included: {string.Join(", ", context.Included)}\nRetrieved: {string.Join("; ", context.Passages.Select(p => p.Chapter + " / " + p.Section))}\nWarnings: {string.Join("; ", context.Warnings)}\n\n{context.Text}", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new("Consolas"), Margin = new Thickness(16) } }; preview.ShowDialog();
    });

    private WritingContext BuildWritingContext()
    {
        if (story is null || chapter is null || section is null) throw new InvalidOperationException("The target section is no longer open.");
        var manuscript = new StringBuilder();
        bool reachedTarget = false;
        foreach (var itemChapter in story.Chapters)
        {
            manuscript.Append("# ").AppendLine(itemChapter.Title).AppendLine();
            foreach (var itemSection in itemChapter.Sections)
            {
                manuscript.Append("## ").AppendLine(itemSection.Title).AppendLine();
                manuscript.AppendLine(itemSection.Text).AppendLine();
                if (itemSection.Id == section.Id) { reachedTarget = true; break; }
            }
            if (reachedTarget) break;
        }
        return new(story.Id, story.Title, story.Synopsis, chapter.Title, section.Id, section.Title, Editor.Text, manuscript.ToString());
    }

    private bool AppendGeneratedText(Guid targetSectionId, string generated)
    {
        if (story is null || string.IsNullOrWhiteSpace(generated)) return false;
        var targetChapter = story.Chapters.FirstOrDefault(c => c.Sections.Any(s => s.Id == targetSectionId));
        var targetSection = targetChapter?.Sections.FirstOrDefault(s => s.Id == targetSectionId);
        if (targetChapter is null || targetSection is null) return false;
        if (section?.Id != targetSectionId)
        {
            if (!SavePending()) return false;
            SetSection(targetChapter, targetSection); SelectNode(targetSection);
        }
        string addition = generated.Trim('\r', '\n');
        if (addition.Length == 0) return false;
        store.SnapshotNow(story, targetSection);
        string separator = Editor.Text.Length == 0 ? "" : Editor.Text.EndsWith("\n\n", StringComparison.Ordinal) ? "" : Editor.Text.EndsWith('\n') ? "\n" : "\n\n";
        EditorTab.IsSelected = true;
        Editor.Select(Editor.Text.Length, 0);
        Editor.SelectedText = separator + addition;
        Editor.CaretIndex = Editor.Text.Length;
        Editor.ScrollToEnd();
        return SavePending();
    }

    private void ToggleFocus()
    {
        if (WorkspacePanel.Visibility != Visibility.Visible) return;
        focusMode = !focusMode;
        if (focusMode && DetailsColumn.ActualWidth > 0) detailsWidth = DetailsColumn.Width;
        OutlineColumn.Width = new(focusMode ? 0 : 270); DetailsSplitterColumn.Width = new(focusMode ? 0 : 6); DetailsColumn.MinWidth = focusMode ? 0 : 280; DetailsColumn.Width = focusMode ? new(0) : detailsWidth; DetailsSplitter.Visibility = focusMode ? Visibility.Collapsed : Visibility.Visible;
        OutlinePanel.Visibility = DetailsPanel.Visibility = focusMode ? Visibility.Collapsed : Visibility.Visible;
        StatusCounts.Visibility = focusMode ? Visibility.Collapsed : Visibility.Visible;
        WindowState = focusMode ? WindowState.Maximized : WindowState.Normal; FocusWritingPane();
    }
    private void InitializeInlineSettings()
    {
        ThemeSelector.ItemsSource = ThemeCatalog.Names; EditorFontSelector.ItemsSource = EditorFonts; LanguageSelector.ItemsSource = new[] { "en-US", "en-GB", "fr-FR", "de-DE", "es-ES" }; HyphenSelector.ItemsSource = new[] { "One word", "Separate words" }; LoadSettingsPanel();
    }
    private void LoadSettingsPanel()
    {
        ThemeSelector.SelectedItem = settings.Theme; EditorFontSelector.SelectedItem = settings.Font; if (EditorFontSelector.SelectedItem is null) EditorFontSelector.Text = settings.Font; FontSizeBox.Text = settings.FontSize.ToString(CultureInfo.InvariantCulture); SpellCheckToggle.IsChecked = settings.SpellCheck; LanguageSelector.SelectedItem = settings.Language; HyphenSelector.SelectedItem = settings.JoinHyphens ? "One word" : "Separate words"; CountNumbersToggle.IsChecked = settings.CountNumbers; SnapshotMinutesBox.Text = settings.SnapshotMinutes.ToString(CultureInfo.InvariantCulture); SnapshotRetentionBox.Text = settings.SnapshotRetention.ToString(CultureInfo.InvariantCulture); DailyTargetBox.Text = settings.DailyTarget.ToString(CultureInfo.InvariantCulture); DefaultStoryTargetBox.Text = settings.DefaultStoryTarget.ToString(CultureInfo.InvariantCulture); StorageRootBox.Text = settings.StorageRoot; SettingsStatus.Text = "Settings apply to this app and future projects.";
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => Guard(() => { SettingsExpander.IsExpanded = true; SettingsExpander.BringIntoView(); ThemeSelector.Focus(); });
    private void ApplySettings_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (!SavePending()) return;
        if (!double.TryParse(FontSizeBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out double size) || !double.IsFinite(size) || size < 10 || size > 48) { SettingsStatus.Text = "Font size must be 10 to 48."; return; }
        if (!int.TryParse(SnapshotMinutesBox.Text, out int interval) || interval is < 1 or > 1440) { SettingsStatus.Text = "Snapshot interval must be 1 to 1440 minutes."; return; }
        if (!int.TryParse(SnapshotRetentionBox.Text, out int retention) || retention is < 1 or > 1000) { SettingsStatus.Text = "Snapshot retention must be 1 to 1000."; return; }
        if (!ValidNonnegative(DailyTargetBox.Text) || !ValidNonnegative(DefaultStoryTargetBox.Text)) { SettingsStatus.Text = "Enter valid word targets."; return; }
        if (string.IsNullOrWhiteSpace(StorageRootBox.Text) || !Path.IsPathFullyQualified(StorageRootBox.Text)) { SettingsStatus.Text = "Use a full path for the project location."; return; }
        string newRoot = Path.GetFullPath(StorageRootBox.Text); Directory.CreateDirectory(newRoot); foreach (var s in stories) if (!settings.ProjectFolders.Contains(s.Folder, StringComparer.OrdinalIgnoreCase)) settings.ProjectFolders.Add(s.Folder);
        settings.Theme = ThemeSelector.SelectedItem?.ToString() ?? "System"; settings.Font = EditorFontSelector.SelectedItem?.ToString() ?? EditorFontSelector.Text; settings.FontSize = size; settings.SpellCheck = SpellCheckToggle.IsChecked == true; settings.Language = LanguageSelector.SelectedItem?.ToString() ?? "en-US"; settings.JoinHyphens = HyphenSelector.SelectedItem?.ToString() != "Separate words"; settings.CountNumbers = CountNumbersToggle.IsChecked == true; settings.SnapshotMinutes = interval; settings.SnapshotRetention = retention; settings.DailyTarget = int.Parse(DailyTargetBox.Text); settings.DefaultStoryTarget = int.Parse(DefaultStoryTargetBox.Text); settings.StorageRoot = newRoot;
        SaveSettings(); ApplyAppearance(); foreach (var s in stories) { foreach (var c in s.Chapters) { foreach (var sec in c.Sections) { sec.Statistics = WordCounter.Analyze(sec.Text, settings.JoinHyphens, settings.CountNumbers); sec.Refresh(); } c.Refresh(); } s.Refresh(); } SettingsStatus.Text = "Settings saved and applied."; if (WorkspacePanel.Visibility == Visibility.Visible) RefreshStatistics(); else RefreshLibrary();
    });
    private void ApplyAppearance()
    {
        bool systemDark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0;
        ThemeCatalog.Apply(Application.Current.Resources, ThemeCatalog.Resolve(settings.Theme, systemDark));
        Editor.FontFamily = new(settings.Font); Editor.FontSize = settings.FontSize;
        SpellCheck.SetIsEnabled(Editor, settings.SpellCheck); Editor.Language = XmlLanguage.GetLanguage(settings.Language);
        SpellCheck.SetIsEnabled(StorySynopsisBox, settings.SpellCheck); StorySynopsisBox.Language = XmlLanguage.GetLanguage(settings.Language); aiWritingSurface?.ApplyEditorAppearance(settings.Font, settings.FontSize, settings.SpellCheck, settings.Language);
        renderedSource = null;
        if (ReadTab.IsSelected) RefreshReading();
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null) { if (element is T found) return found; element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element); }
        return null;
    }
    private void Outline_MouseDown(object sender, MouseButtonEventArgs e) { dragStart = e.GetPosition(Outline); dragNode = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext; }
    private void Outline_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || dragNode is not (Chapter or Section)) return;
        Point now = e.GetPosition(Outline); if (Math.Abs(now.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var node = dragNode; dragNode = null; DragDrop.DoDragDrop(Outline, new DataObject("DraftLedger.Node", node), DragDropEffects.Move);
    }
    private void Outline_Drop(object sender, DragEventArgs e) => Guard(() =>
    {
        if (story is null || !e.Data.GetDataPresent("DraftLedger.Node")) return;
        var source = e.Data.GetData("DraftLedger.Node"); var target = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext;
        if (source is null || target is null || ReferenceEquals(source, target)) return;
        // Same-level reordering avoids changing stable manuscript paths.
        if (source is Chapter from && target is Chapter to) CommitStructure(() => { story.Chapters.Remove(from); story.Chapters.Insert(story.Chapters.IndexOf(to), from); }, from);
        else if (source is Section a && target is Section b)
        {
            var parent = story.Chapters.FirstOrDefault(c => c.Sections.Contains(a));
            if (parent is null || !parent.Sections.Contains(b)) { SaveStatus.Text = "Sections can be reordered within their chapter"; return; }
            CommitStructure(() => { parent.Sections.Remove(a); parent.Sections.Insert(parent.Sections.IndexOf(b), a); }, a);
        }
        e.Handled = true;
    });
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { ToggleFocus(); e.Handled = true; }
        else if (e.Key == Key.Escape && focusMode) { ToggleFocus(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S) { SavePending(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.F || e.Key == Key.H) && WorkspacePanel.Visibility == Visibility.Visible) { Find_Click(this, e); e.Handled = true; }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!SavePending()) { e.Cancel = true; return; }
        if (aiWritingSurface is not null && !aiWritingSurface.TryShutdown()) { e.Cancel = true; return; }
        synopsisCancellation?.Cancel(); synopsisClient.Dispose();
        saveTimer.Stop(); RefreshIndex(); index?.Dispose();
    }
}
