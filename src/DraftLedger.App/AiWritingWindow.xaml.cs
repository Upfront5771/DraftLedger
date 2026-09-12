using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DraftLedger.Core;
using Microsoft.Win32;

namespace DraftLedger.App;

public partial class AiWritingWindow : Window
{
    private readonly AiSettingsFile settingsFile;
    private readonly AiSettings settings;
    private readonly Func<WritingContext> getContext;
    private readonly Func<string, bool> append;
    private readonly string recoveryFolder;
    private readonly Story story;
    private readonly Chapter chapter;
    private readonly Section section;
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
    private sealed class LoreChoice
    {
        public required Lorebook Book { get; init; }
        public bool Enabled { get; set; }
        public string Label => $"{Book.Name} ({Book.Entries.Count} entries)";
    }
    private ApiConnection? Connection => ConnectionSelector.SelectedItem as ApiConnection;

    public AiWritingWindow(string path, Func<WritingContext> getContext, Func<string, bool> append, string recoveryFolder, Story story, Chapter chapter, Section section, ProjectStore projectStore)
    {
        this.getContext = getContext; this.append = append; this.recoveryFolder = recoveryFolder; this.story = story; this.chapter = chapter; this.section = section; this.projectStore = projectStore;
        settingsFile = new(path); settings = settingsFile.Load();
        InitializeComponent(); Height = Math.Min(930, SystemParameters.WorkArea.Height - 40);
        var context = getContext(); TargetLabel.Text = $"Append to: {context.StoryTitle} / {context.ChapterTitle} / {context.SectionTitle}";
        ThinkingSelector.ItemsSource = Enum.GetValues<ThinkingMode>(); ContextSelector.ItemsSource = Enum.GetValues<ContextScope>();
        ConnectionSelector.ItemsSource = settings.Connections;
        ConnectionSelector.SelectedItem = settings.Connections.FirstOrDefault(c => c.Id == settings.LastConnection) ?? settings.Connections.FirstOrDefault();
        PresetSelector.ItemsSource = settings.Presets;
        PresetSelector.SelectedItem = settings.Presets.FirstOrDefault(p => p.Id == settings.LastPreset) ?? settings.Presets[0];
        PromptBox.Text = settings.PromptDrafts.GetValueOrDefault(context.StoryId, "");
        LoadPreset((ModelPreset)PresetSelector.SelectedItem); RefreshLore(); loading = false; UpdateModelInfo();
    }

    private void Report(Exception ex) => AiStatus.Text = ex switch
    {
        OperationCanceledException => cancellation?.IsCancellationRequested == true ? "Stopped. Any partial text is retained for review." : "The request timed out. Partial text is retained; no automatic retry was sent.",
        System.Net.Http.HttpRequestException => "Could not reach the API. Check the endpoint, TLS certificate, and whether the local server is running. " + ex.Message,
        _ => ex.Message
    };
    private void Guard(Action action) { try { action(); } catch (Exception ex) { Report(ex); } }
    private bool Confirm(string message) => MessageBox.Show(this, message, "AI writing", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
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
        DestinationInfo.Text = Connection is null ? "Add an OpenRouter, LM Studio, OpenAI, or custom API connection." : $"Destination: {Connection.BaseUrl}\nOnly the selected context and enabled matching lore are sent when you generate. Remote APIs may charge for requests. Thinking Off works only if the model and selected protocol support it.";
    }

