# Architecture and data safety

## Boundaries

`DraftLedger.Core` has no Windows UI dependency. It owns statistics, JSON models, local file persistence, snapshots, recovery, imports/exports, the derived SQLite index, AI request preparation, lore matching, SillyTavern JSON import, and the OpenAI-compatible HTTP client. The WPF application owns selection, editing, commands, dialogs, timers, appearance, Windows credential protection, and local settings. Core integration tests execute on Linux or Windows.

`project.json` is the authoritative ordered outline. It references sections by stable GUIDs. Text and in-memory statistics are excluded from JSON serialization. Every section is saved separately as UTF-8 Markdown. Chapter metadata lives in `project.json`; the design intentionally avoids two authoritative copies of chapter ordering.

SQLite stores only derived story-level metadata. If the cache cannot open or rebuild, the app still reads portable projects directly. Search uses in-memory project data in this MVP. Preferences use an atomic local JSON file. Neither cache nor preferences is included in a project ZIP.

AI settings use a separate atomic JSON file. API key bytes are protected with Windows DPAPI for the current user, with entropy derived from the connection ID and normalized endpoint. Plaintext key bytes are zeroed after protection/decryption where managed buffers permit. Keys are attached only to the selected endpoint. HTTP redirects are disabled. Remote endpoints require HTTPS; loopback HTTP is allowed for local servers, and numeric private-LAN HTTP requires explicit opt-in.

The AI window prepares chat-completion messages from an explicit context scope, prompt, preset, and enabled lorebooks. Request preview serializes the same body without authorization headers. The client supports JSON replies and SSE chat-completion streams, extracts only message content, filters common tagged reasoning blocks, and never executes tool calls. Generated output remains in a review preview until the user explicitly appends or clears it. Output is checkpointed to the project recovery folder while it arrives and before it is cleared or replaced.

Long-story memory is optional per story. Its canonical representation is `memory/memory.json`, with readable Markdown projections for the story so far, story bible, open plot threads, and chapter summaries. Approved facts may be locked. Model-produced changes are stored as proposals and do not become canonical until the writer approves them. External edits are protected by the same disk-hash conflict approach used for manuscripts, and deleting memory moves the folder to recovery rather than permanently removing it.

When memory is enabled, prompt preparation combines structured approved memory with a bounded set of locally retrieved earlier passages. Retrieval is section-aware and scores keywords, exact entities, and recency. It requires no embedding API, vector database, network service, or background indexing. The current chapter or complete story can be analyzed in explicit user-started batches. Optional analysis after appending AI prose is a separate setting, performs a second visible provider request, and only creates reviewable proposals.

One request may run at a time per AI window. Status failures are surfaced without automatic retries. A 429 response stores the provider cooldown in AI settings and blocks requests until it expires. Response, event, prompt, context, lore, and output sizes are bounded. These are client safety controls, not a promise about provider policy or account status.

## Save sequence

1. Update the current section's in-memory text and statistics immediately.
2. Start a 650 ms save timer if no timer is already running.
3. Before saving, compare the current disk text hash with the hash at the last load/save.
4. On conflict, save a separate recovery copy and pause the operation.
5. Snapshot the previous text when the configured interval is due.
6. Write a temporary file in the same directory, flush it to disk, and replace the destination.
7. Update the saved hash, then write metadata through the same atomic-file helper.
8. On failure, preserve dirty state and prevent ordinary navigation/closing from discarding the draft.

This is atomic per file, not a multi-file database transaction or protection against every power-loss scenario. New text files are created before their metadata references are committed. Structural deletion only removes a reference, retaining the text file. A crash can therefore leave an unreferenced file, which is safer than a reference to text that was deleted by the application. Metadata snapshots provide recovery for structural changes.

The file hash guard reduces external-overwrite risk but is not a distributed lock. A competing application could write between the comparison and replacement. Do not edit a project from multiple writers. Network filesystems and folder sync may have weaker replacement guarantees than a local disk.

## Markdown reading view

The Editor text remains the only editable manuscript. Markdig 1.3.2 parses a display-only projection in `MarkdownReading`, with HTML parsing disabled. The projection contains plain text runs and style flags plus paragraph/heading/list/quote/code structure. It contains no resource loader or navigation actions. `ReadingRenderer` converts that projection into native WPF `FlowDocument` elements, displayed in a read-only `RichTextBox`.

Switching modes does not assign to `Editor.Text`, reload the section, or clear undo history. The Read document is cached until source text, section, or appearance changes. No HTML renderer, browser component, remote image fetching, or Markdown-to-source round trip is used.

## Counting rules

Compiled Unicode regexes identify words. Hyphenated tokens, apostrophes, and decimal numbers are joined by default. Statistics are recomputed for the edited section; chapter/story/library word totals are derived from cached section statistics. Parsing is source-text oriented, without rendered Markdown semantics or language-specific tokenization. Sentence and time metrics are estimates.

## Portability and versioning

Project format version is 1. Unsupported versions, malformed JSON, duplicate IDs, and missing referenced text prevent loading the affected project. Other projects remain available. IDs are typed GUIDs rather than paths, preventing manuscript metadata from supplying arbitrary section paths. Files remain readable with ordinary text editors.

Snapshots are bounded per section. Previous metadata is separately bounded using the same retention setting. ZIP exports include the entire project directory and are forbidden inside that directory to prevent recursive self-inclusion. Text exports are forbidden inside registered project folders to avoid overwriting manuscript assets.

## Windows distribution

Publish with `win-x64` or `win-arm64`, `SelfContained=true`, `PublishSingleFile=true`, and `IncludeNativeLibrariesForSelfExtract=true`. WPF trimming is disabled. The user runs a single executable; the .NET bundler extracts native runtime libraries to its normal per-user temporary location as needed. The application manifest requests `asInvoker`.

No embedded browser, DraftLedger login mechanism, updater, telemetry, or background network service is included. The optional AI window uses `HttpClient` only for endpoints the user configures and requests the user starts. Manuscript management remains available with the network disconnected. Initial source dependency restoration is a build-time network activity. Runtime and package dependencies are bundled with the release.
