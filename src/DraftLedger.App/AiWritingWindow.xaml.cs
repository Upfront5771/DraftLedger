using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using DraftLedger.Core;
using Microsoft.Win32;

namespace DraftLedger.App;

public partial class AiWritingWindow : Window
{
    private readonly AiSettingsFile settingsFile;
    private AiSettings settings;
    private readonly Func<WritingContext> getContext;
    private readonly Func<Guid, string, bool> append;
    private readonly string recoveryFolder;
    private readonly Story story;
    private Chapter chapter;
    private Section section;
    private readonly ProjectStore projectStore;
    private readonly StoryMemoryStore memoryStore = new();
    private readonly ChatApiClient client = new();
    private readonly StringBuilder response = new();
    private ModelPreset? preset;
    private CancellationTokenSource? cancellation;
    private bool loading = true, busy, appended, closeAfterStop;
    private string? recoveryPath;
    private DateTimeOffset lastCheckpoint;
    private List<LoreChoice> loreChoices = [];
    private List<ApiModel> allModels = [];
    private Guid outputSectionId;
    private sealed class LoreChoice
    {
        public required Lorebook Book { get; init; }
        public bool Enabled { get; set; }
        public string Label => $"{Book.Name} ({Book.Entries.Count} entries)";
    }
    private ApiConnection? Connection => ConnectionSelector.SelectedItem as ApiConnection;

    public AiWritingWindow(string path, Func<WritingContext> getContext, Func<Guid, string, bool> append, string recoveryFolder, Story story, Chapter chapter, Section section, ProjectStore projectStore)
    {
        this.getContext = getContext; this.append = append; this.recoveryFolder = recoveryFolder; this.story = story; this.chapter = chapter; this.section = section; this.projectStore = projectStore;
        settingsFile = new(path); settings = settingsFile.Load();
        InitializeComponent(); Height = Math.Min(860, SystemParameters.WorkArea.Height - 40); outputSectionId = section.Id;
        var context = getContext(); TargetLabel.Text = $"Append to: {context.StoryTitle} / {context.ChapterTitle} / {context.SectionTitle}";
        ThinkingSelector.ItemsSource = Enum.GetValues<ThinkingMode>(); ContextSelector.ItemsSource = Enum.GetValues<ContextScope>(); PresetThinkingSelector.ItemsSource = Enum.GetValues<ThinkingMode>(); PresetContextSelector.ItemsSource = Enum.GetValues<ContextScope>(); ThinkingProtocolSelector.ItemsSource = Enum.GetValues<ThinkingProtocol>();
        ConnectionSelector.ItemsSource = settings.Connections;
        ConnectionSelector.SelectedItem = settings.Connections.FirstOrDefault(c => c.Id == settings.LastConnection) ?? settings.Connections.FirstOrDefault();
        PresetSelector.ItemsSource = settings.Presets;
        PresetSelector.SelectedItem = settings.Presets.FirstOrDefault(p => p.Id == settings.LastPreset) ?? settings.Presets[0];
        PromptBox.Text = settings.PromptDrafts.GetValueOrDefault(context.StoryId, "");
        LoadPreset((ModelPreset)PresetSelector.SelectedItem); RefreshLore(); loading = false; UpdateModelInfo();
    }

