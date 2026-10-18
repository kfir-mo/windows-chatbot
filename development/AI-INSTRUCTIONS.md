# Chaty — instructions for AI assistants (and people) maintaining this app

Read this whole file before changing anything. It describes what Chaty is, how to configure it (models, keys,
limits), how the code is organized, how to build and test it — also on a closed network without internet — and
the rules that must not be broken.

## 1. What Chaty is

Chaty is a Hebrew-first (right-to-left) Windows desktop chat assistant. A round button sits in the bottom-right
corner of the screen; clicking it opens a 400×680 chat panel, clicking it again closes it. The panel can be resized
from its top and left edges; a double-click on an empty part of the header or the chat restores 400×680. Every start
opens a new, empty general chat. Chaty has no taskbar
button: it lives in the notification area (tray, next to the clock) — left click toggles the panel, right click
opens a menu (open / hide / exit); "הסתרה" there hides the corner button too. Ctrl+Alt+Space opens and closes it from
any program (registered with Windows; if another program already took the key, only the button and tray work). Users can:

- chat freely with an AI model ("צ׳אט כללי", general chat, no documents);
- create **assistants** ("עוזר אישי"): a name, optional document folders or single documents (by path) and optional
  custom instructions. An assistant with documents answers **only** from them and shows the source files of every
  answer. Each question can use "מהיר", "רגיל" (default) or "מעמיק" (see `AnswerMode` in `ChatEngine.cs`), or, for a
  document that is not found, the separate "חיפוש מקסימלי" button (one answer, then back to "רגיל").

Everything runs on the user's PC. There is no server. The AI is any OpenAI-compatible HTTP endpoint (OpenAI,
Azure OpenAI, vLLM, LiteLLM, an internal gateway…) configured in `%APPDATA%\config.json` on each PC.

Supported documents: DOCX, PPTX (including speaker notes), PDF with a text layer, XLSX (stored cell values),
TXT, Markdown, CSV, HTML and JSON (read as "key: value" lines). Not supported: legacy DOC/PPT/XLS (save them as DOCX/PPTX/XLSX) and scanned PDFs
(no OCR).

## 2. Folder layout

```
(repository root)
  Chaty.exe              the app (self-contained, no install, no .NET needed on the PC)
  README.md              short user/admin readme
  development/           source code — only needed to change or rebuild the app
    AI-INSTRUCTIONS.md     this file
    scripts/publish-local.ps1     builds Chaty.exe into the root folder (use -Offline on a closed network)
    scripts/prepare-offline.ps1   bundles NuGet packages into development/packages (run once, with internet)
    packages/, nuget.offline.config   the offline package bundle (created by prepare-offline.ps1)
    src/Knowledge.Core/   all logic: AI client, configuration, indexing, search, answer pipeline
    src/Chat/             the WPF user interface (XAML + C#)
    src/Chat.Local/       the project that builds Chaty.exe from src/Chat
    tests/Knowledge.Tests unit tests (xUnit)
    tests/Chat.Smoke      UI smoke test (console app)
```

`src/Chat` has no project of its own: always build `src/Chat.Local/Chat.Local.csproj`. (The server-managed edition
lives on the `advance-edition` branch.)

Per-user data is **not** in this folder. It lives in `%LOCALAPPDATA%\WindowsChat.Local\`, encrypted with Windows
DPAPI for the current user: `local-settings.bin` (chosen model), `ai-key-*.bin` (API keys), `chat-types.bin`
(assistants), `conversations.bin` (history), `chat-looks.bin` (assistant colors/icons), `index7-*.bin` (document
index caches, safe to delete — they are rebuilt; caches no assistant uses are deleted at startup),
`document-counts.bin` (last document count per assistant, for the switcher).
Deleting the folder
resets Chaty for that user.

## 3. Configuration: `%APPDATA%\config.json`

Only `Chaty.exe` is handed out. The AI configuration is one file per PC (per Windows user):
`%APPDATA%\config.json` (`C:\Users\<user>\AppData\Roaming\config.json`), the closed network's endpoint JSON
(`AiConfigFile.cs`). It is the **only** source of models: without it (or when it is invalid) the model picker in
"הגדרות AI" is empty, the reason and the expected path are shown under it, and messages can't be sent. The file is
read at startup and every time "הגדרות AI" opens, so an edit applies without a restart.

```jsonc
[
  {
    "name": "GAIA", "vendor": "customendpoint",
    "apiKey": "",                      // usually empty: the user types the key in "הגדרות AI" (DPAPI-encrypted)
    "apiType": "chat-completions",     // or "responses"
    "models": [
      { "id": "GAIA-chat",             // sent as "model"; id = name in this network
        "name": "GAIA-chat",
        "url": "https://…/v1/chat/completions",   // full endpoint, or the base (…/v1): /chat/completions is added
        "toolCalling": true, "vision": true,
        "thinking": true,              // reasoning model; missing = false (e.g. the code model)
        "maxInputTokens": 128000,      // sizes how much of an attached document is sent
        "maxOutputTokens": 16000 }     // the output limit; reasoning counts toward it
    ]
  }
]
```

Each model gets `AllowedHosts` = its url's host, `AllowHttp` only for an `http://` url, `bearer` authentication,
`max_tokens` (`max_completion_tokens` for api.openai.com, which rejects `max_tokens` for reasoning models), no
`store` field, and a 120 s timeout (300 s for `thinking` models). A non-empty `apiKey` is used as is and never copied
anywhere; keys typed in the app are remembered per server address, so models on one server share a key. Search and
memory limits are the built-in defaults (`DocumentLimits`, `HistoryOptions`).

