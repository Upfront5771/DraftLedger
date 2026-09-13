using System.Text.Json;
using System.Text.Json.Serialization;

namespace DraftLedger.Core;

public enum ApiKind { OpenRouter, LMStudio, OpenAI, Custom }
public enum ThinkingMode { Default, Off, Low, Medium, High }
public enum ThinkingProtocol { Auto, ReasoningEffort, OpenRouterReasoning, TemplateEnableThinking }
public enum ContextScope { None, CurrentSection, StoryThroughSection }

public sealed class ApiConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New connection";
    public ApiKind Kind { get; set; }
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";
    public string ProtectedKey { get; set; } = "";
    public bool AllowPrivateHttp { get; set; }
    public int TimeoutSeconds { get; set; } = 300;
    public List<ApiModel> Models { get; set; } = [];
    public DateTimeOffset? ModelsUpdated { get; set; }
    public DateTimeOffset? RetryAfter { get; set; }
    public override string ToString() => Name;
}

public sealed class ApiModel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int? ContextLength { get; set; }
    public List<string>? SupportedParameters { get; set; }
    public bool? ReasoningMandatory { get; set; }
    public List<string>? ReasoningEfforts { get; set; }
    [JsonIgnore] public string Label => string.IsNullOrWhiteSpace(Name) || Name == Id ? Id : $"{Name} ({Id})";
}

public sealed class PromptTemplate
{
    public string Identifier { get; set; } = "";
    public string Role { get; set; } = "system";
    public string Content { get; set; } = "";
    public bool Marker { get; set; }
}

public sealed class ModelPreset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Writing default";
    public Guid? ConnectionId { get; set; }
    public string Model { get; set; } = "";
    public string SystemPrompt { get; set; } = "You are a creative writing assistant. Follow the author's prompt and continue the manuscript in its established voice. Return only the manuscript text to append, without commentary or reasoning.";
    public List<PromptTemplate> ImportedPrompts { get; set; } = [];
    public double? Temperature { get; set; } = 0.8;
    public double? TopP { get; set; } = 1;
    public double? FrequencyPenalty { get; set; }
    public double? PresencePenalty { get; set; }
    public int MaxTokens { get; set; } = 1024;
    public bool UseCompletionTokenLimit { get; set; }
    public bool Stream { get; set; } = true;
    public ThinkingMode Thinking { get; set; }
    public ThinkingProtocol ThinkingProtocol { get; set; }
    public ContextScope Context { get; set; } = ContextScope.CurrentSection;
    public int ContextCharacters { get; set; } = 24000;
    public int LoreCharacters { get; set; } = 8000;
    public List<string> Stop { get; set; } = [];
    public Dictionary<string, JsonElement> ExtraSampling { get; set; } = [];
    public string CharacterName { get; set; } = "Narrator";
    public string UserName { get; set; } = "Writer";
    public string CharacterDescription { get; set; } = "";
    public string CharacterPersonality { get; set; } = "";
    public string Persona { get; set; } = "";
    public override string ToString() => Name;
}

public sealed class LoreEntry
{
    public string Name { get; set; } = "";
    public string Content { get; set; } = "";
    public List<string> Keys { get; set; } = [];
    public List<string> SecondaryKeys { get; set; } = [];
    public bool Enabled { get; set; } = true;
    public bool Constant { get; set; }
    public bool Selective { get; set; }
    public int SelectiveLogic { get; set; }
    public bool CaseSensitive { get; set; }
    public bool WholeWords { get; set; }
    public int Order { get; set; } = 100;
    public int Position { get; set; } = 1;
}

public sealed class Lorebook
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Lorebook";
    public List<LoreEntry> Entries { get; set; } = [];
}

public sealed class AiSettings
{
    public int FormatVersion { get; set; } = 1;
    public List<ApiConnection> Connections { get; set; } = [];
    public List<ModelPreset> Presets { get; set; } = [new()];
    public List<Lorebook> Lorebooks { get; set; } = [];
    public Dictionary<Guid, List<Guid>> StoryLorebooks { get; set; } = [];
    public Dictionary<Guid, string> PromptDrafts { get; set; } = [];
    public Guid? LastConnection { get; set; }
    public Guid? LastPreset { get; set; }
}

public sealed record ChatMessage(string Role, string Content);
public sealed record WritingContext(Guid StoryId, string StoryTitle, string Synopsis, string ChapterTitle, Guid SectionId, string SectionTitle, string CurrentText, string StoryText);
public sealed record ImportResult<T>(T Value, IReadOnlyList<string> Warnings);
public sealed record LoreSelection(string Before, string After, IReadOnlyList<string> Names, IReadOnlyList<string> Warnings);
public sealed record PreparedChat(IReadOnlyList<ChatMessage> Messages, IReadOnlyList<string> LoreNames, IReadOnlyList<string> Warnings, int Characters, StoryMemoryContext? Memory = null);
