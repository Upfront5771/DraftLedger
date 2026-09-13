# Validation record

## Completed in the development environment

Environment: Linux x64, .NET SDK 10.0.401, .NET runtime 10.0.12.

- Release compilation of the WPF application: **passed, zero warnings and zero errors**.
- Self-contained Windows x64 single-file publication: **passed**. Native runtime and SQLite libraries are bundled for extraction. WPF trimming is disabled.
- Core, memory/retrieval, AI integration, Markdown projection, theme/icon, and XAML contract test program: **103 checks passed**.
- Added binding regression: **failed against the original XAML, then passed with the one-way binding fix**.
- Windows WPF regression executable: **compiled successfully**; runtime execution requires Windows and was not performed here.
- Markdown projection checks cover italic, multiple emphasized phrases, bold, combined/nested emphasis, escapes, unmatched delimiters, inline/fenced code, block structure, entities, empty/plain text, image/link labels, and literal HTML.
- Windows-only checks additionally validate native WPF run styles, hidden emphasis delimiters, and rendered block types. There are now 6 Windows regression checks; these were compiled, not executed, in this environment.
- Output executable identified as a Windows x86-64 PE GUI application.

Core tests cover Unicode/combining marks; apostrophes; hyphens; numbers; punctuation; paragraphs/sentences; whitespace; readable Markdown creation; persistence round trip; aggregate counts; separation of metadata and text; snapshot content/retention; rename/reorder; export order; exact section export; ZIP relocation; recursive ZIP prevention; external text/metadata conflict rejection; recovery copies; missing files; retained deleted text; UTF-16 import; invalid encoding rejection; unsupported formats; atomic writes; SQLite rebuild; archive persistence; and partial library corruption.

AI checks cover endpoint normalization; HTTPS enforcement; localhost and private-LAN rules; embedded-credential and final-endpoint rejection; OpenRouter and OpenAI thinking parameter mapping; mandatory-reasoning detection; Unicode-safe context truncation; keyed, constant, whole-word, and budgeted lore matching; SillyTavern chat-completion preset import; SillyTavern lorebook import; tagged-reasoning filtering across stream chunks; model discovery and capability metadata; bearer authentication; truthful non-browser identification; SSE response parsing; single-attempt 429 behavior; persisted cooldown state; and API-key redaction in provider errors.

Memory checks cover disabled-by-default behavior; per-story opt-in; section-aware retrieval of older passages; structured memory injection and request preview; portable JSON and Markdown storage; stale chapter-summary warnings; model proposal parsing; manual approval effects; external-edit conflict rejection; recoverable deletion; complete-story and current-chapter analysis controls; editable proposal review; automatic proposal opt-in; and synopsis generation from full-story context.

Theme and icon checks cover the closed and editable ComboBox foreground/background template, popup item text/selection states, explicit active and inactive selection resources, outline-label foreground inheritance from the selected TreeView item, theme-aware tab headers that do not pass bold weight into their content, normal-weight editor typography, explicit generated-output append/clear controls, and a valid multi-size Windows icon directory. Theme palette contrast pairs were checked for manuscript text, secondary text, primary buttons, and selection text. All tested pairs meet the WCAG AA 4.5:1 contrast ratio used as a practical desktop readability threshold.

The initial package restore identified an outdated transitive SQLite native package. The project now explicitly references SQLitePCLRaw.bundle_e_sqlite3 3.0.5, which resolves native SQLite 3.53.4. The subsequent release build and publish produced no dependency warnings.

## Not verified here

Version 0.3.2 includes the fix for the user-reported startup failure when a saved story card binds to `Story.Progress`. Other bindings were reviewed: read-only display labels bind to `TextBlock.Text`, which does not require writing back to the model. The native Windows regression program loads the actual progress-bar element from application XAML and verifies saved-story loading, live updates, and reopening. Run it on Windows with:

```powershell
dotnet run --project tests/DraftLedger.WpfTests/DraftLedger.WpfTests.csproj -c Release
```

The Windows packaging script also runs this regression program before publishing. This environment compiled it but cannot execute WPF. Windows DPAPI key save/load and live OpenRouter, OpenAI, LM Studio, and custom-provider interoperability require Windows/provider access and were not executed here.

