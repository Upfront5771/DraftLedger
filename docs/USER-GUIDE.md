# DraftLedger user guide

## Updating to 0.3.0

Close DraftLedger, extract the updated release, and replace the previous `DraftLedger.exe`. Your project folders and settings do not need migration. Version 0.3.0 adds optional long-story memory, local retrieval of relevant earlier passages, reviewed story-bible updates, chapter summaries, plot-thread tracking, continuity warnings, a resizable collapsible right pane, and API-assisted story synopsis generation.

## First launch

1. Extract the Windows release ZIP to a folder such as `Documents\DraftLedger-App`.
2. Double-click `DraftLedger.exe`.
3. Select **New story**, enter a title, and begin writing in the Opening section.

No DraftLedger account, internet connection, separate .NET runtime installation, or administrator access is needed to write, read, organize, import, export, or back up projects. An internet connection and a provider account/key are needed only if you choose a remote AI connection. LM Studio can run locally. This preview is unsigned. The delivered build is for x64 Windows PCs.

## Where your writing goes

New projects default to the **DraftLedger** folder inside your Windows **Documents** folder. If Windows redirects Documents to OneDrive, that folder may be synchronized by Windows. DraftLedger itself does not connect to OneDrive or any other cloud service. Use **Settings > New project location** to choose a different local folder.

**Show project files** opens the current project's folder. It contains:

- `project.json`: title, author, synopsis, goals, chapter/section names and ordering, statuses, and scene notes.
- `chapters/<chapter-id>/<section-id>.md`: your writing as ordinary UTF-8 Markdown files.
- `backups/<section-id>/`: earlier section versions.
- `backups/project/`: previous project metadata.
- `recovery/`: copies saved when an outside edit conflicts with your open draft.
- `notes/` and `characters/`: reserved portable folders for future features.
- `memory/`: optional long-story memory in readable JSON and Markdown files, created only after memory is enabled for a story.

Chapter and section filenames use stable IDs, so renaming or reordering cannot accidentally move or overwrite your text. Names and reading order are recorded in `project.json`. Chapter metadata is centralized there, rather than duplicated into separate `chapter.json` files.

Preferences and the disposable library index are stored in `%LOCALAPPDATA%\DraftLedger`. The SQLite database is not required to reconstruct your manuscript. Changing the default project location does not move existing projects; they remain registered at their current paths.

AI connections, model presets, prompt drafts, and imported lorebooks are stored in `%LOCALAPPDATA%\DraftLedger\ai-settings.json`. API keys in that file are encrypted with Windows Data Protection for the current Windows user and bound to the saved endpoint. They are not included in project folders or project ZIP backups.

To move between computers, use **Back up project as ZIP**, copy and extract the ZIP on the other computer, and choose **Open project folder** on the library screen. Select the folder containing `project.json`. You can also copy the complete project folder while DraftLedger is closed.

## Organize a story

- Add chapters and sections with the buttons above the outline.
- Select a chapter to open its first section. Expand a chapter to choose another section.
- Use **Rename**, **Copy**, and **Delete** for the selected outline item.
- Move chapters or sections with **↑ / ↓**, or drag an item before another item at the same level. Section dragging stays within its current chapter.
- Use **Details** on a chapter to set its status, word target, and summary.
- Use **Story details and goal** to change the title, author, status, target, synopsis, or the story's memory setting. A configured AI connection can generate an editable one-paragraph synopsis.
- Use the right-hand notes box for the current scene's purpose or planning notes.
- **Archive / restore** moves a story between the active and archived library views. Its files stay in place.

Removing an item takes it out of the manuscript and word totals. Its text file remains on disk, and the prior metadata is retained in history. There is no trash-restore button in this release. To recover a removed chapter or section, close DraftLedger, make a copy of the project folder, and restore the appropriate `backups/project/*.json` file as `project.json`. Reopen the project. Metadata retention is bounded, so ZIP backups are better for long-term recovery.

## Edit and count

Type directly, paste, or import `.txt` / `.md` files into the **Editor** tab. Switch to **Read** to see the current section with Markdown formatting. The Read tab is read-only; switch back to Editor to change your text. Both modes use native Windows controls and work offline.

| Type in Editor | Appearance in Read |
| --- | --- |
| `*quiet thought*` | *quiet thought* |
| `**important line**` | **important line** |
| `***strong feeling***` | ***strong feeling*** |
| `_quiet thought_` | *quiet thought* |
| `\*literal asterisks\*` | Visible asterisks without italics |
| A word enclosed in backticks | Literal code, with no emphasis parsing |
| `# Chapter heading` | Large heading |
| `- First item` | Bullet list item |
| `> A quoted passage` | Indented quote |

