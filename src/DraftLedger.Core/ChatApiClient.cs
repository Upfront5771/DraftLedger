using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DraftLedger.Core;

public sealed class ApiFailure(string message, int? status = null, DateTimeOffset? retryAt = null) : Exception(message)
{
    public int? Status { get; } = status;
    public DateTimeOffset? RetryAt { get; } = retryAt;
}
public sealed record GenerationResult(string Text, string? FinishReason);

public static class ApiEndpoint
{
    public static Uri Normalize(ApiConnection connection)
    {
        if (!Uri.TryCreate(connection.BaseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) throw new InvalidDataException("Enter an API base URL without embedded credentials, a query, or a fragment.");
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && (uri.IsLoopback || connection.AllowPrivateHttp && IsPrivateAddress(uri.Host)))) throw new InvalidDataException("Use HTTPS, localhost HTTP, or explicitly allow HTTP for a private LAN IP address.");
        if (uri.AbsolutePath.TrimEnd('/').EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.TrimEnd('/').EndsWith("/models", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Enter the base URL ending in /v1, rather than a /models or /chat/completions URL.");
        return uri;
    }
    private static bool IsPrivateAddress(string host)
    {
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var address)) return false;
        byte[] bytes = address.GetAddressBytes();
        return bytes.Length == 4 ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168 : (bytes[0] & 0xfe) == 0xfc;
    }
}

