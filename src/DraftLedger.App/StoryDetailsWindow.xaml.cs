using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Windows;
using DraftLedger.Core;

namespace DraftLedger.App;

public partial class StoryDetailsWindow : Window
{
    private readonly Story story;
    private readonly AiSettingsFile aiFile;
    private readonly ChatApiClient client = new();
    private CancellationTokenSource? cancellation;
    private bool busy;
    public bool Saved { get; private set; }

    public StoryDetailsWindow(Story story, string aiSettingsPath)
    {
        this.story = story; aiFile = new(aiSettingsPath); InitializeComponent();
        StatusSelector.ItemsSource = new[] { "Planned", "Drafting", "Revising", "Complete" };
        TitleBox.Text = story.Title; AuthorBox.Text = story.Author; StatusSelector.SelectedItem = story.Status; TargetBox.Text = story.Target.ToString(); SynopsisBox.Text = story.Synopsis; MemoryToggle.IsChecked = story.Memory.Enabled;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text)) { StatusText.Text = "Enter a story title."; return; }
        if (!int.TryParse(TargetBox.Text, out int target) || target is < 0 or > 10000000) { StatusText.Text = "Enter a word target from 0 to 10,000,000."; return; }
        story.Title = TitleBox.Text.Trim(); story.Author = AuthorBox.Text.Trim(); story.Status = StatusSelector.SelectedItem?.ToString() ?? "Drafting"; story.Target = target; story.Synopsis = SynopsisBox.Text.Trim();
        if (MemoryToggle.IsChecked == true && story.Memory.Mode == MemoryMode.Off) story.Memory.Mode = MemoryMode.FullRetrieval; else if (MemoryToggle.IsChecked != true) story.Memory.Mode = MemoryMode.Off;
        Saved = true; DialogResult = true;
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        try
        {
            var ai = aiFile.Load(); var connection = ai.Connections.FirstOrDefault(c => c.Id == ai.LastConnection) ?? throw new InvalidOperationException("Choose an AI connection in AI Writing first.");
            var preset = ai.Presets.FirstOrDefault(p => p.Id == ai.LastPreset) ?? ai.Presets.FirstOrDefault() ?? throw new InvalidOperationException("Create an AI preset first.");
            if (string.IsNullOrWhiteSpace(preset.Model)) throw new InvalidOperationException("Choose a model in AI Writing first.");
            string source = SynopsisSource(); var messages = new List<ChatMessage> { new("system", "Write one concise, compelling paragraph that accurately summarizes the supplied story. Preserve major characters, central conflict, stakes, and trajectory. Do not invent details. Return only the synopsis paragraph."), new("user", source) };
            var prepared = new PreparedChat(messages, [], [], messages.Sum(m => m.Content.Length));
            var requestPreset = JsonSerializer.Deserialize<ModelPreset>(JsonSerializer.Serialize(preset, ProjectStore.Json), ProjectStore.Json)!; requestPreset.Stream = false; requestPreset.Temperature = 0.3; requestPreset.MaxTokens = Math.Max(700, Math.Min(2000, preset.MaxTokens)); requestPreset.Thinking = ThinkingMode.Default;
            var body = ChatApiClient.BuildRequest(connection, requestPreset, prepared, connection.Models.FirstOrDefault(m => m.Id == requestPreset.Model)); body["stream"] = false;
            cancellation = new(); busy = true; GenerateButton.IsEnabled = SaveButton.IsEnabled = false; StatusText.Text = $"Generating synopsis with {requestPreset.Model}...";
            var result = await client.GenerateAsync(connection, AiSettingsFile.Unprotect(connection), body, _ => { }, cancellation.Token); SynopsisBox.Text = result.Text.Trim(); StatusText.Text = "Synopsis generated. Edit it if needed, then save details.";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; MessageBox.Show(this, ex.Message, "Generate synopsis", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { busy = false; cancellation?.Dispose(); cancellation = null; GenerateButton.IsEnabled = SaveButton.IsEnabled = true; }
    }

    private string SynopsisSource()
    {
        var output = new StringBuilder($"Story title: {story.Title}\n\n");
        try
        {
            var memory = new StoryMemoryStore().Load(story);
            if (memory.StorySoFar.Length > 0) output.Append("Approved story-so-far:\n").AppendLine(memory.StorySoFar).AppendLine();
            if (memory.ChapterSummaries.Count > 0) output.Append("Approved chapter summaries:\n").AppendLine(string.Join("\n", memory.ChapterSummaries.Select(c => c.ChapterTitle + ": " + c.Summary))).AppendLine();
        }
        catch { }
        string manuscript = ProjectStore.Export(story, null, null, false);
        int remaining = 210000 - output.Length;
        if (remaining > 0)
        {
            if (manuscript.Length <= remaining) output.Append("Manuscript:\n").Append(manuscript);
            else { int half = Math.Max(1, remaining / 2); output.Append("Beginning of manuscript:\n").Append(manuscript[..half]).Append("\n\nEnd of manuscript:\n").Append(manuscript[^half..]); }
        }
        return output.ToString();
    }

    private void Window_Closing(object? sender, CancelEventArgs e) { if (busy) { e.Cancel = true; cancellation?.Cancel(); return; } client.Dispose(); }
}