Use blank lines to separate paragraphs. A single line break normally wraps into the same paragraph; two trailing spaces before a newline create an explicit line break. Unmatched asterisks remain visible until completed. Emphasis does not cross blank paragraph breaks.

The preview refreshes when you open Read, change sections, import/restore text, or apply font/theme settings. Switching tabs preserves the Editor text, caret, and undo history. The normal autosave timer continues to save the source Markdown. Select and copy text in either tab; selection word counts use the active view. **Find** and **Ctrl+F / Ctrl+H** switch to Editor for searching and replacing.

Links display their labels. Images show an alt-text placeholder. HTML is displayed literally. The preview does not load online content. Exports and whole-section statistics continue to use the original Markdown source.

| Shortcut | Action |
| --- | --- |
| Ctrl+S | Save immediately |
| Ctrl+F / Ctrl+H | Open find and replace |
| Ctrl+Z / Ctrl+Y | Undo / redo within the current editing visit |
| Ctrl+A | Select all in the focused text box |
| F11 | Toggle focus mode |
| Esc | Leave focus mode |

Undo history resets when changing sections, preventing an undo in one section from restoring text from another. Find and replace are case-insensitive. **Replace all** affects only the current section and can be undone. **Search story** returns text matches across sections. Library search matches titles and synopses, not manuscript contents.

Word rules:

- Hyphenated words and contractions count as one by default.
- Numbers count as words. Decimal and comma-separated number tokens remain joined.
- Standalone punctuation does not count.
- You can change hyphen and number handling in Settings.
- Unicode letters and combining accents are supported. CJK text is counted as contiguous tokens, not language-specific dictionary segmentation.
- Whole-section/chapter/story statistics count Markdown source text. The Read tab interprets supported formatting for display, but words in links, code, and other source constructs still count in manuscript totals.
- Characters include spaces and line endings; the second character count excludes all whitespace. These are UTF-16 code units, so some emoji occupy more than one character unit.
- Blank lines separate paragraphs. Sentence splitting is a punctuation-based estimate, with no abbreviation model.
- Page, reading, and speaking estimates use 250 words/page, 250 words/minute, and 130 words/minute.

The session counter measures net word changes made in the editor since opening the story. Deletions can make it negative. It resets when the story is opened again, including reload. AI text appended to the section is included in this net count. It is not a persisted daily writing record. The daily target is a stored reference goal in this release.

## AI writing

Open a story section and expand **AI WRITING** in the right pane. DraftLedger opens a separate writing window tied to that section. Enter a prompt, choose the connection, model, preset, context, streaming, and thinking setting, then select **Generate**. The reply remains editable in the generated manuscript preview. Review or revise it, then choose **Append to section** to add it after a blank line and save it, or choose **Clear output** to remove it from the preview. The current section is snapshotted only when you append.

The AI window is optional. It makes no request until you choose **Refresh models** or **Generate**. **Build request preview** assembles the JSON locally and shows the destination, messages, context size, matched lore, and import notes. Credentials are excluded from the preview.

### Connections and model lists

Use **Add** beside Connection and choose a provider:

| Provider | Default API base | Key |
| --- | --- | --- |
| OpenRouter | `https://openrouter.ai/api/v1` | OpenRouter API key |
| OpenAI | `https://api.openai.com/v1` | OpenAI API key |
| LM Studio | `http://localhost:1234/v1` | Usually blank |
| Custom | Enter the provider's OpenAI-compatible `/v1` base | Provider-specific |

For LM Studio, load a model, start its local server, add the default LM Studio connection, and choose **Refresh models**. To use another computer on your private network, enter its numeric private IP address and explicitly enable private-LAN HTTP. Public endpoints require HTTPS. URLs with credentials, query strings, fragments, or a final `/models` or `/chat/completions` path are rejected.

Choose **Refresh models** to read the selected endpoint's OpenAI-compatible model list. Pick a result or type an exact model ID if the provider does not list it. Model availability, context limits, billing, and acceptable-use rules belong to the provider.

Keys are saved only when entered. Leaving the key field blank keeps the existing encrypted key; **Remove saved key** deletes it. Changing an endpoint requires entering the new endpoint's key or removing the old one. Windows encryption ties a saved key to the current user profile and endpoint, so moving `ai-settings.json` to another account or changing the endpoint requires re-entering it.

