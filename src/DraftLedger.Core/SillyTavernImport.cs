using System.Text.Json;

namespace DraftLedger.Core;

public static class SillyTavernImport
{
    public static readonly HashSet<string> SamplingKeys = ["top_k", "min_p", "top_a", "repetition_penalty", "seed"];
    private static JsonDocument Read(string text)
    {
        if (text.Length > 4_000_000) throw new InvalidDataException("Imports are limited to 4 MB of JSON.");
        var doc = JsonDocument.Parse(text, new() { MaxDepth = 64 });
        if (doc.RootElement.ValueKind != JsonValueKind.Object) { doc.Dispose(); throw new InvalidDataException("Expected a JSON object."); }
        return doc;
    }
    internal static string Str(JsonElement e, string name, string fallback = "") => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
    internal static bool Bool(JsonElement e, string name, bool fallback = false) => e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;
    internal static double? Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
    internal static int Int(JsonElement e, string name, int fallback) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : fallback;
    private static List<string> Strings(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var value)) return [];
        if (value.ValueKind == JsonValueKind.String) return [value.GetString() ?? ""];
        return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString() ?? "").Where(v => v.Length > 0).ToList() : [];
    }

    public static ImportResult<ModelPreset> Preset(string text, string filename)
    {
        using var doc = Read(text); var root = doc.RootElement; var warnings = new List<string>();
        if (!root.TryGetProperty("prompts", out _) && !root.TryGetProperty("openai_max_tokens", out _) && !root.TryGetProperty("chat_completion_source", out _) && !root.TryGetProperty("temperature", out _)) throw new InvalidDataException("This does not look like a chat-completion preset.");
        var preset = new ModelPreset
        {
            Name = Str(root, "name", filename), SystemPrompt = "", Temperature = Num(root, "temperature"), TopP = Num(root, "top_p"),
            FrequencyPenalty = Num(root, "frequency_penalty"), PresencePenalty = Num(root, "presence_penalty"),
            MaxTokens = Int(root, "openai_max_tokens", Int(root, "max_tokens", 1024)), Stream = Bool(root, "stream_openai", Bool(root, "stream", true)),
            Stop = Strings(root, "stop")
        };
        string source = Str(root, "chat_completion_source");
        preset.Model = Str(root, source switch { "openrouter" => "openrouter_model", "custom" => "custom_model", _ => "openai_model" }, Str(root, "model"));
        if (preset.Model == "OR_Website") { preset.Model = ""; warnings.Add("The OpenRouter website-default model was not imported. Choose an explicit model."); }
        foreach (string name in SamplingKeys) if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number) preset.ExtraSampling[name] = value.Clone();
        if (root.TryGetProperty("stop_strings", out _)) { preset.Stop.AddRange(Strings(root, "stop_strings")); warnings.Add("stop_strings were imported as stop sequences."); }
        string effort = Str(root, "reasoning_effort");
        preset.Thinking = effort switch { "none" => ThinkingMode.Off, "low" => ThinkingMode.Low, "medium" => ThinkingMode.Medium, "high" => ThinkingMode.High, _ => ThinkingMode.Default };
        if (effort.Length > 0 && preset.Thinking == ThinkingMode.Default && effort != "auto") warnings.Add($"Reasoning effort '{effort}' is not mapped; provider default will be used.");
        if (root.TryGetProperty("prompts", out var prompts) && prompts.ValueKind == JsonValueKind.Array)
        {
            var items = prompts.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Object).ToList();
            var byId = items.Where(p => Str(p, "identifier").Length > 0).GroupBy(p => Str(p, "identifier")).ToDictionary(g => g.Key, g => g.First());
            List<JsonElement>? order = null;
            if (root.TryGetProperty("prompt_order", out var orders) && orders.ValueKind == JsonValueKind.Array)
            {
                var lists = orders.EnumerateArray().Where(o => o.ValueKind == JsonValueKind.Object && o.TryGetProperty("order", out var a) && a.ValueKind == JsonValueKind.Array).ToList();
                var chosen = lists.FirstOrDefault(o => Int(o, "character_id", 0) == 100001);
                if (chosen.ValueKind == JsonValueKind.Undefined) chosen = lists.FirstOrDefault();
                if (chosen.ValueKind != JsonValueKind.Undefined) order = chosen.GetProperty("order").EnumerateArray().Where(o => o.ValueKind == JsonValueKind.Object && Bool(o, "enabled", true)).ToList();
            }
            var active = order is null ? items.Where(p => Bool(p, "enabled", true)) : order.Where(o => byId.ContainsKey(Str(o, "identifier"))).Select(o => byId[Str(o, "identifier")]);
            foreach (var p in active)
            {
                string role = Str(p, "role", "system");
                if (role is not ("system" or "user" or "assistant")) { warnings.Add($"Prompt role '{role}' was mapped to system."); role = "system"; }
                preset.ImportedPrompts.Add(new() { Identifier = Str(p, "identifier"), Role = role, Content = Str(p, "content"), Marker = Bool(p, "marker") });
                if (Int(p, "injection_position", 0) != 0 || Int(p, "injection_depth", 0) != 0) warnings.Add("Depth-based prompts are kept in prompt order; chat-depth insertion is not emulated.");
            }
        }
        else
        {
            foreach (string key in new[] { "main_prompt", "nsfw_prompt", "jailbreak_prompt" }) if (Str(root, key).Length > 0) preset.ImportedPrompts.Add(new() { Identifier = key, Content = Str(root, key) });
        }
        warnings.Add("Imported active prompt blocks/order, model, streaming, supported sampling values, and stop sequences. Connections, API keys, custom headers/bodies, tools, scripts, assistant prefills, and unsupported ST settings were not imported.");
        warnings.Add("Supported macros: char, user, scenario, description, personality, persona, input, lastMessage, and trim. Other macros remain visible in request preview.");
        return new(preset, warnings.Distinct().ToList());
    }

    public static ImportResult<Lorebook> Lorebook(string text, string filename)
    {
        using var doc = Read(text); var root = doc.RootElement; var warnings = new List<string>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("character_book", out var nested)) root = nested;
        else if (root.TryGetProperty("character_book", out var book)) root = book;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("entries", out var entries) || entries.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) throw new InvalidDataException("Expected a World Info / lorebook JSON object with entries.");
        var result = new Lorebook { Name = Str(root, "name", filename) };
        IEnumerable<JsonElement> source = entries.ValueKind == JsonValueKind.Array ? entries.EnumerateArray() : entries.EnumerateObject().Select(p => p.Value);
        foreach (var entry in source)
        {
            if (result.Entries.Count >= 10000) throw new InvalidDataException("Lorebooks are limited to 10,000 entries.");
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var ext = entry.TryGetProperty("extensions", out var x) && x.ValueKind == JsonValueKind.Object ? x : entry;
            var keys = Strings(entry, "key"); if (keys.Count == 0) keys = Strings(entry, "keys");
            var secondary = Strings(entry, "keysecondary"); if (secondary.Count == 0) secondary = Strings(entry, "secondary_keys");
            var item = new LoreEntry
            {
                Name = Str(entry, "comment", Str(entry, "name", $"Entry {result.Entries.Count + 1}")), Content = Str(entry, "content"), Keys = keys, SecondaryKeys = secondary,
                Enabled = !Bool(entry, "disable") && Bool(entry, "enabled", true), Constant = Bool(entry, "constant"), Selective = Bool(entry, "selective", secondary.Count > 0),
                SelectiveLogic = Int(entry, "selectiveLogic", Int(ext, "selectiveLogic", 0)),
                CaseSensitive = Bool(entry, "caseSensitive", Bool(entry, "case_sensitive", Bool(ext, "caseSensitive"))),
                WholeWords = Bool(entry, "matchWholeWords", Bool(ext, "matchWholeWords", true)),
                Order = Int(entry, "order", Int(entry, "insertion_order", 100)), Position = Int(entry, "position", Int(ext, "position", 1))
            };
            if (Str(entry, "position") == "before_char") item.Position = 0;
            if (item.Position is not (0 or 1)) { item.Position = 1; warnings.Add("Nonstandard/depth positions are mapped to after-context reference information."); }
            bool probability = Bool(entry, "useProbability", Bool(ext, "useProbability"));
            if (probability && Int(entry, "probability", Int(ext, "probability", 100)) != 100) { item.Enabled = false; warnings.Add("Probabilistic entries were disabled. This version supports deterministic lore matching."); }
            result.Entries.Add(item);
        }
        warnings.Add("Imported content, enabled state, constant entries, keywords, secondary-key logic, case/whole-word matching, order, and before/after placement. Recursion, vector search, groups, timers, automation, character filters, and extra scanning sources are not emulated.");
        return new(result, warnings.Distinct().ToList());
    }
}