There is no Windows graphical session in this environment. The WPF interface has **not** been launched or interactively tested. No claim is made about measured startup speed, memory use, huge manuscripts, Windows accessibility behavior, or full keyboard/UI correctness. PowerShell packaging scripts and the optional ARM64 target were not executed on Windows. The delivered x64 binary was published directly with the .NET CLI.

## Windows acceptance checks for the preview

1. Extract the release and launch as a normal Windows user with the network disconnected and no separately installed .NET runtime.
2. Create a story, chapter, and section; type and paste; switch sections; close and reopen; verify exact text and totals.
3. Import UTF-8 `.txt` and `.md`; exercise each destination; cancel replacement once; confirm replacement once.
4. Rename, duplicate, reorder, and remove a chapter/section; reopen and verify outline and export ordering.
5. Use Ctrl+Z/Ctrl+Y, find/replace, story search, and selection counts. Confirm undo cannot bring text across section boundaries.
6. Test Light/Dark/System, font size, available spell-check languages, keyboard focus, 125%/150% display scaling, and F11 focus mode.
7. Save a version, edit, preview and restore it. Modify the file externally and verify the conflict message and recovery copy.
8. Export text/Markdown and create a ZIP; extract in a new folder on another PC and open it.
9. Make the project folder unavailable or unwritable, edit, and verify that a failed save leaves the draft open and ordinary closing is canceled. Restore access and save.
10. Confirm archive/restore, default storage changes, and library reopening preserve access to existing projects.
11. Type `A *quiet thought* and **bold line**` in Editor, then open Preview. Confirm the asterisks disappear and the corresponding styles are applied. Return to Editor and confirm exact source text, caret, and undo history are retained.
12. While Preview is active, select another section, restore a snapshot, change theme/font, and toggle focus mode. Confirm current text and styling remain correct. Try typing/pasting into Preview and verify it cannot modify the manuscript.
13. Add an LM Studio connection at `http://localhost:1234/v1`, refresh models, generate with streaming on and off, stop one response, and verify no response is appended until **Append to section** is selected. Verify **Clear output** empties the preview without changing the manuscript.
14. Add a disposable remote-provider key, close and reopen the AI window, and confirm model refresh works. Change the endpoint and confirm the app requires a replacement/removal of the saved key. Remove the key afterward.
15. Preview a request with each context scope and an enabled lorebook. Confirm the authorization key is absent, only matching lore appears, and StoryThroughSection stops at the target section.
16. Import representative SillyTavern chat-completion presets and lorebooks. Review import warnings and compare prompt order, matching entries, sampling values, thinking, and streaming against the source JSON.
17. Trigger a provider 429 response and verify a second request is blocked until the displayed cooldown. Confirm no automatic retry occurs.
18. Switch through every theme and open Settings plus the AI window and connection/preset dialogs. Verify selected, hovered, focused, and ordinary tab and dropdown text is readable.
19. Select chapters and sections, then move focus to the editor, notes, Find box, and top toolbar. Confirm the selected outline row and its text remain visible in each theme.
20. Confirm the new logo appears on the executable in Explorer, the taskbar, the main window, AI writing, and owned dialogs. Windows may require unpinning an older shortcut before pinning the updated executable.
21. Compare Editor and Preview with ordinary prose, headings, italics, and bold spans. Confirm ordinary story text is normal weight with comfortable spacing and that only intentional Markdown emphasis is bold.
22. Enable memory for one story and leave it disabled for another. Confirm only the enabled story injects memory into request preview.
23. Analyze a current chapter, edit and approve selected proposals, reject another, and verify the story bible, threads, summary, and warnings update only after approval.
24. Run complete-story analysis on several chapters. Confirm DraftLedger reports the request count, creates chapter-sized proposals, marks revised summaries stale, and offers final synthesis only when chapter summaries are current.
25. Add an important fact from an early chapter, lock it, and generate in a later chapter. Confirm memory preview includes the fact and relevant older passages within the configured character budget.

Use a copy of test writing for the acceptance pass before entrusting the preview with an only copy of a manuscript.