Reasoning models: `reasoning_content` and a leading `<think>…</think>` are never shown as the answer. If the thinking
uses all of `max_tokens` the server returns `content: null` with `finish_reason: "length"`, and Chaty says so.

HTTPS certificates are validated against the **Windows certificate store**. For an internal certificate
authority, install its root certificate in Windows (by IT), don't disable validation. Redirects are blocked: the
url must be the final address.

### Streaming answers

The final answer is shown while it is written: the request carries `"stream": true` and the server answers with
server-sent events (`text/event-stream`, OpenAI chat-completions deltas or Responses `response.output_text.delta`).
Only the call that writes the final answer streams (general chat, fast mode, and the reviewer call of the other modes; a
draft that will still be reviewed is never shown). A server that ignores `stream` and answers with plain JSON works as
before. A gateway that rejects it (HTTP 400/404/405/415/422/501) is asked again without it, and Chaty stops streaming to
that address until it restarts. A proxy that buffers the response makes the answer appear at once at the end: nothing
breaks, only the live text is lost.

### Rate limit

Chaty has **no request budget of its own**: calls go out until the server answers HTTP 429. Then
`RateLimitException` reads the limit and the reset time from the server's message (LiteLLM style: "… Current limit: 15
… Limit resets at: 2026-10-07 11:32:10 UTC"; or `Retry-After`) and the chat shows "הגעתם למגבלת הבקשות … אפשר לשלוח
שוב בשעה …". The message itself is never shown, because it names the API key. A 429 on a review call keeps the
unreviewed draft, so a question near the limit still gets an answer. One question in a document assistant normally
uses **3 calls** (route, draft, review); "מהיר" uses 1 (plus a planner call and a second search only when the
user's own words find nothing to answer from); general chat uses 1.

### Attaching a document (general chat)

The paperclip in the general chat's composer (or dropping a file on it) attaches one document: its text is extracted
like an indexed file (`Attachments.cs`), shown as a chip on the message, and kept with the conversation
(`ChatMessage.File`, DPAPI-encrypted) so follow-up questions still have it. Every general-chat request sends the
conversation's attachments as `attachedDocuments`, newest first, cut to `(maxInputTokens − maxOutputTokens − 3000) × 2`
characters (24,000 when `maxInputTokens` is unknown). Assistants with folders have no attach button.

## 4. How it works (code map)

**Answer pipeline** — `src/Knowledge.Core/ChatEngine.cs` (`AskAsync`):
1. `RequestPlanner` (`RequestPlanner.cs`): greetings / "list documents" / "the first one" are handled locally
   without AI; otherwise the AI routes the question to `reply | search | list | read` (JSON). An assistant answers
   only from its documents: `reply` is for greetings and unclear requests, general-knowledge questions are searched,
   unless the assistant's instructions explicitly allow answering beyond the documents.
2. Search: `DocumentIndex.SearchAsync` → `PassageCatalog.Rank` (BM25-style keyword ranking with Hebrew prefix
   handling, `SearchText.Tokens`). If nothing matches strictly, a relaxed pass is tried.
3. `RetrievalText.Budget` trims evidence to the `Documents` limits.
4. The AI drafts `{answer, citations}`, a second call reviews it. Citations map to source files shown under the
   answer. No valid citation → "not enough information". AI failure → raw excerpts, clearly labelled.
5. Access is re-checked (`Access.CanRead`) before excerpts are sent and before sources are shown.
   `ChatEngine.AnswerLanguage` tells the AI which language to answer in (from the question's script).

**AI prompts** (English, answers in the user's language): `ChatEngine.cs` (general chat prompt, answer +
review prompt), `RequestPlanner.cs` (routing), `Configuration.cs` (`AnswerPrompt`). Per-deployment additions go
in a model's `SystemPrompt`; per-assistant ones in the assistant's custom instructions — prefer those over code.

**HTTP** — `AiClient.cs` (request body, auth, error messages, reasoning-model answers; never shows response bodies),
`AiCredentials.cs` (encrypted keys, `RateLimitException`), `AiConfigFile.cs` (reads `%APPDATA%\config.json`),
`LocalAiCatalog.cs` (the models and the built-in limits),
`Configuration.cs` (`Config`, all limits and their validation ranges).

**Indexing** — `DocumentIndex.cs`: walks the assistant's folders (a source that is a single file is indexed alone) (skips hidden/system files and Office lock files
`~$*`), extracts text in parallel (`Extract`: DOCX/PPTX via XML, PDF via PdfPig, XLSX via `SpreadsheetText.cs`,
text/HTML via `PlainText` with UTF-8/UTF-16/Windows-1255 detection), splits it into passages (`TextChunks.cs`),
and caches the result compressed + encrypted (`index7-*.bin`). Unchanged files are reused, so refreshes are fast.
Loaded indexes stay in memory for the session (`SessionIndex` in `MainWindow.ChatTypes.cs`; the other assistants
are preloaded in the background after startup), so switching assistants or starting a new chat does not reload;
the folders are rescanned at most every 10 minutes then, and always from the ⋯ menu ("רענון המסמכים").
Search adds Hebrew singular variants (`HebrewSuffixVariants`: שומרי→שומר, הוראות→הוראה) and ranks passages that
are mostly file names/paths (a FOLDER_MAP.csv, file lists) much lower (`RetrievalText.LooksLikeFileList`). Files skipped during
indexing are listed when hovering the status line (subtitle) in the header. To add a file type: add the extension to
`DocumentIndex.Extensions`, handle it in `Extract`, add a badge in `FileBadge.For` (`src/Chat/MessageView.cs`).
Changing extraction output for existing types: bump `v7` in the `DocumentIndex` constructor (cache identity) so
old caches are rebuilt.

**User interface** (`src/Chat/`, WPF). The header is solid in the active assistant's color (`AssistantBrush`
resource, set in `ApplyTypeLook`); popovers open over the dimmed chat; history replaces the chat under the header; AI settings and the assistant
form are full-panel screens:
- `MainWindow.xaml` / `.xaml.cs` — the panel, header, landing pages (general chat; assistant with folder tags and
  example questions), messages, composer (מהיר · רגיל · מעמיק switch for assistants with documents, and the separate
  "חיפוש מקסימלי" toggle with its orange cost note: `SetMaxSearch`; `CurrentMode`,
  `AnswerConfig` halves/doubles `MaxPassages`/`EvidenceCharacters`, `ChatEngine.Mode` changes the steps), resize grips,
  launcher button, status line, idle memory trimming hooks, assistant looks (`LookOf`, saved in `chat-looks.bin`).