public sealed class ChatApiClient : IDisposable
{
    private readonly HttpClient http;
    private readonly SemaphoreSlim gate = new(1, 1);
    public ChatApiClient(HttpMessageHandler? handler = null)
    {
        http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static JsonObject BuildRequest(ApiConnection connection, ModelPreset preset, PreparedChat prepared, ApiModel? model = null)
    {
        ChatPreparation.ValidatePreset(preset);
        if (string.IsNullOrWhiteSpace(preset.Model)) throw new InvalidDataException("Choose a model or enter its exact model ID.");
        var request = new JsonObject { ["model"] = preset.Model, ["stream"] = preset.Stream, ["messages"] = new JsonArray(prepared.Messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()) };
        request[connection.Kind == ApiKind.OpenAI || preset.UseCompletionTokenLimit ? "max_completion_tokens" : "max_tokens"] = preset.MaxTokens;
        bool Supports(string key) => model?.SupportedParameters is null || model.SupportedParameters.Contains(key);
        void Number(string key, double? value) { if (value.HasValue && Supports(key)) request[key] = value.Value; }
        Number("temperature", preset.Temperature); Number("top_p", preset.TopP); Number("frequency_penalty", preset.FrequencyPenalty); Number("presence_penalty", preset.PresencePenalty);
        if (preset.Stop.Count > 0 && Supports("stop")) request["stop"] = new JsonArray(preset.Stop.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
        foreach (var pair in preset.ExtraSampling) if (Supports(pair.Key)) request[pair.Key] = JsonNode.Parse(pair.Value.GetRawText());
        if (preset.Thinking != ThinkingMode.Default)
        {
            bool off = preset.Thinking == ThinkingMode.Off;
            if (off && model?.ReasoningMandatory == true) throw new InvalidDataException("This model reports that reasoning is mandatory. Choose Default or an available effort level.");
            string effort = off ? "none" : preset.Thinking.ToString().ToLowerInvariant();
            if (model?.ReasoningEfforts is { Count: > 0 } efforts && !efforts.Contains(effort) && !off) throw new InvalidDataException("The chosen thinking level is not supported by this model.");
            var protocol = preset.ThinkingProtocol == ThinkingProtocol.Auto ? connection.Kind == ApiKind.OpenRouter ? ThinkingProtocol.OpenRouterReasoning : ThinkingProtocol.ReasoningEffort : preset.ThinkingProtocol;
            if (protocol == ThinkingProtocol.OpenRouterReasoning)
            {
                if (!Supports("reasoning") && !Supports("reasoning_effort")) throw new InvalidDataException("This model does not advertise a reasoning parameter. Use Default.");
                request["reasoning"] = off ? new JsonObject { ["enabled"] = false, ["exclude"] = true } : new JsonObject { ["effort"] = effort, ["exclude"] = true };
            }
            else if (protocol == ThinkingProtocol.TemplateEnableThinking) request["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = !off };
            else request["reasoning_effort"] = effort;
        }
        return request;
    }

    private HttpRequestMessage Request(ApiConnection connection, string key, HttpMethod method, string path)
    {
        if (connection.RetryAfter > DateTimeOffset.UtcNow) throw new ApiFailure($"The provider requested a cooldown until {connection.RetryAfter.Value.LocalDateTime:T}.", 429, connection.RetryAfter);
        if (key.Any(char.IsWhiteSpace)) throw new InvalidDataException("An API key cannot contain whitespace.");
        var request = new HttpRequestMessage(method, new Uri(ApiEndpoint.Normalize(connection), path));
        request.Headers.UserAgent.ParseAdd("DraftLedger/0.3.0");
        if (key.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (connection.Kind == ApiKind.OpenRouter) request.Headers.Add("X-OpenRouter-Title", "DraftLedger");
        return request;
    }

    private static string Safe(string text, string key) => key.Length > 0 ? text.Replace(key, "[redacted]", StringComparison.Ordinal) : text;
    private static async Task CheckStatus(HttpResponseMessage response, ApiConnection connection, string key, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        string body = await ReadLimited(response.Content, 32000, token);
        string detail = "";
        try { using var doc = JsonDocument.Parse(body); if (doc.RootElement.TryGetProperty("error", out var error)) detail = error.ValueKind == JsonValueKind.Object ? SillyTavernImport.Str(error, "message") : error.ToString(); }
        catch (JsonException) { }
        detail = Safe(detail, key); if (detail.Length > 1500) detail = detail[..1500];
        DateTimeOffset? retry = null;
        if ((int)response.StatusCode == 429)
        {
            retry = response.Headers.RetryAfter?.Date ?? DateTimeOffset.UtcNow + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60));
            if (retry < DateTimeOffset.UtcNow) retry = DateTimeOffset.UtcNow.AddSeconds(5);
            connection.RetryAfter = retry;
        }
        string summary = (int)response.StatusCode switch
        {
            401 => "Authentication failed. Check the saved key for this connection.",
            402 => "The provider reports insufficient credits or billing access.",
            403 => "The provider denied this request. Check account/model access and provider policy.",
            404 => "Endpoint or model not found. Check the base URL and model ID.",
            429 => "Rate limited. A provider cooldown is active; no automatic retry was sent.",
            400 or 422 => "The provider rejected a parameter or context size. Try provider-default thinking/sampling settings or less context.",
            >= 300 and < 400 => "The endpoint redirected the request. Redirects are blocked to protect credentials; update the base URL.",
            _ => "The provider could not complete this request. No automatic retry was sent."
        };
        throw new ApiFailure($"HTTP {(int)response.StatusCode}: {summary}" + (detail.Length > 0 ? "\n\n" + detail : ""), (int)response.StatusCode, retry);
    }

    public async Task<List<ApiModel>> ModelsAsync(ApiConnection connection, string key, CancellationToken token)
    {
        if (!await gate.WaitAsync(0, token)) throw new InvalidOperationException("A request is already running.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(45));
            using var request = Request(connection, key, HttpMethod.Get, "models");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            await CheckStatus(response, connection, key, timeout.Token);
            using var doc = JsonDocument.Parse(await ReadLimited(response.Content, 16000000, timeout.Token));
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) throw new ApiFailure("The model list did not contain an OpenAI-compatible data array.");
            var result = new List<ApiModel>();
            foreach (var item in data.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object))
            {
                string id = SillyTavernImport.Str(item, "id"); if (id.Length == 0) continue;
                var model = new ApiModel { Id = id, Name = SillyTavernImport.Str(item, "name", id), ContextLength = item.TryGetProperty("context_length", out var length) && length.TryGetInt32(out int n) ? n : null };
                if (item.TryGetProperty("supported_parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Array) model.SupportedParameters = parameters.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
                if (item.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.Object)
                {
                    model.ReasoningMandatory = SillyTavernImport.Bool(reasoning, "mandatory");
                    if (reasoning.TryGetProperty("supported_efforts", out var efforts) && efforts.ValueKind == JsonValueKind.Array) model.ReasoningEfforts = efforts.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
                }
                result.Add(model);
            }
            return result.DistinctBy(m => m.Id).OrderBy(m => m.Label, StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally { gate.Release(); }
    }

    public async Task<GenerationResult> GenerateAsync(ApiConnection connection, string key, JsonObject payload, Action<string> onText, CancellationToken token)
    {
        if (!await gate.WaitAsync(0, token)) throw new InvalidOperationException("A request is already running.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(connection.TimeoutSeconds, 30, 1800)));
            using var request = Request(connection, key, HttpMethod.Post, "chat/completions");
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            await CheckStatus(response, connection, key, timeout.Token);
            var output = new StringBuilder(); var filter = new ThinkingTextFilter(); string? finish = null; bool done = false;
            void Emit(string raw, bool final = false)
            {
                string text = filter.Add(raw, final);
                if (output.Length + text.Length > 1_000_000) throw new ApiFailure("Output exceeded the 1,000,000-character safeguard; partial text is retained.");
                if (text.Length > 0) { output.Append(text); onText(text); }
            }
            void Parse(string json, bool streaming)
            {
                if (json.Trim() == "[DONE]") { done = true; return; }
                using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
                if (root.TryGetProperty("error", out var error)) throw new ApiFailure("Provider stream error: " + Safe(error.ValueKind == JsonValueKind.Object ? SillyTavernImport.Str(error, "message", "Unknown provider error") : error.ToString(), key));
                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return;
                var choice = choices.EnumerateArray().FirstOrDefault(c => SillyTavernImport.Int(c, "index", 0) == 0);
                if (choice.ValueKind != JsonValueKind.Object) return;
                string reason = SillyTavernImport.Str(choice, "finish_reason"); if (reason.Length > 0) finish = reason;
                if (finish is "content_filter" or "tool_calls" or "function_call") throw new ApiFailure("The provider returned a filtered response or a tool request. DraftLedger does not run tools or append that response.");
                if (!choice.TryGetProperty(streaming ? "delta" : "message", out var message) || message.ValueKind != JsonValueKind.Object) return;
                if (SillyTavernImport.Str(message, "refusal").Length > 0) throw new ApiFailure("The provider declined the prompt. Review its policy and adjust the request.");
                if (message.TryGetProperty("content", out var content))
                {
                    if (content.ValueKind == JsonValueKind.String) Emit(content.GetString() ?? "");
                    else if (content.ValueKind == JsonValueKind.Array)
                        foreach (var part in content.EnumerateArray()) if (part.ValueKind == JsonValueKind.Object && SillyTavernImport.Str(part, "type") is "text" or "output_text") Emit(SillyTavernImport.Str(part, "text"));
                }
            }
            bool sse = response.Content.Headers.ContentType?.MediaType?.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;
            if (sse)
            {
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token), Encoding.UTF8); var frame = new StringBuilder();
                while (await reader.ReadLineAsync(timeout.Token) is { } line)
                {
                    if (line.Length == 0)
                    {
                        if (frame.Length > 0) { Parse(frame.ToString(), true); frame.Clear(); if (done) break; }
                    }
                    else if (line.StartsWith("data:", StringComparison.Ordinal)) { if (frame.Length > 0) frame.Append('\n'); frame.Append(line.AsSpan(5).TrimStart()); if (frame.Length > 2_000_000) throw new ApiFailure("An oversized stream event was rejected."); }
                }
                if (frame.Length > 0 && !done) Parse(frame.ToString(), true);
                if (!done && finish is null) throw new ApiFailure("The stream ended before completion. Partial text is retained and was not appended.");
            }
            else Parse(await ReadLimited(response.Content, 8_000_000, timeout.Token), false);
            timeout.Token.ThrowIfCancellationRequested(); Emit("", true);
            if (string.IsNullOrWhiteSpace(output.ToString())) throw new ApiFailure("The model returned no manuscript text. It may have used its output budget for reasoning. Try a larger limit or a different thinking setting.");
            return new(output.ToString(), finish);
        }
        finally { gate.Release(); }
    }

    private static async Task<string> ReadLimited(HttpContent content, int limit, CancellationToken token)
    {
        using var reader = new StreamReader(await content.ReadAsStreamAsync(token), Encoding.UTF8); var result = new StringBuilder(); char[] buffer = new char[8192]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0) { if (result.Length + count > limit) throw new ApiFailure("The API response exceeded the size limit."); result.Append(buffer, 0, count); }
        return result.ToString();
    }
    public void Dispose() { http.Dispose(); gate.Dispose(); }
}

public sealed class ThinkingTextFilter
{
    private string pending = "";
    private string? close;
    private static readonly string[] Open = ["<think>", "<analysis>"];
    public string Add(string chunk, bool final = false)
    {
        pending += chunk; var output = new StringBuilder();
        while (pending.Length > 0)
        {
            if (close is not null)
            {
                int index = pending.IndexOf(close, StringComparison.OrdinalIgnoreCase);
                if (index < 0) { pending = final ? "" : pending[^Math.Min(pending.Length, close.Length - 1)..]; break; }
                pending = pending[(index + close.Length)..]; close = null; continue;
            }
            var match = Open.Select(tag => (Tag: tag, Index: pending.IndexOf(tag, StringComparison.OrdinalIgnoreCase))).Where(x => x.Index >= 0).OrderBy(x => x.Index).FirstOrDefault();
            if (match.Tag is not null) { output.Append(pending[..match.Index]); pending = pending[(match.Index + match.Tag.Length)..]; close = "</" + match.Tag[1..]; continue; }
            int hold = 0;
            if (!final) foreach (string tag in Open) for (int n = 1; n < tag.Length && n <= pending.Length; n++) if (pending.EndsWith(tag[..n], StringComparison.OrdinalIgnoreCase)) hold = Math.Max(hold, n);
            output.Append(pending[..(pending.Length - hold)]); pending = hold > 0 ? pending[^hold..] : ""; break;
        }
        return output.ToString();
    }
}