    private void AddConnection_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        var type = Dialogs.Form(this, "Connection type", [new("Provider", "LMStudio", Enum.GetNames<ApiKind>())]); if (type is null) return;
        var kind = Enum.Parse<ApiKind>(type["Provider"]);
        EditConnection(new ApiConnection { Kind = kind, Name = kind == ApiKind.LMStudio ? "LM Studio (local)" : kind.ToString(), BaseUrl = kind switch { ApiKind.LMStudio => "http://localhost:1234/v1", ApiKind.OpenAI => "https://api.openai.com/v1", ApiKind.OpenRouter => "https://openrouter.ai/api/v1", _ => "https://your-provider.example/v1" } }, true);
    });
    private void EditConnection_Click(object sender, RoutedEventArgs e) => Guard(() => { if (Connection is not null) EditConnection(Connection, false); });
    private void EditConnection(ApiConnection original, bool create)
    {
        var value = Copy(original);
        var fields = Dialogs.Form(this, "API connection", [new("Name", value.Name), new("API base URL", value.BaseUrl), new("API key (blank keeps saved key)", "", Secret: true), new("Remove saved key", "No", ["No", "Yes"]), new("Allow unencrypted HTTP on private LAN IP", value.AllowPrivateHttp ? "Yes" : "No", ["No", "Yes"]), new("Timeout in seconds", value.TimeoutSeconds.ToString())], v =>
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
        CapturePreset(); var name = Dialogs.Name(this, "New model preset", "Writing preset"); if (name is null) return;
        var created = preset is null ? new ModelPreset() : Copy(preset); created.Id = Guid.NewGuid(); created.Name = name;
        settings.Presets.Add(created); ReloadPresets(created);
    });
    private void SavePreset_Click(object sender, RoutedEventArgs e) => Guard(() => { CapturePreset(); if (preset is not null) ChatPreparation.ValidatePreset(preset); SaveConfiguration(); AiStatus.Text = "Model preset saved."; });
    private void DeletePreset_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (preset is null || !Confirm($"Delete preset '{preset.Name}'?")) return;
        settings.Presets.Remove(preset); if (settings.Presets.Count == 0) settings.Presets.Add(new()); ReloadPresets(settings.Presets[0]);
    });
    private static string Number(double? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
    private static double? OptionalNumber(string value) => string.IsNullOrWhiteSpace(value) ? null : double.Parse(value, CultureInfo.InvariantCulture);
    private void EditPreset_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        CapturePreset(); if (preset is null) return; var edited = Copy(preset);
        var values = Dialogs.Form(this, "Preset settings (blank sampling = provider default)", [new("Name", edited.Name), new("System instruction (additional to imported prompts)", edited.SystemPrompt, Multiline: true), new("Max output tokens", edited.MaxTokens.ToString()), new("Use max_completion_tokens", edited.UseCompletionTokenLimit ? "Yes" : "No", ["No", "Yes"]), new("Temperature", Number(edited.Temperature)), new("Top P", Number(edited.TopP)), new("Frequency penalty", Number(edited.FrequencyPenalty)), new("Presence penalty", Number(edited.PresencePenalty)), new("Thinking protocol", edited.ThinkingProtocol.ToString(), Enum.GetNames<ThinkingProtocol>()), new("Context character limit", edited.ContextCharacters.ToString()), new("Lore character budget", edited.LoreCharacters.ToString()), new("Stop sequences (JSON array)", JsonSerializer.Serialize(edited.Stop), Multiline: true), new("Extra sampling (JSON object)", JsonSerializer.Serialize(edited.ExtraSampling), Multiline: true), new("Character name", edited.CharacterName), new("User name", edited.UserName), new("Character description", edited.CharacterDescription, Multiline: true), new("Character personality", edited.CharacterPersonality, Multiline: true), new("User persona", edited.Persona, Multiline: true), new("Imported prompt blocks (JSON array)", JsonSerializer.Serialize(edited.ImportedPrompts, ProjectStore.Json), Multiline: true)], v =>
        {
            try
            {
                edited.Name = v["Name"].Trim(); edited.SystemPrompt = v["System instruction (additional to imported prompts)"];
                edited.MaxTokens = int.Parse(v["Max output tokens"]); edited.UseCompletionTokenLimit = v["Use max_completion_tokens"] == "Yes";
                edited.Temperature = OptionalNumber(v["Temperature"]); edited.TopP = OptionalNumber(v["Top P"]); edited.FrequencyPenalty = OptionalNumber(v["Frequency penalty"]); edited.PresencePenalty = OptionalNumber(v["Presence penalty"]);
                edited.ThinkingProtocol = Enum.Parse<ThinkingProtocol>(v["Thinking protocol"]); edited.ContextCharacters = int.Parse(v["Context character limit"]); edited.LoreCharacters = int.Parse(v["Lore character budget"]);
                edited.Stop = JsonSerializer.Deserialize<List<string>>(v["Stop sequences (JSON array)"]) ?? []; edited.ExtraSampling = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(v["Extra sampling (JSON object)"]) ?? [];
                edited.CharacterName = v["Character name"]; edited.UserName = v["User name"]; edited.CharacterDescription = v["Character description"]; edited.CharacterPersonality = v["Character personality"]; edited.Persona = v["User persona"];
                edited.ImportedPrompts = JsonSerializer.Deserialize<List<PromptTemplate>>(v["Imported prompt blocks (JSON array)"], ProjectStore.Json) ?? [];
                ChatPreparation.ValidatePreset(edited); return null;
            }
            catch (Exception ex) { return "Check the preset fields: " + ex.Message; }
        });
        if (values is null) return;
        settings.Presets[settings.Presets.IndexOf(preset)] = edited; ReloadPresets(edited); AiStatus.Text = "Preset settings saved.";
    });
    private static string ReadImport(string path)
    {
        if (new FileInfo(path).Length > 4_000_000) throw new InvalidDataException("Import files are limited to 4 MB.");
        return File.ReadAllText(path);
    }
    private void ImportPreset_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        var file = new OpenFileDialog { Filter = "SillyTavern chat completion preset|*.json" }; if (file.ShowDialog(this) != true) return;
        var imported = SillyTavernImport.Preset(ReadImport(file.FileName), Path.GetFileNameWithoutExtension(file.FileName));
        ChatPreparation.ValidatePreset(imported.Value); imported.Value.ConnectionId = Connection?.Id;
        settings.Presets.Add(imported.Value); ReloadPresets(imported.Value);
        MessageBox.Show(this, string.Join("\n\n", imported.Warnings), "Preset import report", MessageBoxButton.OK, MessageBoxImage.Information);
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
        var file = new OpenFileDialog { Filter = "World Info / lorebook JSON|*.json" }; if (file.ShowDialog(this) != true) return;
        var imported = SillyTavernImport.Lorebook(ReadImport(file.FileName), Path.GetFileNameWithoutExtension(file.FileName)); settings.Lorebooks.Add(imported.Value);
        var active = settings.StoryLorebooks.GetValueOrDefault(getContext().StoryId, []); active.Add(imported.Value.Id); settings.StoryLorebooks[getContext().StoryId] = active;
        RefreshLore(); SaveConfiguration(); MessageBox.Show(this, string.Join("\n\n", imported.Warnings), "Lorebook import report", MessageBoxButton.OK, MessageBoxImage.Information);
    });
    private void InspectLore_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (LoreList.SelectedItem is not LoreChoice choice) return;
        var entry = Dialogs.Select(this, choice.Book.Name, choice.Book.Entries, e => $"{(e.Enabled ? "Enabled" : "Disabled")} · {e.Name}", e => $"Keys: {string.Join(", ", e.Keys)}\nAlways active: {e.Constant}\n\n{e.Content}");
        if (entry is null) return;
        var fields = Dialogs.Form(this, "Lore entry", [new("Enabled", entry.Enabled ? "Yes" : "No", ["Yes", "No"]), new("Name", entry.Name), new("Content", entry.Content, Multiline: true), new("Primary keys (JSON array)", JsonSerializer.Serialize(entry.Keys), Multiline: true), new("Always active", entry.Constant ? "Yes" : "No", ["Yes", "No"])], v => { try { _ = JsonSerializer.Deserialize<List<string>>(v["Primary keys (JSON array)"]); return null; } catch { return "Enter a JSON array of keyword strings."; } });
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
        SaveRecovery(); string approvedText = ResponseBox.Text; bool saved = append(approvedText); appended = true; AppendButton.IsEnabled = false;
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
