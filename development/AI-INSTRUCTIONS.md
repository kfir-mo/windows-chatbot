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
opens a menu (open / hide / exit); "הסתרה" there hides the corner button too. Users can:

- chat freely with an AI model ("צ׳אט כללי", general chat, no documents);
- create **assistants** ("עוזר אישי"): a name, optional document folders or single documents (by path) and optional
  custom instructions. An assistant with documents answers **only** from them and shows the source files of every
  answer. Each question can use "מהיר", "רגיל" (default) or "מעמיק" (see `AnswerMode` in `ChatEngine.cs`).

Everything runs on the user's PC. There is no server. The AI is any OpenAI-compatible HTTP endpoint (OpenAI,
Azure OpenAI, vLLM, Ollama, LM Studio, an internal gateway…) configured in `ai.config.json`.

Supported documents: DOCX, PPTX (including speaker notes), PDF with a text layer, XLSX (stored cell values),
TXT, Markdown, CSV, HTML and JSON (read as "key: value" lines). Not supported: legacy DOC/PPT/XLS (save them as DOCX/PPTX/XLSX) and scanned PDFs
(no OCR).

## 2. Folder layout

```
(repository root)
  Chaty.exe              the app (self-contained, no install, no .NET needed on the PC)
  ai.config.json         deployment configuration: models, rate limit, search limits (edit this, see §3)
  README.md              user/admin readme
  CLAUDE.md              short orientation for AI coding assistants
  development/           source code — only needed to change or rebuild the app
    AI-INSTRUCTIONS.md            this file
    scripts/publish-local.ps1     builds Chaty.exe into the root folder (use -Offline on a closed network)
    scripts/prepare-offline.ps1   bundles NuGet packages into development/packages (run once, with internet)
    packages/, nuget.offline.config   the offline package bundle (created by prepare-offline.ps1)
    src/Knowledge.Core/   all logic: AI client, configuration, indexing, search, answer pipeline
    src/Chat/             the WPF user interface (XAML + C#)
    src/Chat.Local/       the project that builds Chaty.exe from src/Chat (defines LOCAL_PROTOTYPE)
    tests/Knowledge.Tests unit tests (xUnit)
    tests/Chat.Smoke      UI smoke test (console app)
```

`src/Chat` has no project of its own: always build `src/Chat.Local/Chat.Local.csproj`. (The server-managed edition
lives on the `advance-edition` branch.)

Per-user data is **not** in this folder. It lives in `%LOCALAPPDATA%\WindowsChat.Local\`, encrypted with Windows
DPAPI for the current user: `local-settings.bin` (chosen model), `ai-key-*.bin` (API keys), `chat-types.bin`
(assistants), `conversations.bin` (history), `chat-looks.bin` (assistant colors/icons), `index7-*.bin` (document
index caches, safe to delete — they are rebuilt), `ai-request-budget.bin` (rate-limit state). Deleting the folder
resets Chaty for that user.

## 3. Configuration: `ai.config.json`

`ai.config.json` sits beside `Chaty.exe`. It contains **no secrets** — users type their key in the app
(⋯ menu → "הגדרות AI"), and it is stored encrypted per user. Publishing a new build never overwrites an
existing `ai.config.json`. Restart Chaty after editing. The file may contain `//` comments and trailing commas.
If the file is invalid, Chaty shows the error in its status line ("בדקו את ai.config.json: …").

### Full example

```jsonc
{
  "RequestsPerMinute": 18,          // shared AI-call budget per user, 1–600 (see "Rate limit")
  "SearchSubfolders": true,         // index sub-folders of assistant folders
  "Documents": {                    // optional; omitted values use these defaults
    "ChunkCharacters": 1800,        // 200–10000: size of indexed text passages
    "MaxFileMegabytes": 50,         // 1–500: larger files are skipped (listed as "not included")
    "MaxPassages": 8,               // 1–30: passages sent to the AI per answer
    "EvidenceCharacters": 4800,     // 500–30000: total document text sent to the AI per answer
    "ExcerptCharacters": 1000       // 200–4000: max length of one passage sent to the AI
  },
  "History": {                      // optional; omitted values use these defaults
    "RecentMessages": 8,            // 2–40: previous messages sent as conversation context
    "SummaryCharacters": 2000,      // 100–10000
    "MessageCharacters": 6000,      // 100–20000: longest question a user may send
    "ContextCharacters": 2000       // 500–12000: total conversation context sent to the AI
  },
  "Models": [                       // shown in the model picker; the first is the default
    {
      "Model": "gpt-4o-mini",                                   // exact model id sent to the endpoint (unique)
      "Endpoint": "https://api.openai.com/v1/chat/completions", // full URL including the API route
      "AllowedHosts": ["api.openai.com"],                       // must contain the endpoint's host name
      "Format": "chat-completions",                             // or "responses" (OpenAI Responses API)
      "Authentication": "bearer",                               // bearer | header | none | windows
      "HeaderName": "api-key",                                  // only for "header" authentication
      "AllowHttp": false,                                       // true allows plain http:// (internal servers)
      "UseProxy": false,                                        // true uses the Windows proxy settings
      "TimeoutSeconds": 60,                                     // 1–600 per AI call
      "MaxOutputTokens": 1600,                                  // 1–16000
      "MaxTokensField": "",                                     // "" auto | "max_tokens" | "max_completion_tokens" | "max_output_tokens" | "none"
      "SendStoreFalse": true,                                   // sends "store": false; set false if the server rejects it
      "Temperature": null,                                      // optional 0–2; omitted when null
      "SystemPrompt": "",                                       // optional text prepended to every AI instruction
      "InputTokensPath": "usage.prompt_tokens",                 // where token usage is in the response (optional)
      "OutputTokensPath": "usage.completion_tokens"
    }
  ]
}
```