    private Window DialogOwner => Owner ?? Application.Current.MainWindow;
    public FrameworkElement DetachWritingSurface(string font, double fontSize, bool spellCheck, string language) { ConfigurationPanel.Visibility = Visibility.Collapsed; PresetSettingsScroll.Visibility = Visibility.Collapsed; WritingHeader.Visibility = Visibility.Visible; AiTabs.Visibility = Visibility.Visible; RootPanel.Margin = new Thickness(0); ApplyEditorAppearance(font, fontSize, spellCheck, language); UpdateWritingSummary(); var surface = (FrameworkElement)Content; Content = null; return surface; }
    public void SetTarget(Chapter nextChapter, Section nextSection) { chapter = nextChapter; section = nextSection; if (ResponseBox.Text.Length == 0) outputSectionId = nextSection.Id; UpdateWritingSummary(); }
    public void ReloadConfiguration() { string promptText = PromptBox.Text; settings = settingsFile.Load(); loading = true; ConnectionSelector.ItemsSource = settings.Connections; ConnectionSelector.SelectedItem = settings.Connections.FirstOrDefault(c => c.Id == settings.LastConnection) ?? settings.Connections.FirstOrDefault(); PresetSelector.ItemsSource = settings.Presets; var selected = settings.Presets.FirstOrDefault(p => p.Id == settings.LastPreset) ?? settings.Presets.FirstOrDefault() ?? new ModelPreset(); if (settings.Presets.Count == 0) settings.Presets.Add(selected); PresetSelector.SelectedItem = selected; PromptBox.Text = promptText; LoadPreset(selected); RefreshLore(); loading = false; UpdateModelInfo(); UpdateWritingSummary(); }
    public void ApplyEditorAppearance(string font, double fontSize, bool spellCheck, string language) { var family = new FontFamily(font); PromptBox.FontFamily = ResponseBox.FontFamily = family; PromptBox.FontSize = ResponseBox.FontSize = fontSize; SpellCheck.SetIsEnabled(PromptBox, spellCheck); SpellCheck.SetIsEnabled(ResponseBox, spellCheck); PromptBox.Language = ResponseBox.Language = XmlLanguage.GetLanguage(language); }
    public void FocusInput() { InputTab.IsSelected = true; PromptBox.Focus(); }
    public void SaveState() => SaveConfiguration();
    private void UpdateWritingSummary() { if (WritingConfigurationSummary is not null) WritingConfigurationSummary.Text = $"{Connection?.Name ?? "No connection"} · {preset?.Name ?? "No preset"}\nTarget: {story.Title} / {chapter.Title} / {section.Title}"; }
    public bool TryShutdown() { if (busy) { cancellation?.Cancel(); return false; } SaveRecovery(); SaveConfiguration(); client.Dispose(); return true; }