DraftLedger identifies itself as `DraftLedger/0.3.0`; it does not impersonate a browser. It permits one active AI request, disables repeated Generate actions while running, blocks HTTP redirects so credentials cannot be forwarded, honors a provider's 429 cooldown across restarts, and never automatically retries a failed request. These measures reduce accidental request bursts, but only the provider can determine account access or enforcement.

### Presets, thinking, and streaming

A model preset stores its connection, exact model ID, system instruction, output limit, sampling values, stop sequences, context and lore budgets, stream setting, and thinking behavior. **New** copies the current preset. **Edit** exposes the full set of supported fields. **Save** retains the choices currently visible in the AI window.

**Thinking: Default** omits a reasoning setting. **Off**, **Low**, **Medium**, and **High** send the selected setting using the preset's thinking protocol. Auto uses OpenRouter's reasoning object for OpenRouter and `reasoning_effort` for other providers. `TemplateEnableThinking` is available for compatible local chat templates. Thinking controls vary by provider/model. DraftLedger blocks Off when model metadata says reasoning is mandatory, and it reports rejected parameters without retrying. Reasoning fields and `<think>` or `<analysis>` blocks are excluded from appended text.

Streaming shows manuscript text as it arrives. **Stop** cancels the active request. Completed and partial replies remain in the preview and are never appended automatically. Review the text and choose **Append to section** if wanted, or **Clear output** to empty the preview. Generated output is checkpointed in the project's `recovery` folder during a request, including before it is cleared or an older preview is replaced.

Context choices are **None**, **CurrentSection**, and **StoryThroughSection**. StoryThroughSection includes the ordered manuscript from the beginning through the target section. The preset's character limit keeps the tail if the context is too large. Character limits are safeguards rather than tokenizer counts, so check the provider's actual model context limit.

### SillyTavern imports and lorebooks

**Import ST preset** accepts a SillyTavern chat-completion preset JSON file. It imports active prompt blocks/order, model, streaming, common sampling settings, stop sequences, and mapped reasoning effort. It does not import API keys, connections, custom headers/bodies, scripts, tools, assistant prefills, or every extension setting. The import report names these limits. Review the imported model, prompt variables, and request preview before use.

Supported prompt macros are `char`, `user`, `scenario`, `description`, `personality`, `persona`, `input`, `lastMessage`, and `trim`. Unknown macros remain visible in request preview. Depth-based prompt placement is preserved in order but is not emulated at chat depth.

The **Lorebooks** tab imports SillyTavern World Info/lorebook JSON. Enable books per story and inspect or edit individual entries. Matching supports constant entries, primary and secondary keys, regular-expression keys, case and whole-word options, common selective logic, ordering, and before/after placement. Only matching entries that fit the preset's lore character budget are sent. Probabilistic entries below 100 percent are disabled on import so matching stays deterministic. Recursion, vector search, groups, timers, automation, character filters, and extra scan sources are not emulated.

Remote AI requests send the prompt, selected manuscript context, enabled matching lore, preset instructions, and provider settings to the selected endpoint. Review the request preview before sending private writing. Provider charges may apply. Local LM Studio requests stay on the configured local or private-network endpoint.

## Long-story memory and continuity

Memory is optional and disabled for every story until you turn it on. Use the **Enable long-story memory** checkbox in Story Details or the right pane. Disabling it later stops memory retrieval and injection but keeps the memory files. Use **Delete memory** in the memory manager only when you want to remove the working memory; DraftLedger moves it to the project's recovery folder.

The memory manager contains:

- **Story so far**: a compact rolling synopsis of the established manuscript.
- **Story bible**: canonical facts about characters, places, objects, relationships, rules, and timeline details. Facts can be locked.
- **Style guide**: voice, tense, point of view, tone, and recurring stylistic choices.
- **Open plot threads**: unresolved promises, mysteries, goals, and conflicts.
- **Chapter summaries**: one summary per chapter, with stale markers when the chapter changes afterward.
- **Proposed updates**: editable model suggestions waiting for approval or rejection.
- **Warnings**: possible contradictions and stale summaries for the writer to review or resolve.

Choose **Analyze current chapter** to ask the configured model for proposed facts, threads, warnings, and a chapter summary. Choose **Analyze complete story** to process missing or stale chapters in separate requests. DraftLedger shows the number of requests before starting. After every chapter is current, the same command can make one synthesis request for the story-so-far summary, style guide, and cross-chapter threads.