### How to add a model

Add an object to `Models` (model names must be unique), restart Chaty, open "הגדרות AI", pick the model, enter
the key if it needs one ("החלפת מפתח"), press "בדיקת חיבור" (connection test), then "שמירה". Keys are remembered per
endpoint address + authentication type, so models on the same server share one key.

Recipes:

| Server | Settings |
|---|---|
| OpenAI | `Endpoint` `https://api.openai.com/v1/chat/completions`, `Authentication` `bearer` |
| Azure OpenAI | `Endpoint` `https://<name>.openai.azure.com/openai/deployments/<deployment>/chat/completions?api-version=2024-10-21`, `AllowedHosts` `["<name>.openai.azure.com"]`, `Authentication` `header`, `HeaderName` `api-key`, `Model` = deployment name |
| vLLM / LM Studio / internal OpenAI-compatible gateway | `Endpoint` `http://ai-server:8000/v1/chat/completions`, `AllowHttp` `true` (only if it has no HTTPS), `Authentication` `none` (or `bearer` if it wants a token), `MaxTokensField` `max_tokens` for older servers |
| Ollama | `Endpoint` `http://ollama-host:11434/v1/chat/completions`, `AllowHttp` `true`, `Authentication` `none`, `MaxTokensField` `max_tokens`, `SendStoreFalse` `false` |
| Gateway with Windows login (Kerberos/NTLM) | `Authentication` `windows` — the user's Windows account is sent, no key field is shown |

With `Authentication` `none` or `windows` the key section is hidden in Settings.

HTTPS certificates are validated against the **Windows certificate store**. For an internal certificate
authority, install its root certificate in Windows (by IT), don't disable validation. Redirects are blocked: the
`Endpoint` must be the final URL.

The answer pipeline needs a model that follows JSON-format instructions reasonably well (it returns
`{"answer": …, "citations": […]}`). Small local models may answer "not enough information" more often; prefer
instruction-tuned models of roughly 7B parameters or more.

### Rate limit

`RequestsPerMinute` is a budget shared by all models for one Windows user, persisted across restarts. It counts
every AI call (calls are not spaced out — Chaty waits only when the last minute's budget is full): one question in a
document assistant normally uses **3 calls** (route the request, draft the
answer, review the answer); general chat uses 1; the connection test uses 1. When the budget is used up Chaty
waits and shows "ממתין למכסת ה־API". An HTTP 429 from the server pauses for at least 61 seconds (or the server's
`Retry-After`). Keep it below the provider's real limit. For an internal server without limits use e.g. 120–600.

## 4. How it works (code map)

**Answer pipeline** — `src/Knowledge.Core/ChatEngine.cs` (`AskAsync`):
1. `RequestPlanner` (`RequestPlanner.cs`): greetings / "list documents" / "the first one" are handled locally
   without AI; otherwise the AI routes the question to `reply | search | list | read` (JSON).
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

**HTTP** — `AiClient.cs` (request body, auth, error messages; never shows response bodies), `AiRequestGate.cs`
(rate limit), `AiCredentials.cs` (encrypted keys), `LocalAiCatalog.cs` (reads `ai.config.json`),
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
  example questions), messages, composer (מהיר · רגיל · מעמיק switch for assistants with documents: `CurrentMode`,
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
   (they can echo credentials); never write keys into `ai.config.json`.
4. **`AllowedHosts` allow-list and no redirects** stay enforced (`Config.Validate`, `AiClient`).
5. **Tests never touch a real profile**: `tests/Knowledge.Tests/TestProfile.cs` moves `Storage.Root` to a temp
   folder; do the same in any new test or tool.
6. **Low memory**: Chaty renders in software mode and releases idle memory (`MemoryTrim`); the search index
   uses compact arrays. Measure (Task Manager → Details → "Memory (private working set)") before adding heavy
   libraries or always-on timers. Typical: ~25–30 MB while used, ~5–10 MB idle.
7. After any change: build, run both test suites, run `scripts/publish-local.ps1`, start `Chaty.exe` and try it.

## 7. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| "בדקו את ai.config.json: …" | Invalid JSON or a value outside its range (see §3). The message names the model. |
| "כתובת AI אינה מורשית בהגדרות" | Endpoint host missing from `AllowedHosts`, or `http://` without `AllowHttp: true`. |
| "השרת דחה את מבנה הבקשה" (HTTP 400) | Wrong `Format`, model name, or the server rejects a field: try `MaxTokensField: "max_tokens"` and `SendStoreFalse: false`. |
| HTTP 401/403 | Wrong key, or wrong `Authentication`/`HeaderName`. |
| "לא ניתן להתחבר לשירות ה-AI" | Network/DNS/firewall, or an HTTPS certificate Windows doesn't trust. |
| Waiting for the API budget too often | Raise `RequestsPerMinute` (if the server allows it). |
| A document isn't used | Hover the header status line: skipped files and reasons are listed. Scanned PDFs and DOC/PPT/XLS are not supported. |
| Answers miss information | Raise `Documents.MaxPassages` / `EvidenceCharacters` (more text per answer = more tokens), or add the right folder to the assistant. |