    private void Report(Exception ex) => AiStatus.Text = ex switch
    {
        OperationCanceledException => cancellation?.IsCancellationRequested == true ? "Stopped. Any partial text is retained for review." : "The request timed out. Partial text is retained; no automatic retry was sent.",
        System.Net.Http.HttpRequestException => "Could not reach the API. Check the endpoint, TLS certificate, and whether the local server is running. " + ex.Message,
        _ => ex.Message
    };
    private void Guard(Action action) { try { action(); } catch (Exception ex) { Report(ex); } }
    private bool Confirm(string message) => MessageBox.Show(DialogOwner, message, "AI writing", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ProjectStore.Json), ProjectStore.Json)!;
    private string ModelId => ModelSelector.SelectedItem is ApiModel model ? model.Id : ModelSelector.Text.Trim();
    private void CapturePreset()
    {
        if (preset is null) return;
        preset.Model = ModelId; preset.ConnectionId = Connection?.Id; preset.Stream = StreamToggle.IsChecked == true;
        if (ThinkingSelector.SelectedItem is ThinkingMode thinking) preset.Thinking = thinking;
        if (ContextSelector.SelectedItem is ContextScope scope) preset.Context = scope;
    }
    private void SaveConfiguration()
    {
        CapturePreset(); settings.LastConnection = Connection?.Id; settings.LastPreset = preset?.Id;
        settings.PromptDrafts[getContext().StoryId] = PromptBox.Text;
        settings.StoryLorebooks[getContext().StoryId] = loreChoices.Where(l => l.Enabled).Select(l => l.Book.Id).ToList();
        settingsFile.Save(settings);
    }
    private void LoadPreset(ModelPreset value)
    {
        bool previous = loading; loading = true; preset = value;
        var connection = settings.Connections.FirstOrDefault(c => c.Id == value.ConnectionId);
        if (connection is not null) ConnectionSelector.SelectedItem = connection;
        allModels = Connection?.Models ?? []; ModelSelector.ItemsSource = allModels;
        ModelSelector.SelectedItem = Connection?.Models.FirstOrDefault(m => m.Id == value.Model);
        ModelSelector.Text = value.Model;
        StreamToggle.IsChecked = value.Stream; ThinkingSelector.SelectedItem = value.Thinking; ContextSelector.SelectedItem = value.Context;
        PresetStreamToggle.IsChecked = value.Stream; PresetThinkingSelector.SelectedItem = value.Thinking; PresetContextSelector.SelectedItem = value.Context; PresetNameBox.Text = value.Name; SystemPromptBox.Text = value.SystemPrompt; MaxTokensBox.Text = value.MaxTokens.ToString(CultureInfo.InvariantCulture); CompletionTokenLimitToggle.IsChecked = value.UseCompletionTokenLimit;
        TemperatureBox.Text = Number(value.Temperature); TopPBox.Text = Number(value.TopP); FrequencyPenaltyBox.Text = Number(value.FrequencyPenalty); PresencePenaltyBox.Text = Number(value.PresencePenalty); ThinkingProtocolSelector.SelectedItem = value.ThinkingProtocol; ContextCharactersBox.Text = value.ContextCharacters.ToString(CultureInfo.InvariantCulture); LoreCharactersBox.Text = value.LoreCharacters.ToString(CultureInfo.InvariantCulture); StopSequencesBox.Text = JsonSerializer.Serialize(value.Stop); ExtraSamplingBox.Text = JsonSerializer.Serialize(value.ExtraSampling); CharacterNameBox.Text = value.CharacterName; UserNameBox.Text = value.UserName; CharacterDescriptionBox.Text = value.CharacterDescription; CharacterPersonalityBox.Text = value.CharacterPersonality; PersonaBox.Text = value.Persona; ImportedPromptsBox.Text = JsonSerializer.Serialize(value.ImportedPrompts, ProjectStore.Json);
        loading = previous; UpdateModelInfo();
    }
    private void ReloadPresets(ModelPreset value)
    {
        loading = true; PresetSelector.ItemsSource = null; PresetSelector.ItemsSource = settings.Presets; PresetSelector.SelectedItem = value;
        loading = false; LoadPreset(value); SaveConfiguration();
    }
    private void ReloadConnections(ApiConnection? selected)
    {
        loading = true; ConnectionSelector.ItemsSource = null; ConnectionSelector.ItemsSource = settings.Connections; ConnectionSelector.SelectedItem = selected;
        allModels = selected?.Models ?? []; ModelSelector.ItemsSource = allModels; ModelSelector.SelectedItem = null; ModelSelector.Text = "";
        loading = false; UpdateModelInfo(); SaveConfiguration();
    }
    private void Connection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        allModels = Connection?.Models ?? []; ModelSelector.ItemsSource = allModels; ModelSelector.SelectedItem = null; ModelSelector.Text = "";
        UpdateModelInfo();
    }
    private void ModelFilter_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (loading || Connection is null) return;
        string query = ModelSelector.Text.Trim(); string exact = ModelId;
        allModels = Connection.Models;
        var filtered = query.Length == 0 ? allModels : allModels.Where(m => m.Id.Contains(query, StringComparison.OrdinalIgnoreCase) || m.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        loading = true; ModelSelector.ItemsSource = filtered; ModelSelector.Text = query; ModelSelector.IsDropDownOpen = filtered.Count > 0;
        if (ModelSelector.Template.FindName("PART_EditableTextBox", ModelSelector) is TextBox editor) { editor.CaretIndex = editor.Text.Length; editor.SelectionLength = 0; }
        loading = false; if (exact.Length > 0) UpdateModelInfo();
    }
    private void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || PresetSelector.SelectedItem is not ModelPreset value) return;
        CapturePreset(); LoadPreset(value);
    }
    private void Model_Changed(object sender, SelectionChangedEventArgs e) { if (!loading) UpdateModelInfo(); }
    private void UpdateModelInfo()
    {
        if (ModelInfo is null) return;
        var model = Connection?.Models.FirstOrDefault(m => m.Id == ModelId);
        ModelInfo.Text = model is null ? "Select a fetched model or type its exact ID. Availability is checked by the provider." : model.Name + (model.ContextLength.HasValue ? $" · context {model.ContextLength:N0} tokens" : "") + (model.ReasoningMandatory == true ? " · thinking is mandatory" : "");
        DestinationInfo.Text = Connection is null ? "Add an OpenRouter, LM Studio, OpenAI, or custom API connection." : $"Destination: {Connection.BaseUrl}\nEdit the selected preset directly below, then choose Save preset."; UpdateWritingSummary();
    }

    private void AddConnection_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        var type = Dialogs.Form(DialogOwner, "Connection type", [new("Provider", "LMStudio", Enum.GetNames<ApiKind>())]); if (type is null) return;
        var kind = Enum.Parse<ApiKind>(type["Provider"]);
        EditConnection(new ApiConnection { Kind = kind, Name = kind == ApiKind.LMStudio ? "LM Studio (local)" : kind.ToString(), BaseUrl = kind switch { ApiKind.LMStudio => "http://localhost:1234/v1", ApiKind.OpenAI => "https://api.openai.com/v1", ApiKind.OpenRouter => "https://openrouter.ai/api/v1", _ => "https://your-provider.example/v1" } }, true);
    });
    private void EditConnection_Click(object sender, RoutedEventArgs e) => Guard(() => { if (Connection is not null) EditConnection(Connection, false); });
    private void EditConnection(ApiConnection original, bool create)
    {
        var value = Copy(original);
        var fields = Dialogs.Form(DialogOwner, "API connection", [new("Name", value.Name), new("API base URL", value.BaseUrl), new("API key (blank keeps saved key)", "", Secret: true), new("Remove saved key", "No", ["No", "Yes"]), new("Allow unencrypted HTTP on private LAN IP", value.AllowPrivateHttp ? "Yes" : "No", ["No", "Yes"]), new("Timeout in seconds", value.TimeoutSeconds.ToString())], v =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(v["Name"])) return "Enter a connection name.";
                value.BaseUrl = v["API base URL"].Trim(); value.AllowPrivateHttp = v["Allow unencrypted HTTP on private LAN IP"] == "Yes"; ApiEndpoint.Normalize(value);
                if (!int.TryParse(v["Timeout in seconds"], out int seconds) || seconds is < 30 or > 1800) return "Timeout must be 30 to 1800 seconds.";
                if (v["API key (blank keeps saved key)"].Any(char.IsWhiteSpace)) return "API keys cannot contain whitespace.";
                if (original.ProtectedKey.Length > 0 && ApiEndpoint.Normalize(value) != ApiEndpoint.Normalize(original) && v["API key (blank keeps saved key)"].Length == 0 && v["Remove saved key"] != "Yes") return "The endpoint changed. Enter its API key or choose Remove saved key, so an existing credential is not sent to another destination.";
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        });
        if (fields is null) return;
        value.Name = fields["Name"].Trim(); value.TimeoutSeconds = int.Parse(fields["Timeout in seconds"]);
        string key = fields["API key (blank keeps saved key)"];
        if (fields["Remove saved key"] == "Yes") value.ProtectedKey = "";
        else if (key.Length > 0) value.ProtectedKey = AiSettingsFile.Protect(value, key);
        if (ApiEndpoint.Normalize(value) != ApiEndpoint.Normalize(original)) { value.Models.Clear(); value.ModelsUpdated = null; value.RetryAfter = null; }
        if (create) settings.Connections.Add(value); else settings.Connections[settings.Connections.IndexOf(original)] = value;
        ReloadConnections(value); AiStatus.Text = "Connection saved. Keys are encrypted for this Windows user and endpoint.";
    }
    private void DeleteConnection_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (Connection is not { } value || !Confirm($"Delete connection '{value.Name}' and its saved key?")) return;
        settings.Connections.Remove(value); foreach (var p in settings.Presets.Where(p => p.ConnectionId == value.Id)) p.ConnectionId = null;
        ReloadConnections(settings.Connections.FirstOrDefault());
    });

    private async void RefreshModels_Click(object sender, RoutedEventArgs e)
    {
        if (busy || Connection is not { } connection) return;
        SetBusy(true);
        try
        {
            string modelId = ModelId;
            var models = await client.ModelsAsync(connection, AiSettingsFile.Unprotect(connection), cancellation!.Token);
            connection.Models = models; allModels = models; connection.ModelsUpdated = DateTimeOffset.Now;
            ModelSelector.ItemsSource = models; ModelSelector.SelectedItem = models.FirstOrDefault(m => m.Id == modelId); ModelSelector.Text = modelId;
            AiStatus.Text = $"Loaded {models.Count:N0} models from {connection.Name}. Choose a model ID."; SaveConfiguration(); UpdateModelInfo();
        }
        catch (Exception ex) { Report(ex); }
        finally { FinishOperation(); }
    }

    private void NewPreset_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        CapturePreset(); var name = Dialogs.Name(DialogOwner, "New model preset", "Writing preset"); if (name is null) return;
        var created = preset is null ? new ModelPreset() : Copy(preset); created.Id = Guid.NewGuid(); created.Name = name;
        settings.Presets.Add(created); ReloadPresets(created);
    });
    private void SavePreset_Click(object sender, RoutedEventArgs e) => Guard(() => { CapturePreset(); if (preset is null) return; CaptureInlinePreset(preset); ChatPreparation.ValidatePreset(preset); ReloadPresets(preset); AiStatus.Text = "Model preset saved."; });
    private void DeletePreset_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (preset is null || !Confirm($"Delete preset '{preset.Name}'?")) return;
        settings.Presets.Remove(preset); if (settings.Presets.Count == 0) settings.Presets.Add(new()); ReloadPresets(settings.Presets[0]);
    });
    private static string Number(double? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
    private static double? OptionalNumber(string value) => string.IsNullOrWhiteSpace(value) ? null : double.Parse(value, CultureInfo.InvariantCulture);
    private void CaptureInlinePreset(ModelPreset value)
    {
        if (string.IsNullOrWhiteSpace(PresetNameBox.Text)) throw new InvalidDataException("Enter a preset name.");
        value.Name = PresetNameBox.Text.Trim(); value.SystemPrompt = SystemPromptBox.Text; value.MaxTokens = int.Parse(MaxTokensBox.Text, CultureInfo.InvariantCulture); value.UseCompletionTokenLimit = CompletionTokenLimitToggle.IsChecked == true;
        value.Stream = PresetStreamToggle.IsChecked == true; value.Thinking = PresetThinkingSelector.SelectedItem is ThinkingMode thinking ? thinking : ThinkingMode.Default; value.Context = PresetContextSelector.SelectedItem is ContextScope scope ? scope : ContextScope.CurrentSection;
        value.Temperature = OptionalNumber(TemperatureBox.Text); value.TopP = OptionalNumber(TopPBox.Text); value.FrequencyPenalty = OptionalNumber(FrequencyPenaltyBox.Text); value.PresencePenalty = OptionalNumber(PresencePenaltyBox.Text); value.ThinkingProtocol = ThinkingProtocolSelector.SelectedItem is ThinkingProtocol protocol ? protocol : ThinkingProtocol.Auto;
        value.ContextCharacters = int.Parse(ContextCharactersBox.Text, CultureInfo.InvariantCulture); value.LoreCharacters = int.Parse(LoreCharactersBox.Text, CultureInfo.InvariantCulture); value.Stop = JsonSerializer.Deserialize<List<string>>(StopSequencesBox.Text) ?? []; value.ExtraSampling = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ExtraSamplingBox.Text) ?? [];
        value.CharacterName = CharacterNameBox.Text; value.UserName = UserNameBox.Text; value.CharacterDescription = CharacterDescriptionBox.Text; value.CharacterPersonality = CharacterPersonalityBox.Text; value.Persona = PersonaBox.Text; value.ImportedPrompts = JsonSerializer.Deserialize<List<PromptTemplate>>(ImportedPromptsBox.Text, ProjectStore.Json) ?? [];
    }
    private static string ReadImport(string path)
    {
        if (new FileInfo(path).Length > 4_000_000) throw new InvalidDataException("Import files are limited to 4 MB.");
        return File.ReadAllText(path);
    }
    private void ImportPreset_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        var file = new OpenFileDialog { Filter = "SillyTavern chat completion preset|*.json" }; if (file.ShowDialog(DialogOwner) != true) return;
        var imported = SillyTavernImport.Preset(ReadImport(file.FileName), Path.GetFileNameWithoutExtension(file.FileName));
        ChatPreparation.ValidatePreset(imported.Value); imported.Value.ConnectionId = Connection?.Id;
        settings.Presets.Add(imported.Value); ReloadPresets(imported.Value);
        MessageBox.Show(DialogOwner, string.Join("\n\n", imported.Warnings), "Preset import report", MessageBoxButton.OK, MessageBoxImage.Information);
        AiStatus.Text = "Preset imported. Check the model, prompt variables, and request preview before generating.";
    });

    private void RefreshLore()
    {
        var active = settings.StoryLorebooks.GetValueOrDefault(getContext().StoryId, []);
        loreChoices = settings.Lorebooks.Select(b => new LoreChoice { Book = b, Enabled = active.Contains(b.Id) }).ToList(); LoreList.ItemsSource = loreChoices;
    }
    private void LoreEnabled_Click(object sender, RoutedEventArgs e) => Guard(SaveConfiguration);
    private void ImportLore_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        var file = new OpenFileDialog { Filter = "World Info / lorebook JSON|*.json" }; if (file.ShowDialog(DialogOwner) != true) return;
        var imported = SillyTavernImport.Lorebook(ReadImport(file.FileName), Path.GetFileNameWithoutExtension(file.FileName)); settings.Lorebooks.Add(imported.Value);
        var active = settings.StoryLorebooks.GetValueOrDefault(getContext().StoryId, []); active.Add(imported.Value.Id); settings.StoryLorebooks[getContext().StoryId] = active;
        RefreshLore(); SaveConfiguration(); MessageBox.Show(DialogOwner, string.Join("\n\n", imported.Warnings), "Lorebook import report", MessageBoxButton.OK, MessageBoxImage.Information);
    });
    private void InspectLore_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (LoreList.SelectedItem is not LoreChoice choice) return;
        var entry = Dialogs.Select(DialogOwner, choice.Book.Name, choice.Book.Entries, e => $"{(e.Enabled ? "Enabled" : "Disabled")} · {e.Name}", e => $"Keys: {string.Join(", ", e.Keys)}\nAlways active: {e.Constant}\n\n{e.Content}");
        if (entry is null) return;
        var fields = Dialogs.Form(DialogOwner, "Lore entry", [new("Enabled", entry.Enabled ? "Yes" : "No", ["Yes", "No"]), new("Name", entry.Name), new("Content", entry.Content, Multiline: true), new("Primary keys (JSON array)", JsonSerializer.Serialize(entry.Keys), Multiline: true), new("Always active", entry.Constant ? "Yes" : "No", ["Yes", "No"])], v => { try { _ = JsonSerializer.Deserialize<List<string>>(v["Primary keys (JSON array)"]); return null; } catch { return "Enter a JSON array of keyword strings."; } });
        if (fields is null) return;
        entry.Enabled = fields["Enabled"] == "Yes"; entry.Name = fields["Name"]; entry.Content = fields["Content"]; entry.Keys = JsonSerializer.Deserialize<List<string>>(fields["Primary keys (JSON array)"]) ?? []; entry.Constant = fields["Always active"] == "Yes"; SaveConfiguration();
    });
    private void DeleteLore_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (LoreList.SelectedItem is not LoreChoice choice || !Confirm($"Delete '{choice.Book.Name}' from all stories' AI settings?")) return;
        settings.Lorebooks.Remove(choice.Book); foreach (var ids in settings.StoryLorebooks.Values) ids.Remove(choice.Book.Id); RefreshLore(); SaveConfiguration();
    });

    private (ApiConnection Connection, ModelPreset Preset, PreparedChat Prepared, System.Text.Json.Nodes.JsonObject Body) Prepare()
    {
        CapturePreset(); if (Connection is not { } connection || preset is null) throw new InvalidOperationException("Choose a connection and preset first.");
        ApiEndpoint.Normalize(connection); ChatPreparation.ValidatePreset(preset);
        StoryMemoryContext memoryContext = StoryMemoryContext.Empty;
        if (story.Memory.Enabled)
        {
            var memory = memoryStore.Load(story);
            memoryContext = StoryMemoryRetrieval.Build(story, chapter, section, memory, PromptBox.Text);
        }
        var request = ChatPreparation.Prepare(preset, getContext(), PromptBox.Text, loreChoices.Where(l => l.Enabled).Select(l => l.Book), memoryContext);
        var body = ChatApiClient.BuildRequest(connection, preset, request, connection.Models.FirstOrDefault(m => m.Id == preset.Model));
        return (connection, preset, request, body);
    }
    private void Preview_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        var request = Prepare();
        string memory = request.Prepared.Memory is { } m && m.Included.Count > 0 ? $"\nMemory blocks: {string.Join("; ", m.Included)}\nRetrieved passages: {string.Join("; ", m.Passages.Select(p => p.Chapter + " / " + p.Section))}" : "\nLong-story memory: off or empty";
        RequestPreview.Text = $"POST {new Uri(ApiEndpoint.Normalize(request.Connection), "chat/completions")}\n\nInput: {request.Prepared.Characters:N0} characters (not a tokenizer count)\nMatched lore: {string.Join("; ", request.Prepared.LoreNames)}{memory}\n\nNotes:\n{string.Join("\n", request.Prepared.Warnings)}\n\n" + request.Body.ToJsonString(ProjectStore.Json);
    });
    private void SetBusy(bool value)
    {
        busy = value; ConnectionPanel.IsEnabled = ModelPanel.IsEnabled = PresetPanel.IsEnabled = RequestOptionsPanel.IsEnabled = LoreTab.IsEnabled = PreviewButton.IsEnabled = GenerateButton.IsEnabled = !value;
        PromptBox.IsReadOnly = value; ResponseBox.IsReadOnly = value; StopButton.IsEnabled = value; AppendButton.IsEnabled = !value && !appended && ResponseBox.Text.Length > 0; ClearButton.IsEnabled = !value && ResponseBox.Text.Length > 0;
        if (value) { cancellation = new(); AiStatus.Text = "Connecting…"; }
    }
    private void SaveRecovery()
    {
        string text = ResponseBox?.Text ?? response.ToString();
        if (text.Length > 0 && recoveryPath is not null) { AtomicFile.Write(recoveryPath, text); lastCheckpoint = DateTimeOffset.UtcNow; }
    }
    private void FinishOperation()
    {
        cancellation?.Dispose(); cancellation = null; SetBusy(false);
        try { SaveConfiguration(); } catch (Exception ex) { Report(ex); }
        if (closeAfterStop) { closeAfterStop = false; Close(); }
    }
    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        try
        {
            var request = Prepare(); string key = AiSettingsFile.Unprotect(request.Connection); SaveConfiguration();
            // Retain any previous unfinished response before replacing its preview.
            SaveRecovery(); response.Clear(); ResponseBox.Clear(); OutputInfo.Text = ""; appended = false;
            outputSectionId = section.Id; OutputTab.IsSelected = true;
            recoveryPath = Path.Combine(recoveryFolder, $"ai-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.md"); lastCheckpoint = DateTimeOffset.MinValue;
            SetBusy(true); AiStatus.Text = $"Generating with {request.Preset.Model}. {request.Prepared.Characters:N0} input characters; {request.Prepared.LoreNames.Count} lore entries.";
            var result = await client.GenerateAsync(request.Connection, key, request.Body, chunk =>
            {
                response.Append(chunk); ResponseBox.AppendText(chunk); ResponseBox.ScrollToEnd();
                OutputInfo.Text = $"{response.Length:N0} characters received";
                if (DateTimeOffset.UtcNow - lastCheckpoint > TimeSpan.FromSeconds(1)) SaveRecovery();
            }, cancellation!.Token);
            cancellation.Token.ThrowIfCancellationRequested(); SaveRecovery();
            AiStatus.Text = "Generation complete. Review the response, then append it to the section or clear it.";
            if (result.FinishReason == "length") AiStatus.Text += " The model reached its output limit; the continuation may be unfinished.";
            if (request.Prepared.Warnings.Count > 0) AiStatus.Text += $" {request.Prepared.Warnings.Count} context/import notes are available in request preview.";
        }
        catch (Exception ex) { Report(ex); try { SaveRecovery(); } catch (Exception disk) { AiStatus.Text += "\nRecovery write failed: " + disk.Message + " Copy the visible response before closing."; } }
        finally { if (busy) FinishOperation(); }
    }
    private async void Append_Click(object sender, RoutedEventArgs e)
    {
        if (busy || appended || string.IsNullOrWhiteSpace(ResponseBox.Text)) return;
        try
        {
        SaveRecovery(); string approvedText = ResponseBox.Text; bool saved = append(outputSectionId, approvedText); appended = true; AppendButton.IsEnabled = false;
        AiStatus.Text = saved ? "Retained response appended and saved." : "Appended. Manuscript saving needs attention.";
        if (saved && story.Memory.Enabled && story.Memory.AutomaticProposals) await ProposeMemoryUpdateAsync();
        }
        catch (Exception ex) { Report(ex); }
    }
    private void Clear_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (busy || ResponseBox.Text.Length == 0) return;
        SaveRecovery(); response.Clear(); ResponseBox.Clear(); OutputInfo.Text = ""; recoveryPath = null; appended = false;
        AppendButton.IsEnabled = ClearButton.IsEnabled = false;
        AiStatus.Text = "Generated output cleared. Its recovery copy remains in the project folder.";
    });
    private void ResponseBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (OutputInfo is null || busy) return;
        response.Clear(); response.Append(ResponseBox.Text); OutputInfo.Text = $"{ResponseBox.Text.Length:N0} editable characters";
        AppendButton.IsEnabled = !appended && ResponseBox.Text.Length > 0; ClearButton.IsEnabled = ResponseBox.Text.Length > 0;
    }

    private async Task ProposeMemoryUpdateAsync()
    {
        if (Connection is not { } connection || preset is null) return;
        var memory = memoryStore.Load(story);
        var analysisPreset = Copy(preset); analysisPreset.Stream = false; analysisPreset.Temperature = 0.2; analysisPreset.MaxTokens = Math.Max(4096, preset.MaxTokens); analysisPreset.Thinking = ThinkingMode.Default;
        var prepared = StoryMemoryAnalysis.BuildRequest(story, [chapter], memory, "Update memory after the newly appended passage. Propose only facts, plot-thread changes, summaries, and warnings supported by the manuscript.");
        var body = ChatApiClient.BuildRequest(connection, analysisPreset, prepared, connection.Models.FirstOrDefault(m => m.Id == analysisPreset.Model)); body["stream"] = false;
        SetBusy(true); AiStatus.Text = "Text appended. Creating optional memory proposals with a second model request...";
        try
        {
            var result = await client.GenerateAsync(connection, AiSettingsFile.Unprotect(connection), body, _ => { }, cancellation!.Token);
            var proposals = StoryMemoryAnalysis.Parse(result.Text, story, [chapter], section.Id); proposals.RemoveAll(p => p.Kind is MemoryProposalKind.StorySummary or MemoryProposalKind.StyleGuide);
            foreach (var proposal in proposals) if (!memory.Proposals.Any(p => p.Status == MemoryProposalStatus.Proposed && p.Kind == proposal.Kind && p.ChapterId == proposal.ChapterId && p.Subject.Equals(proposal.Subject, StringComparison.OrdinalIgnoreCase) && p.Content.Equals(proposal.Content, StringComparison.OrdinalIgnoreCase))) memory.Proposals.Add(proposal);
            memoryStore.Save(story, memory);
            AiStatus.Text = $"Text appended. Added {proposals.Count:N0} memory proposals for review in Memory & continuity.";
        }
        finally { FinishOperation(); }
    }
    private void Stop_Click(object sender, RoutedEventArgs e) { cancellation?.Cancel(); AiStatus.Text = "Stopping…"; }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; closeAfterStop = true; cancellation?.Cancel(); AiStatus.Text = "Stopping the active request before closing…"; return; }
        try { SaveRecovery(); SaveConfiguration(); client.Dispose(); }
        catch (Exception ex) { e.Cancel = true; Report(ex); }
    }
}