Analysis never changes canonical memory directly. Review each proposal, edit it if needed, and choose **Approve** or **Reject**. If a proposed fact conflicts with an existing fact, approval creates a continuity warning rather than silently deleting the older fact. Locked facts stay visible as authoritative context.

When memory is enabled, ordinary AI writing requests include approved memory plus a limited number of relevant older passages. Passage selection happens locally using section boundaries, keywords, exact names, and recency. No embedding service or separate vector database is required. Use **Preview memory context** before generating to inspect exactly what was selected. Adjust the memory character budget and retrieved-passage count in the memory manager.

**Automatically propose memory updates after appending AI text** is a separate opt-in setting. When enabled, appending generated prose starts a second model request to create proposals for that chapter. It does not approve anything, and it makes no background request while you are simply editing or reading.

Memory improves continuity but is not an infallible fact checker. Keep important facts concise, lock genuinely canonical items, resolve warnings, and refresh stale chapter summaries after substantial revisions.

## Saving and recovery

DraftLedger schedules a save within about 650 ms of the first unsaved edit. Continuous typing does not postpone saving indefinitely. Moving between sections, opening another story, returning to the library, or closing the window flushes pending changes. **Ctrl+S** saves immediately. The status bar reports whether saving succeeded.

Writes use a flushed temporary file and replacement. A section's previous disk text is snapshotted on its first edit in a session and then at the configured interval when edits are saved. The default interval is five minutes, with up to 50 snapshots per section. Snapshots are change-driven, not a background full-project backup schedule. **Snapshot now** saves a version on demand.

**Version history** previews earlier section text and restores the selected version after confirmation. The current draft is snapshotted before restoration. Snapshot filenames use UTC timestamps.

If a section changes in another editor, DraftLedger pauses a conflicting save and places the current local draft in `recovery/`. Use **Reload from disk** to reopen the external copy after preserving unsaved editor text and pending metadata. Avoid editing the same project concurrently in multiple applications or on multiple computers.

If a save fails, the draft remains open and normal window closing is canceled. Fix the storage issue and press Ctrl+S, or copy the text to a safe location. Unsaved input still in memory can be lost if Windows or the process terminates before a successful save. Snapshots on the same disk do not protect against disk failure.

**Back up project as ZIP** creates a full portable backup, including retained files, notes, metadata, snapshots, and recovery copies. Choose a destination outside the project folder. A user-selected NAS or synchronized folder can be a backup destination, but DraftLedger does not manage synchronization or simultaneous writers.

## Import and export

Imports support `.txt` and `.md` using UTF-8 or BOM-marked UTF-16/UTF-32. If an older encoding cannot be read, reopen that file in another editor and save it as UTF-8 first. Imports do not alter the original file.

Choose a new story, new chapter, section in the current chapter, or replacement of the current section. Replacement is confirmed and snapshotted.

Exports support the current section, current chapter, or whole story in plain text or Markdown. A section export preserves its exact text. Chapter and story exports combine sections in outline order, adding chapter headings and blank-line separators. Plain-text export keeps any Markdown syntax you typed; it does not convert Markdown to formatted prose.

Word, RTF, PDF, and EPUB are not supported in this version. For later formatting, open a text/Markdown export in your preferred word processor.

## Appearance

Settings include seven themes: **System**, **Pen & Paper**, **Muted Sage**, **Retro Terminal**, **Midnight Ink**, **Charcoal**, and **Deep Navy**. System follows the Windows light/dark choice when DraftLedger starts or settings are applied. Pen & Paper and Muted Sage are light themes. Retro Terminal, Midnight Ink, Charcoal, and Deep Navy are dark themes.

Each theme controls the canvas, manuscript paper, side panels, tab headers, buttons, form fields, dropdown menus, progress bars, text selection, and both focused and unfocused outline selections. Chapter and section names remain readable when keyboard focus moves to the editor or another control. Selected tabs emphasize only their header, so manuscript and settings content retain their intended weight. Editor and Read body text use normal weight with relaxed line spacing. Settings also include editor font and size, spell checking, language, counting rules, goals, snapshot interval/retention, and storage location. Spell checking depends on the spelling resources available in Windows. Installing additional language resources is a Windows task.

Focus mode hides the outline, statistics panel, and count status, and maximizes the window. The title bar and editing controls remain available.