- `MainWindow.Design.cs` — the popovers: assistant switcher (the only place with "edit assistant"), ⋯ menu
  (`BuildOptionsMenu`), icon & color popover (live preview, "סיום" keeps it), About.
- `MainWindow.ChatTypes.cs` — switching, create/edit/delete (delete removes the assistant's conversations unless the
  user unticks it; kept ones are marked `KeptAfterDelete` and open read-only; others of missing assistants are dropped at startup).
- `MainWindow.Local.cs` — startup and the AI settings screen host.
- `TrayIcon.cs` — the notification-area icon (native Shell_NotifyIcon, no WinForms).
- `LocalSettingsPanel.cs` — "הגדרות AI" (model + key card with connection test).
- `ChatTypesPage.xaml(.cs)` — new/edit assistant form (look picker folded until the avatar is pressed, unlimited
  folders or documents with a real document count each, delete with confirmation). `HistoryPage.xaml(.cs)` — conversation history grouped by day.
- `Looks.cs` — the 6 assistant colors, the 18 symbols and the `LookPicker` control. `Icon.cs` — the line icons
  (SVG path data drawn as WPF paths; add new ones to `Icon.Data`).
- `MessageView.cs` — message bubbles (copy button on hover), Markdown rendering (`AnswerFormat.SplitInlineLists` puts
  "1. … 2. …" runs on separate lines), file-type badges, text direction detection.
- `App.xaml` — all shared styles (font "Heebo, Segoe UI", buttons, pills, rows, segmented control, tooltips,
  scrollbars, model picker). `Theme.cs` — color tokens. `Ui.cs` — attached properties (corner radius, hover
  color). `MemoryTrim.cs` — idle memory release.
