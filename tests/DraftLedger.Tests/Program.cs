using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using DraftLedger.Core;
using Microsoft.Data.Sqlite;

var root = Path.Combine(Path.GetTempPath(), "DraftLedger-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
void Throws<T>(Action action, string name) where T : Exception { try { action(); } catch (T) { Check(true, name); return; } throw new Exception("FAIL (no exception): " + name); }
async Task<T> ThrowsAsync<T>(Func<Task> action, string name) where T : Exception { try { await action(); } catch (T ex) { Check(true, name); return ex; } throw new Exception("FAIL (no exception): " + name); }
IEnumerable<ReadingRun> ReadingRuns(IEnumerable<ReadingBlock> blocks) => blocks.SelectMany(block => block.Runs.Concat(ReadingRuns(block.Children)));
List<ReadingRun> ReadRuns(string text) => ReadingRuns(MarkdownReading.Parse(text)).ToList();
try
{
    using (var xaml = typeof(Program).Assembly.GetManifestResourceStream("MainWindow.xaml")!)
    {
        var document = XDocument.Load(xaml);
        var progressBindings = document.Descendants().Where(e => e.Name.LocalName == "ProgressBar")
            .Select(e => (string?)e.Attribute("Value")).Where(v => v?.Contains("Binding Progress", StringComparison.Ordinal) == true).ToList();
        Check(progressBindings.Count > 0 && progressBindings.All(v => v!.Split(',').Any(part => part.Trim().TrimEnd('}').Trim() == "Mode=OneWay")), "read-only story progress uses an explicit one-way UI binding");
        var outlineLabels = document.Descendants().Where(e => e.Name.LocalName == "TextBlock" && ((string?)e.Attribute("Text"))?.Contains("TreeLabel", StringComparison.Ordinal) == true).ToList();
        Check(outlineLabels.Count == 2 && outlineLabels.All(e => ((string?)e.Attribute("Foreground"))?.Contains("AncestorType=TreeViewItem", StringComparison.Ordinal) == true), "outline labels inherit active and inactive selection text color");
        var editor = document.Descendants().Single(e => e.Name.LocalName == "TextBox" && e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "Editor"));
        Check((string?)editor.Attribute("FontWeight") == "Normal" && editor.Attributes().Any(a => a.Name.LocalName.EndsWith("LineHeight", StringComparison.Ordinal) && a.Value == "29"), "editor manuscript text has explicit normal weight and relaxed line spacing");
        Check(document.Descendants().Any(e => e.Name.LocalName == "GridSplitter" && (string?)e.Attribute("ResizeDirection") == "Columns"), "workspace has a resizable details pane");
        var expanderHeaders = document.Descendants().Where(e => e.Name.LocalName == "Expander").Select(e => (string?)e.Attribute("Header")).ToHashSet();
        Check(new[] { "AT A GLANCE", "SCENE PURPOSE / NOTES", "MEMORY & CONTINUITY", "API CONNECTIONS", "STORY DETAILS", "SETTINGS", "FILES & RECOVERY" }.All(expanderHeaders.Contains), "details-pane tools are inline and collapsible");
        Check(document.Descendants().Any(e => e.Name.LocalName == "CheckBox" && (string?)e.Attribute("Content") == "Enable long-story memory"), "long-story memory has an explicit per-story opt-in");
        var writingTabs = document.Descendants().Where(e => e.Name.LocalName == "TabItem").Select(e => (string?)e.Attribute("Header")).ToHashSet();
        Check(new[] { "AI Writing", "Editor", "Preview" }.All(writingTabs.Contains), "main writing area has AI Writing, Editor, and Preview tabs");
        Check(document.Descendants().Any(e => e.Name.LocalName == "Image" && (string?)e.Attribute("Source") == "Assets/DraftLedger-256.png"), "application header uses the high-resolution logo asset");
    }
    using (var xaml = typeof(Program).Assembly.GetManifestResourceStream("AppStyles.xaml")!)
    {
        var document = XDocument.Load(xaml);
        var combo = document.Descendants().Where(e => e.Name.LocalName == "Style" && (string?)e.Attribute("TargetType") == "ComboBox").Single();
        bool HasNamed(string name) => combo.Descendants().Any(e => e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == name));
        var selectionPresenter = combo.Descendants().Single(e => e.Name.LocalName == "ContentPresenter" && e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "SelectionPresenter"));
        Check(HasNamed("ComboBorder") && HasNamed("PART_Popup") && HasNamed("PART_EditableTextBox") && ((string?)selectionPresenter.Attributes().FirstOrDefault(a => a.Name.LocalName.EndsWith("Foreground", StringComparison.Ordinal)))?.Contains("TemplateBinding Foreground", StringComparison.Ordinal) == true, "closed and editable dropdown faces use the themed foreground and background template");
        var comboItems = document.Descendants().Where(e => e.Name.LocalName == "Style" && (string?)e.Attribute("TargetType") == "ComboBoxItem").Single();
        Check(comboItems.Descendants().Any(e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == "Foreground" && ((string?)e.Attribute("Value"))?.Contains("InkBrush", StringComparison.Ordinal) == true) && comboItems.Descendants().Any(e => e.Name.LocalName == "Trigger" && (string?)e.Attribute("Property") == "IsSelected"), "dropdown items define readable themed text and selection states");
        var resourceKeys = document.Root!.Element(document.Root.Name.Namespace + "Application.Resources")!.Elements().Select(e => e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key")?.Value).Where(v => v is not null).ToHashSet();
        Check(new[] { "SelectionBrush", "SelectionTextBrush", "InactiveSelectionBrush", "DisabledBrush" }.All(resourceKeys.Contains), "theme resources include explicit active and inactive contrast colors");
        var tab = document.Descendants().Single(e => e.Name.LocalName == "Style" && (string?)e.Attribute("TargetType") == "TabItem");
        Check(tab.Descendants().Any(e => e.Name.LocalName == "Setter" && ((string?)e.Attribute("TargetName")) == "HeaderContent" && ((string?)e.Attribute("Property")) == "TextElement.FontWeight") && !tab.Elements().Any(e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == "FontWeight"), "tab selection emphasizes only the header without making its page content bold");
    }
    using (var xaml = typeof(Program).Assembly.GetManifestResourceStream("AiWritingWindow.xaml")!)
    {
        var document = XDocument.Load(xaml);
        string? ButtonText(string name) => (string?)document.Descendants().Single(e => e.Name.LocalName == "Button" && e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == name)).Attribute("Content");
        Check(ButtonText("GenerateButton") == "Generate" && ButtonText("ClearButton") == "Clear output" && ButtonText("AppendButton") == "Append to section", "AI output requires an explicit append and offers a clear action");
        var response = document.Descendants().Single(e => e.Name.LocalName == "TextBox" && e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "ResponseBox"));
        Check((string?)response.Attribute("IsReadOnly") == "False", "generated output is editable before append");
        var model = document.Descendants().Single(e => e.Name.LocalName == "ComboBox" && e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "ModelSelector"));
        Check((string?)model.Attribute("IsTextSearchEnabled") == "False" && model.Attributes().Any(a => a.Name.LocalName == "KeyUp" && a.Value == "ModelFilter_KeyUp"), "model selector uses substring filtering");
        var aiTabs = document.Descendants().Where(e => e.Name.LocalName == "TabItem").Select(e => (string?)e.Attribute("Header")).ToHashSet();
        Check(new[] { "Input", "Output", "Lorebooks", "Context / Request" }.All(aiTabs.Contains), "AI writing workspace keeps input, output, lorebooks, and request preview tabs");
        var prompt = document.Descendants().Single(e => e.Name.LocalName == "TextBox" && e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "PromptBox"));
        Check((string?)prompt.Attribute("SpellCheck.IsEnabled") == "True", "AI input editor enables spell checking");
        Check(document.Descendants().Any(e => e.Name.LocalName == "TextBox" && e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "PresetNameBox")) && !document.Descendants().Any(e => e.Name.LocalName == "Button" && (string?)e.Attribute("Content") == "Edit preset"), "preset settings are edited inline without a second popup");
    }
    using (var xaml = typeof(Program).Assembly.GetManifestResourceStream("MemoryWindow.xaml")!)
    {
        var document = XDocument.Load(xaml); var buttons = document.Descendants().Where(e => e.Name.LocalName == "Button").Select(e => (string?)e.Attribute("Content")).ToHashSet();
        Check(new[] { "Analyze current chapter", "Analyze complete story", "Preview memory context", "Approve", "Reject", "Delete saved memory and move it to recovery" }.All(buttons.Contains), "memory manager exposes analysis, review, preview, and recoverable deletion");
        Check(document.Descendants().Any(e => e.Name.LocalName == "CheckBox" && ((string?)e.Attribute("Content"))?.StartsWith("Automatically propose", StringComparison.Ordinal) == true), "automatic memory proposals are separately opt-in");
    }
    using (var xaml = typeof(Program).Assembly.GetManifestResourceStream("StoryDetailsWindow.xaml")!)
    {
        var document = XDocument.Load(xaml);
        Check(document.Descendants().Any(e => e.Name.LocalName == "Button" && (string?)e.Attribute("Content") == "Generate synopsis with AI") && document.Descendants().Any(e => e.Name.LocalName == "CheckBox" && ((string?)e.Attribute("Content"))?.Contains("long-story memory", StringComparison.OrdinalIgnoreCase) == true), "story details includes synopsis generation and per-story memory control");
        Check(document.Descendants().Any(e => e.Name.LocalName == "ComboBox" && ((string?)e.Attribute("ToolTip"))?.Contains("Drafting", StringComparison.Ordinal) == true), "story status explains each workflow state");
    }
    using (var icon = typeof(Program).Assembly.GetManifestResourceStream("DraftLedger.ico")!)
    {
        Span<byte> header = stackalloc byte[6]; icon.ReadExactly(header);
        Check(header[0] == 0 && header[1] == 0 && header[2] == 1 && header[3] == 0 && BitConverter.ToUInt16(header[4..]) >= 8, "application icon contains a multi-size Windows icon directory");
    }
    using (var png = typeof(Program).Assembly.GetManifestResourceStream("DraftLedger-256.png")!)
    {
        Span<byte> header = stackalloc byte[24]; png.ReadExactly(header);
        int width = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(header[16..20]));
        int height = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(header[20..24]));
        Check(width >= 256 && height >= 256, "in-application logo is at least 256 pixels");
    }
    using (var source = new StreamReader(typeof(Program).Assembly.GetManifestResourceStream("MainWindow.xaml.cs")!))
    {
        string text = source.ReadToEnd();
        Check(text.Contains("DetailsSplitterColumn.Width = new(focusMode ? 0 : 6)") && text.Contains("DetailsColumn.MinWidth = focusMode ? 0 : 280"), "focus mode removes both side panes and their reserved column space");
        Check(new[] { "Aptos", "Cascadia Mono", "Garamond", "Palatino Linotype", "Trebuchet MS", "Verdana" }.All(text.Contains), "settings offer an expanded editor font list");
    }
    using (var source = new StreamReader(typeof(Program).Assembly.GetManifestResourceStream("ThemeCatalog.cs")!))
    {
        string text = source.ReadToEnd();
        Check(new[] { "Warm Sepia", "Ocean Mist", "Forest Night", "Plum Noir" }.All(text.Contains), "theme catalog includes four additional palettes");
    }
    var italics = ReadRuns("A *quiet moment* in the story.");
    Check(string.Concat(italics.Select(r => r.Text)) == "A quiet moment in the story." && italics.Single(r => r.Text == "quiet moment").Italic, "asterisks render italics without visible delimiters");
    Check(ReadRuns("*one* and *two*").Count(r => r.Italic) == 2, "multiple italic phrases remain separate");
    Check(ReadRuns("**bold**").Single() is { Text: "bold", Bold: true, Italic: false }, "double asterisks render bold");
    Check(ReadRuns("***both***").Single() is { Text: "both", Bold: true, Italic: true }, "triple asterisks combine bold and italics");
    var nested = ReadRuns("**bold with *italics* inside**");
    Check(nested.Single(r => r.Text == "italics") is { Bold: true, Italic: true } && nested.All(r => r.Bold), "nested emphasis retains surrounding bold");
    var escaped = ReadRuns(@"\*literal\*");
    Check(string.Concat(escaped.Select(r => r.Text)) == "*literal*" && escaped.All(r => !r.Italic), "escaped asterisks stay literal");
    Check(ReadRuns("unfinished *phrase").All(r => !r.Italic) && string.Concat(ReadRuns("unfinished *phrase").Select(r => r.Text)) == "unfinished *phrase", "unmatched asterisk preserves unfinished writing");
    Check(ReadRuns("`*literal*`").Single() is { Text: "*literal*", Code: true, Italic: false }, "inline code preserves literal asterisks");
    Check(MarkdownReading.Parse("```\n*literal*\n```").Single() is { Kind: ReadingBlockKind.Code } code && code.Runs.All(r => !r.Italic) && code.Runs[0].Text.Contains("*literal*"), "fenced code does not apply emphasis");
    var structure = MarkdownReading.Parse("# Title\n\n- First\n- Second\n\n> Quote\n\n3. Ordered");
    Check(structure.Count == 4 && structure[0] is { Kind: ReadingBlockKind.Heading, Level: 1 } && structure[1] is { Kind: ReadingBlockKind.BulletList } && structure[1].Children.Count == 2 && structure[2].Kind == ReadingBlockKind.Quote && structure[3] is { Kind: ReadingBlockKind.NumberedList, Start: 3 }, "reading projection preserves headings, lists, quotes, and list starts");
    Check(ReadRuns("_quiet_ &amp; **strong**").Any(r => r.Italic) && string.Concat(ReadRuns("_quiet_ &amp; **strong**").Select(r => r.Text)) == "quiet & strong", "underscores and character entities are rendered");
    Check(MarkdownReading.Parse("").Count == 0 && ReadRuns("plain text").Single() is { Text: "plain text", Italic: false, Bold: false }, "blank and plain sections render without formatting");
    Check(string.Concat(ReadRuns("![cover](https://example.test/cover.png) [label](https://example.test)").Select(r => r.Text)) == "[Image: cover] label", "images and links become display-only text");
    Check(string.Concat(ReadRuns("<script>alert(1)</script>").Select(r => r.Text)) == "<script>alert(1)</script>", "HTML is literal text in the native reading projection");
    Check(WordCounter.Count("well-known isn't rock’n’roll 42 3.14 -- !!!") == 5, "hyphens, contractions, decimal numbers, punctuation");
    Check(WordCounter.Count("well-known isn't", false) == 3, "configurable hyphen splitting");
    Check(WordCounter.Count("123 3.14 abc123 hello", true, false) == 2, "numbers can be excluded without dropping alphanumeric words");
    Check(WordCounter.Count("naïve cafe\u0301 привет 中文") == 4, "Unicode letters and combining marks");
    Check(WordCounter.Count("word—word word–word") == 4, "dash-separated words");
    Check(WordCounter.Analyze("  \r\n\t!?  ").Words == 0, "empty and punctuation-only input");
    var stats = WordCounter.Analyze("Hello world.\r\n\r\nAnother paragraph! Last line");
    Check(stats.Words == 6 && stats.Paragraphs == 2 && stats.Sentences == 3, "paragraph and sentence estimates including unfinished sentence");
    Check(WordCounter.Analyze("a b\r\n").Characters == 5 && WordCounter.Analyze("a b\r\n").CharactersWithoutSpaces == 2, "character counts and whitespace");
    var settings = new Settings { StorageRoot = Path.Combine(root, "library"), SnapshotRetention = 3, SnapshotMinutes = 5 };
    var store = new ProjectStore(settings);
    var story = store.Create("A portable story"); var chapter = story.Chapters[0]; var section = chapter.Sections[0];
    Check(File.Exists(store.SectionPath(story, chapter, section)), "new project creates readable Markdown");
    section.Text = "An opening with four?"; section.Statistics = WordCounter.Analyze(section.Text); store.SaveSection(story, chapter, section);
    var loaded = store.Load(story.Folder);
    Check(loaded.Chapters[0].Sections[0].Text == section.Text && loaded.Words == 4, "autosave round trip and derived aggregate counts");
    Check(!File.ReadAllText(Path.Combine(story.Folder, "project.json")).Contains("An opening"), "manuscript lives outside metadata");
    Check(store.Snapshots(story, section).Length == 1 && File.ReadAllText(store.Snapshots(story, section)[0]) == "", "first edit keeps previous disk version");
    for (int i = 0; i < 5; i++) store.SnapshotNow(story, section);
    Check(store.Snapshots(story, section).Length == 3, "snapshot retention is bounded");
    string originalPath = store.SectionPath(story, chapter, section);
    chapter.Title = "Renamed chapter"; section.Title = "Renamed section";
    var second = store.AddSection(story, chapter, "Second", "Second scene.");
    chapter.Sections.Reverse(); story.Synopsis = "Planning notes"; store.SaveMetadata(story);
    loaded = store.Load(story.Folder);
    Check(File.Exists(originalPath) && loaded.Chapters[0].Sections[1].Id == section.Id && loaded.Synopsis == "Planning notes", "rename and reorder preserve content and portable metadata");
    string export = ProjectStore.Export(story, null, null, true);
    Check(export.IndexOf("Second scene.", StringComparison.Ordinal) < export.IndexOf("An opening", StringComparison.Ordinal), "export respects manuscript order");
    Check(ProjectStore.Export(story, chapter, section, false) == section.Text, "section export preserves exact text");
    string zipPath = Path.Combine(root, "backup.zip"); ProjectStore.ExportZip(story, zipPath);
    string restoredRoot = Path.Combine(root, "restored"); ZipFile.ExtractToDirectory(zipPath, restoredRoot);
    var portable = store.Load(Directory.GetDirectories(restoredRoot)[0]);
    Check(portable.Words == story.Words && portable.Chapters[0].Sections[1].Text == section.Text, "ZIP moves between folders and rebuilds counts");
    Throws<IOException>(() => ProjectStore.ExportZip(story, Path.Combine(story.Folder, "backup.zip")), "ZIP cannot recursively include itself");
    string beforeConflict = File.ReadAllText(originalPath);
    File.WriteAllText(originalPath, "External edit"); section.Text = "Unsaved local draft";
    Throws<IOException>(() => store.SaveSection(story, chapter, section), "external section edits block overwrite");
    Check(File.ReadAllText(originalPath) == "External edit" && Directory.GetFiles(Path.Combine(story.Folder, "recovery"), "*.md").Any(p => File.ReadAllText(p) == "Unsaved local draft"), "conflict preserves external text and recovery copy");
    File.Delete(originalPath);
    Throws<FileNotFoundException>(() => store.Load(story.Folder), "missing text never silently becomes a blank manuscript");
    File.WriteAllText(originalPath, beforeConflict);
    section.Text = beforeConflict;
    string manifestPath = Path.Combine(story.Folder, "project.json"), originalManifest = File.ReadAllText(manifestPath);
    File.AppendAllText(manifestPath, "\n");
    Throws<IOException>(() => store.SaveMetadata(story), "external metadata edits block overwrite");
    File.WriteAllText(manifestPath, originalManifest);
    chapter.Sections.Remove(second); store.SaveMetadata(story);
    Check(File.Exists(store.SectionPath(story, chapter, second)) && Directory.GetFiles(Path.Combine(story.Folder, "backups", "project")).Any(p => File.ReadAllText(p).Contains(second.Id.ToString())), "removal retains text and recoverable metadata");
    string utf16 = Path.Combine(root, "utf16.txt"); File.WriteAllText(utf16, "Café dialogue", Encoding.Unicode);
    Check(ProjectStore.ImportText(utf16) == "Café dialogue", "BOM-marked UTF-16 imports correctly");
    string invalid = Path.Combine(root, "invalid.txt"); File.WriteAllBytes(invalid, [0xff, 0xff, 0x61]);
    Throws<DecoderFallbackException>(() => ProjectStore.ImportText(invalid), "invalid text encoding fails instead of replacing characters");
    Throws<InvalidDataException>(() => ProjectStore.ImportText(Path.Combine(root, "file.docx")), "unsupported import formats are explicit");
    string atomic = Path.Combine(root, "atomic.txt"); AtomicFile.Write(atomic, "old"); AtomicFile.Write(atomic, "new");
    Check(File.ReadAllText(atomic) == "new" && Directory.GetFiles(root, "*.tmp").Length == 0, "atomic replacement cleans temporary files");
    string db = Path.Combine(root, "index.sqlite");
    using (var index = new LibraryIndex(db)) { index.Rebuild([story]); index.Rebuild([story]); }
    using (var connection = new SqliteConnection("Data Source=" + db))
    {
        connection.Open(); using var query = connection.CreateCommand(); query.CommandText = "SELECT count(*) FROM stories";
        Check(Convert.ToInt32(query.ExecuteScalar()) == 1, "SQLite rebuild is idempotent");
    }
    story.Archived = true; store.SaveMetadata(story);
    Check(store.Load(story.Folder).Archived, "archive state survives reopening");
    var corrupt = Path.Combine(settings.StorageRoot, "broken"); Directory.CreateDirectory(corrupt); File.WriteAllText(Path.Combine(corrupt, "project.json"), "broken json");
    var library = store.LoadLibrary(out var errors);
    Check(library.Count == 1 && errors.Count == 1, "corrupt project does not prevent healthy library access");

    var memoryProjectStore = new ProjectStore(new Settings { StorageRoot = Path.Combine(root, "memory-library") });
    var memoryStory = memoryProjectStore.Create("Long novel"); var oldChapter = memoryStory.Chapters[0]; var oldSection = oldChapter.Sections[0];
    oldSection.Text = "Mara hid the silver locket beneath the observatory floor. Only Tomas saw her."; oldSection.Statistics = WordCounter.Analyze(oldSection.Text); memoryProjectStore.SaveSection(memoryStory, oldChapter, oldSection);
    var currentChapter = new Chapter { Title = "Chapter 20" }; memoryStory.Chapters.Add(currentChapter); var currentSection = memoryProjectStore.AddSection(memoryStory, currentChapter, "Return", "Mara entered the observatory and wondered whether the secret remained safe."); memoryProjectStore.SaveMetadata(memoryStory);
    var memoryStore = new StoryMemoryStore(); var memoryDoc = new StoryMemoryDocument { StorySoFar = "Mara is investigating the northern observatory.", StyleGuide = "Close third person, past tense.", Facts = [new() { Category = "Object", Subject = "silver locket", Fact = "Mara hid it beneath the observatory floor.", Locked = true }], OpenThreads = [new() { Title = "The hidden locket", Detail = "Tomas knows where Mara hid it." }] };
    Check(StoryMemoryRetrieval.Build(memoryStory, currentChapter, currentSection, memoryDoc, "Continue").Text.Length == 0, "story memory remains inactive until explicitly enabled");
    memoryStory.Memory.Mode = MemoryMode.FullRetrieval; memoryStory.Memory.CharacterBudget = 12000; memoryStory.Memory.RetrievedPassages = 4;
    var memoryContext = StoryMemoryRetrieval.Build(memoryStory, currentChapter, currentSection, memoryDoc, "Does Tomas reveal the silver locket?");
    Check(memoryContext.Text.Contains("Canonical story bible") && memoryContext.Text.Contains("Open plot threads") && memoryContext.Passages.Any(p => p.Text.Contains("observatory floor")), "hybrid memory combines approved facts, open threads, and relevant earlier prose");
    var preparedWithMemory = ChatPreparation.Prepare(new() { Model = "writer", Context = ContextScope.CurrentSection }, new(memoryStory.Id, memoryStory.Title, "", currentChapter.Title, currentSection.Id, currentSection.Title, currentSection.Text, currentSection.Text), "Continue", [], memoryContext);
    Check(preparedWithMemory.Messages.Any(m => m.Role == "system" && m.Content.Contains("Long-story memory")) && preparedWithMemory.Memory?.Passages.Count > 0, "approved memory is injected separately and reported in request preview data");
    memoryDoc.ChapterSummaries.Add(new() { ChapterId = oldChapter.Id, ChapterTitle = oldChapter.Title, Summary = "Mara hid a locket.", SourceHash = StoryMemoryRetrieval.ChapterHash(oldChapter) });
    memoryStore.Save(memoryStory, memoryDoc); var reopenedMemory = memoryStore.Load(memoryStory);
    Check(File.Exists(memoryStore.PathFor(memoryStory)) && File.Exists(Path.Combine(memoryStore.Folder(memoryStory), "story-bible.md")) && reopenedMemory.Facts.Single().Locked, "memory persists as portable JSON with readable Markdown projections");
    oldSection.Text += " Tomas later moved it."; oldSection.Statistics = WordCounter.Analyze(oldSection.Text);
    Check(StoryMemoryRetrieval.Build(memoryStory, currentChapter, currentSection, reopenedMemory, "Continue").Warnings.Any(w => w.Contains("stale", StringComparison.OrdinalIgnoreCase)), "edited source text marks its chapter summary as potentially stale");
    string analysisJson = $$"""{"storySummary":"Mara returned to the observatory.","styleGuide":"Past tense.","chapterSummaries":[{"chapterId":"{{currentChapter.Id}}","summary":"Mara returned."}],"facts":[{"category":"Character","subject":"Mara","fact":"She distrusts Tomas."}],"openThreads":[{"title":"The locket","detail":"Its location may be compromised."}],"resolvedThreads":[],"warnings":[{"subject":"Locket location","detail":"Verify whether Tomas moved it."}]}""";
    var proposals = StoryMemoryAnalysis.Parse(analysisJson, memoryStory, [currentChapter], currentSection.Id);
    Check(proposals.Count == 6 && proposals.All(p => p.Status == MemoryProposalStatus.Proposed), "AI analysis becomes reviewable proposals instead of silently changing memory");
    foreach (var proposal in proposals) StoryMemoryAnalysis.Approve(memoryStory, reopenedMemory, proposal);
    Check(reopenedMemory.StorySoFar.Contains("returned") && reopenedMemory.ChapterSummaries.Any(c => c.ChapterId == currentChapter.Id) && reopenedMemory.Facts.Any(f => f.Subject == "Mara") && reopenedMemory.OpenThreads.Any(t => t.Title == "The locket") && reopenedMemory.Warnings.Any(w => w.Subject == "Locket location"), "approving proposals updates summaries, story bible, plot threads, and warnings");
    memoryStore.Save(memoryStory, reopenedMemory); string memoryJson = memoryStore.PathFor(memoryStory); string originalMemory = File.ReadAllText(memoryJson); File.AppendAllText(memoryJson, "\n");
    Throws<IOException>(() => memoryStore.Save(memoryStory, reopenedMemory), "external memory edits block overwrite"); File.WriteAllText(memoryJson, originalMemory); reopenedMemory.DiskHash = AtomicFile.Hash(originalMemory);
    string recoveredMemory = memoryStore.DeleteRecoverably(memoryStory);
    Check(!Directory.Exists(memoryStore.Folder(memoryStory)) && Directory.Exists(recoveredMemory), "memory deletion moves files to recovery instead of destroying them");

    var localApi = new ApiConnection { Kind = ApiKind.LMStudio, BaseUrl = "http://localhost:1234/v1" };
    Check(new ApiConnection { Name = "My local model" }.ToString() == "My local model" && new ModelPreset { Name = "Novel prose" }.ToString() == "Novel prose", "connection and preset selectors display friendly saved names");
    Check(ApiEndpoint.Normalize(localApi).AbsoluteUri == "http://localhost:1234/v1/", "localhost model servers may use HTTP");
    Throws<InvalidDataException>(() => ApiEndpoint.Normalize(new() { BaseUrl = "http://example.com/v1" }), "public API connections require HTTPS");
    Throws<InvalidDataException>(() => ApiEndpoint.Normalize(new() { BaseUrl = "http://192.168.1.5/v1" }), "private LAN HTTP requires explicit opt-in");
    Check(ApiEndpoint.Normalize(new() { BaseUrl = "http://192.168.1.5/v1", AllowPrivateHttp = true }).Host == "192.168.1.5", "private LAN HTTP opt-in is accepted");
    Throws<InvalidDataException>(() => ApiEndpoint.Normalize(new() { BaseUrl = "https://key@example.com/v1" }), "embedded URL credentials are rejected");
    Throws<InvalidDataException>(() => ApiEndpoint.Normalize(new() { BaseUrl = "https://example.com/v1/chat/completions" }), "connection URL must be an API base");

    var prepared = new PreparedChat([new("system", "Write prose."), new("user", "Continue.")], [], [], 22);
    var thinkingPreset = new ModelPreset { Model = "reasoner", Thinking = ThinkingMode.Off, ThinkingProtocol = ThinkingProtocol.Auto };
    var openRouterBody = ChatApiClient.BuildRequest(new() { Kind = ApiKind.OpenRouter }, thinkingPreset, prepared);
    Check(openRouterBody["reasoning"]?["enabled"]?.GetValue<bool>() == false && openRouterBody["reasoning"]?["exclude"]?.GetValue<bool>() == true, "OpenRouter thinking-off request excludes reasoning");
    thinkingPreset.Thinking = ThinkingMode.Low;
    var openAiBody = ChatApiClient.BuildRequest(new() { Kind = ApiKind.OpenAI }, thinkingPreset, prepared);
    Check(openAiBody["reasoning_effort"]?.GetValue<string>() == "low" && openAiBody["max_completion_tokens"]?.GetValue<int>() == 1024, "OpenAI reasoning effort and output limit are mapped");
    thinkingPreset.Thinking = ThinkingMode.Off;
    Throws<InvalidDataException>(() => ChatApiClient.BuildRequest(new() { Kind = ApiKind.OpenRouter }, thinkingPreset, prepared, new() { Id = "reasoner", ReasoningMandatory = true }), "mandatory reasoning cannot be disabled silently");

    var writing = new WritingContext(Guid.NewGuid(), "Story", "A mystery", "Chapter 1", Guid.NewGuid(), "Opening", "A😀BCDEF", "Earlier. A😀BCDEF");
    var contextPreset = new ModelPreset { Model = "test", Context = ContextScope.CurrentSection, ContextCharacters = 7, LoreCharacters = 1000 };
    var contextRequest = ChatPreparation.Prepare(contextPreset, writing, "Continue", []);
    Check(contextRequest.Messages.Any(m => m.Content.Contains("😀BCDEF")) && contextRequest.Warnings.Any(w => w.Contains("character limit")), "manuscript context is safely tail-truncated");
    var lorebook = new Lorebook { Name = "People", Entries = [new() { Name = "Mara", Keys = ["Mara"], Content = "Mara fears heights.", WholeWords = true }, new() { Name = "Constant", Constant = true, Content = "The year is 1920." }] };
    var lore = LoreMatcher.Select([lorebook], "Mara climbed.", 1000);
    Check(lore.Names.Count == 2 && lore.After.Contains("fears heights") && lore.After.Contains("1920"), "lore matching includes keyed and constant entries");
    Check(LoreMatcher.Select([lorebook], "Marathon", 1000).Names.Count == 1, "whole-word lore keys do not match inside another word");
    var budgetedLore = LoreMatcher.Select([lorebook], "Mara", 20);
    Check(budgetedLore.Names.Count == 0 && budgetedLore.Warnings.Any(w => w.Contains("budget")), "lore character budget is enforced");

    string stPresetJson = """{"name":"ST prose","temperature":0.65,"top_p":0.9,"openai_max_tokens":777,"stream_openai":false,"chat_completion_source":"openrouter","openrouter_model":"vendor/model","reasoning_effort":"none","prompts":[{"identifier":"main","role":"system","content":"Stay in voice","enabled":true}]}""";
    var stPreset = SillyTavernImport.Preset(stPresetJson, "preset");
    Check(stPreset.Value.Name == "ST prose" && stPreset.Value.Model == "vendor/model" && stPreset.Value.MaxTokens == 777 && !stPreset.Value.Stream && stPreset.Value.Thinking == ThinkingMode.Off && stPreset.Value.ImportedPrompts.Count == 1, "SillyTavern chat-completion preset imports supported settings");
    string stLoreJson = """{"name":"Novel lore","entries":{"0":{"comment":"City","key":["Arden"],"content":"Arden is coastal.","constant":false,"disable":false,"order":150,"position":0}}}""";
    var stLore = SillyTavernImport.Lorebook(stLoreJson, "lore");
    Check(stLore.Value.Name == "Novel lore" && stLore.Value.Entries.Single() is { Name: "City", Position: 0, Order: 150 }, "SillyTavern lorebook imports entries and placement");
    var filter = new ThinkingTextFilter();
    string filtered = filter.Add("Start <thi") + filter.Add("nk>secret</th") + filter.Add("ink> finish", true);
    Check(filtered == "Start  finish", "reasoning tags split across stream chunks are removed");

    int modelCalls = 0; Uri? modelUri = null; string? authScheme = null, authValue = null, userAgent = null, referrer = null, appTitle = null;
    using (var api = new ChatApiClient(new RecordingHandler(request =>
    {
        modelCalls++; modelUri = request.RequestUri; authScheme = request.Headers.Authorization?.Scheme; authValue = request.Headers.Authorization?.Parameter;
        userAgent = request.Headers.UserAgent.ToString(); referrer = request.Headers.Referrer?.ToString(); appTitle = request.Headers.TryGetValues("X-OpenRouter-Title", out var titles) ? titles.Single() : null;
        return JsonResponse(HttpStatusCode.OK, """{"data":[{"id":"writer/a","name":"Writer A","context_length":32000,"supported_parameters":["temperature","reasoning"],"reasoning":{"mandatory":false,"supported_efforts":["low","high"]}}]}""");
    })))
    {
        var models = await api.ModelsAsync(new() { Kind = ApiKind.OpenRouter, BaseUrl = "https://openrouter.ai/api/v1" }, "secret-key", CancellationToken.None);
        Check(modelCalls == 1 && modelUri?.AbsoluteUri == "https://openrouter.ai/api/v1/models" && authScheme == "Bearer" && authValue == "secret-key", "model discovery uses the selected API base and bearer key once");
        Check(userAgent == "DraftLedger/0.3.2" && referrer is null && appTitle == "DraftLedger", "API client identifies DraftLedger without browser impersonation");
        Check(models.Single() is { Id: "writer/a", ContextLength: 32000, ReasoningMandatory: false } && models[0].ReasoningEfforts!.SequenceEqual(["low", "high"]), "model discovery retains capability metadata");
    }
    string streamed = "";
    using (var api = new ChatApiClient(new RecordingHandler(_ => EventStream("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"<think>notes</think>Hello \"}}]}\n\ndata: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"world\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"))))
    {
        var result = await api.GenerateAsync(new() { Kind = ApiKind.LMStudio, BaseUrl = "http://localhost:1234/v1" }, "", new JsonObject { ["model"] = "local", ["stream"] = true }, chunk => streamed += chunk, CancellationToken.None);
        Check(result.Text == "Hello world" && streamed == result.Text && result.FinishReason == "stop", "streaming generation returns content while excluding tagged reasoning");
    }
    int limitedCalls = 0;
    using (var api = new ChatApiClient(new RecordingHandler(_ =>
    {
        limitedCalls++; var response = JsonResponse((HttpStatusCode)429, """{"error":{"message":"slow down secret-key"}}"""); response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)); return response;
    })))
    {
        var limited = new ApiConnection { Kind = ApiKind.OpenRouter, BaseUrl = "https://openrouter.ai/api/v1" };
        var failure = await ThrowsAsync<ApiFailure>(() => api.GenerateAsync(limited, "secret-key", new JsonObject(), _ => { }, CancellationToken.None), "rate limits fail without automatic retries");
        Check(limitedCalls == 1 && limited.RetryAfter > DateTimeOffset.UtcNow && !failure.Message.Contains("secret-key") && failure.Message.Contains("[redacted]"), "rate-limit cooldown persists and API keys are redacted from errors");
    }
    Console.WriteLine($"\n{passed} checks passed.");
}
finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }

static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
static HttpResponseMessage EventStream(string data) => new(HttpStatusCode.OK) { Content = new StringContent(data, Encoding.UTF8, "text/event-stream") };

sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
}
