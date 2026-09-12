# DraftLedger 0.3.0 preview

## Optional long-story memory

Added a per-story **Long-story memory and continuity** mode for novels and other large projects. It is off by default and can be enabled from Story Details or the right pane. Writing, saving, and ordinary AI generation continue to work normally when it is disabled.

The memory manager provides an editable story-so-far summary, style guide, canonical facts, open plot threads, chapter summaries, and continuity warnings. Facts can be locked. Memory files remain inside the portable project folder as readable JSON and Markdown.

## Reviewed memory updates

DraftLedger never allows a model to silently rewrite canonical memory. Analysis results arrive as editable proposals. The writer can approve or reject each proposal, and contradictory facts create a continuity warning instead of silently replacing established information.

**Analyze current chapter** updates one chapter at a time. **Analyze complete story** works in chapter-sized batches so a long manuscript does not need to fit into one model request. A final synthesis request can update the story-so-far summary, style guide, and cross-chapter plot threads after chapter summaries are current.

Optional automatic proposals after appending generated prose use a separate checkbox. This makes a second provider request and still requires manual approval.

## Local retrieval for older context

AI requests can combine approved structured memory with a bounded set of relevant passages from earlier sections. Retrieval is local and section-aware, using keywords, exact entities, and recency. It does not require an embedding service, vector database, or internet connection beyond the model request itself.

**Preview memory context** shows the exact story-bible items, summaries, warnings, and retrieved manuscript passages selected for the next request. Character and passage limits are configurable.

## Workspace and AI improvements

- The right pane is wider and resizable, with collapsible At a Glance, Scene Notes, Memory, AI Writing, Settings, and Files & Recovery sections.
- Generated AI output is editable before it is appended.
- Typing in the model selector filters the list by any matching characters in its ID or display name.
- Saved connection and preset selectors display their friendly names.
- Story Details can generate an editable one-paragraph synopsis using the last selected AI connection and the available full-story context.
- Status help explains Planned, Drafting, Revising, and Complete.

## Compatibility and safety

Existing projects, settings, AI connections, presets, lorebooks, and encrypted keys remain compatible. Memory is not created or sent unless the writer enables it for that story. Remote providers receive only the content shown by request and memory previews.

Deleting story memory is recoverable: the memory folder is moved into the project's recovery area. Disabling memory keeps its files but stops memory injection and retrieval.

## Validation and constraints

Release compilation passes with zero warnings and errors. The cross-platform suite has **93 passing checks**, including memory opt-in, retrieval, prompt injection, portable storage, proposal parsing and approval, stale-summary warnings, external-edit conflicts, recoverable deletion, resizable workspace controls, editable output, model filtering, synopsis generation, and the existing writing and AI workflows.

The Windows-only WPF regression executable compiles successfully but requires Windows to run. The graphical interface, DPAPI credential round trip, and live provider interoperability were not executed in this Linux build environment.
