using System.Text;
using System.Text.RegularExpressions;

namespace DraftLedger.Core;

public static class ChatPreparation
{
    public static PreparedChat Prepare(ModelPreset preset, WritingContext context, string prompt, IEnumerable<Lorebook> books, StoryMemoryContext? memory = null)
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new InvalidDataException("Enter a prompt first.");
        if (prompt.Length > 32000) throw new InvalidDataException("The prompt is limited to 32,000 characters.");
        string manuscript = preset.Context switch { ContextScope.CurrentSection => context.CurrentText, ContextScope.StoryThroughSection => context.StoryText, _ => "" };
        var warnings = new List<string>();
        if (manuscript.Length > preset.ContextCharacters) { manuscript = manuscript[^preset.ContextCharacters..]; if (manuscript.Length > 0 && char.IsLowSurrogate(manuscript[0])) manuscript = manuscript[1..]; warnings.Add("Only the end of the selected manuscript context fits the configured character limit."); }
        var lore = LoreMatcher.Select(books, manuscript + "\n" + prompt, preset.LoreCharacters);
        warnings.AddRange(lore.Warnings);
        var macros = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["char"] = preset.CharacterName, ["user"] = preset.UserName, ["scenario"] = context.Synopsis,
            ["description"] = preset.CharacterDescription, ["personality"] = preset.CharacterPersonality, ["persona"] = preset.Persona,
            ["input"] = prompt, ["lastMessage"] = prompt, ["trim"] = ""
        };
        string Expand(string value) => Regex.Replace(value, @"\{\{([^{}]+)\}\}", m =>
        {
            if (macros.TryGetValue(m.Groups[1].Value.Trim(), out string? replacement)) return replacement;
            warnings.Add($"Unresolved macro: {m.Value}"); return m.Value;
        }, RegexOptions.None, TimeSpan.FromMilliseconds(100));
        var messages = new List<ChatMessage>(); var inserted = new HashSet<string>();
        void Add(string role, string content) { if (!string.IsNullOrWhiteSpace(content)) messages.Add(new(role, content)); }
        void Marker(string identifier)
        {
            if (!inserted.Add(identifier)) return;
            switch (identifier)
            {
                case "worldInfoBefore": Add("system", Expand(lore.Before)); break;
                case "worldInfoAfter": Add("system", Expand(lore.After)); break;
                case "chatHistory":
                    if (manuscript.Length > 0) Add("user", $"Manuscript context from {context.StoryTitle} / {context.ChapterTitle} / {context.SectionTitle}:\n\n{manuscript}");
                    break;
                case "charDescription": Add("system", preset.CharacterDescription); break;
                case "charPersonality": Add("system", preset.CharacterPersonality); break;
                case "personaDescription": Add("system", preset.Persona); break;
                case "scenario": Add("system", context.Synopsis); break;
                case "dialogueExamples": warnings.Add("The dialogue-examples marker has no character-card examples to insert."); break;
                default: warnings.Add($"Unsupported prompt marker: {identifier}"); break;
            }
        }
        Add("system", Expand(preset.SystemPrompt));
        if (!string.IsNullOrWhiteSpace(memory?.Text)) Add("system", "Long-story memory and continuity context. Treat locked/canonical facts as authoritative. Do not mention this context in the manuscript.\n\n" + memory.Text);
        // Unspecified standard blocks still appear once; explicit markers control their location.
        var markers = preset.ImportedPrompts.Where(p => p.Marker).Select(p => p.Identifier).ToHashSet();
        if (!markers.Contains("worldInfoBefore")) Marker("worldInfoBefore");
        foreach (var part in preset.ImportedPrompts)
        {
            if (part.Marker) Marker(part.Identifier);
            else Add(part.Role, Expand(part.Content));
        }
        Marker("chatHistory"); Marker("worldInfoAfter");
        Add("user", prompt);
        int length = messages.Sum(m => m.Content.Length);
        if (length > 240000) throw new InvalidDataException("The prepared request exceeds 240,000 characters. Reduce context, lore, or imported prompts.");
        if (memory is not null) warnings.AddRange(memory.Warnings);
        return new(messages, lore.Names, warnings.Distinct().ToList(), length, memory);
    }

    public static void ValidatePreset(ModelPreset preset)
    {
        if (string.IsNullOrWhiteSpace(preset.Name)) throw new InvalidDataException("Enter a preset name.");
        if (preset.MaxTokens is < 1 or > 65536) throw new InvalidDataException("Output limit must be between 1 and 65,536 tokens.");
        if (preset.ContextCharacters is < 1 or > 200000 || preset.LoreCharacters is < 0 or > 64000) throw new InvalidDataException("Context must be 1 to 200,000 characters; lore budget must be 0 to 64,000.");
        void Range(double? value, double min, double max, string name) { if (value.HasValue && (!double.IsFinite(value.Value) || value < min || value > max)) throw new InvalidDataException($"{name} must be {min} to {max}, or blank for provider default."); }
        Range(preset.Temperature, 0, 2, "Temperature"); Range(preset.TopP, 0.000001, 1, "Top P"); Range(preset.FrequencyPenalty, -2, 2, "Frequency penalty"); Range(preset.PresencePenalty, -2, 2, "Presence penalty");
        if (preset.Stop.Count > 16 || preset.Stop.Any(s => string.IsNullOrEmpty(s) || s.Length > 1000)) throw new InvalidDataException("Use at most 16 nonempty stop sequences, each at most 1,000 characters. Some providers allow fewer.");
        if (preset.ImportedPrompts.Any(p => p.Role is not ("system" or "user" or "assistant"))) throw new InvalidDataException("Prompt roles must be system, user, or assistant.");
        foreach (var pair in preset.ExtraSampling)
        {
            if (!SillyTavernImport.SamplingKeys.Contains(pair.Key) || pair.Value.ValueKind != System.Text.Json.JsonValueKind.Number || !pair.Value.TryGetDouble(out var n) || !double.IsFinite(n)) throw new InvalidDataException("Extra sampling accepts only numeric top_k, min_p, top_a, repetition_penalty, and seed.");
            if (pair.Key is "min_p" or "top_a") Range(n, 0, 1, pair.Key);
            if (pair.Key == "repetition_penalty") Range(n, 0.000001, 2, pair.Key);
            if (pair.Key is "top_k" or "seed" && !pair.Value.TryGetInt32(out _)) throw new InvalidDataException($"{pair.Key} must be a 32-bit integer.");
            if (pair.Key == "top_k" && n < 0) throw new InvalidDataException("top_k cannot be negative.");
        }
    }
}