- `chaty.ico` — the app icon (exe, taskbar).

All user-facing text is Hebrew and lives directly in these files (search for the Hebrew string to find it).

## 5. Building, testing, publishing

Requirements: Windows, **.NET 8 SDK** (x64). Commands run from `development` in PowerShell.

```powershell
dotnet build src/Chat.Local/Chat.Local.csproj      # compile
dotnet test tests/Knowledge.Tests                    # unit tests — must all pass
dotnet run --project tests/Chat.Smoke                # UI smoke test — every line must say PASS
scripts/publish-local.ps1                            # builds ../Chaty.exe (closes a running Chaty first)
```

If `dotnet` is not on PATH, use `& "$env:USERPROFILE\.dotnet\dotnet.exe"`.

### Building without internet (closed network)

1. On a PC **with** internet, run `scripts/prepare-offline.ps1` once (already done if `development/packages`
   exists). It copies all needed NuGet packages (~110 MB) into `development/packages` and writes
   `nuget.offline.config`.
2. On the closed network install the .NET 8 SDK (x64) — the same 8.0 feature band or newer.
3. Publish with `scripts/publish-local.ps1 -Offline`. Before building or testing, restore from the bundle:
   `dotnet restore tests/Knowledge.Tests --configfile nuget.offline.config`, then
   `dotnet test tests/Knowledge.Tests --no-restore` (same for `tests/Chat.Smoke`). If the SDK there is a newer 8.0.x patch, the runtime-pack version
   may differ; then either copy the matching `Microsoft.*.Runtime.win-x64` packages in, or publish
   framework-dependent: `scripts/publish-local.ps1 -Offline -FrameworkDependent` (needs the .NET 8 Desktop
   Runtime on the PCs).

## 6. Rules (do not break these)

1. **Hebrew, right-to-left UI.** Keep `FlowDirection` handling; new text in Hebrew; test that mixed Hebrew/English
   text still renders correctly. In right-to-left containers `HorizontalAlignment.Left` is the visual right.
2. **Answers only from evidence** for document assistants; keep citations, the reviewer call, and the access
   re-checks (`Access.CanRead`, `RedactInaccessible`) in `ChatEngine`.
3. **No secrets in files or logs.** Keys stay DPAPI-encrypted in the user profile; never print response bodies
   (they can echo credentials); tests point `AiConfigFile.Path` and `Storage.Root` to temp folders.
4. **`AllowedHosts` allow-list and no redirects** stay enforced (`Config.Validate`, `AiClient`).
5. **Tests never touch a real profile**: `tests/Knowledge.Tests/TestProfile.cs` moves `Storage.Root` to a temp
   folder; do the same in any new test or tool.
6. **Low memory**: Chaty renders in software mode and releases idle memory (`MemoryTrim`); the search index
   uses compact arrays. Measure (Task Manager → Details → "Memory (private working set)") before adding heavy
   libraries or always-on timers. Only the active assistant's index stays in memory. Measured: ~10–12 MB working
   set while hidden, 0% CPU when idle.
7. After any change: build, run both test suites, run `scripts/publish-local.ps1`, start `Chaty.exe` and try it.

## 7. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Empty model picker, red text under it | `%APPDATA%\config.json` is missing or invalid; the text says which (with the JSON line number) and shows the path. |
| "כתובת AI אינה מורשית בהגדרות" | Endpoint host missing from `AllowedHosts`, or `http://` without `AllowHttp: true`. |
| "השרת דחה את מבנה הבקשה" (HTTP 400) | Wrong model id or apiType, or the server rejects a field. |
| HTTP 401/403 | Wrong key ("הגדרות AI" → "החלפת מפתח"). |
| "לא ניתן להתחבר לשירות ה-AI" | Network/DNS/firewall, or an HTTPS certificate Windows doesn't trust. |
| "הגעתם למגבלת הבקשות של שרת ה־AI" | The server's own 429. Wait for the time shown, or ask for a higher limit on the server. |
| "המודל הגיע למגבלת אורך התשובה" | A reasoning model spent `maxOutputTokens` on thinking. Raise `maxOutputTokens` in the JSON, or use a model without thinking. |
| A document isn't used | Hover the header status line: skipped files and reasons are listed. Scanned PDFs and DOC/PPT/XLS are not supported. |
| Answers miss information | Try "רגיל" or "מעמיק" (the AI adds the words documents use), then "חיפוש מקסימלי" (rewordings, other language, fixed spelling), or add the right folder to the assistant. |
