# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Hebrew-first (RTL) Windows desktop chatbot that answers employee questions from organizational documents (DOCX, PPTX, text PDFs, XLSX, TXT, Markdown) using an OpenAI-compatible AI endpoint. .NET 8 (`net8.0-windows`), WPF desktop client, ASP.NET Core server.

## Two independent editions — duplicated source

```
advance/            server-managed edition (desktop client + central AI server with AD auth)
  client/           published Chat.exe + .env (GATEWAY_ENDPOINT) + config.json  — the only thing given to users
  server/           published server install: app/, .env, settings.json, config.json, data/audit.db, logs/  (live data, not a template)
  development/      source: WindowsChat.sln, src/, server/, tests/, tools/, scripts/
local/              standalone personal edition "Chaty" (no server, no AD) — see local/AI-INSTRUCTIONS.md
  Chaty.exe, ai.config.json, AI-INSTRUCTIONS.md (full guide for AI maintainers)
  development/      its OWN copy of src/Chat, src/Chat.Local, src/Knowledge.Core, tests/, plus packages/ (offline NuGet bundle)
```

Each edition is meant to be copied/moved independently, so `local/development/src` started as a **full copy** of `advance/development/src`. Both have since diverged: the local `Chat` UI was redesigned (`App.xaml` styles, `MessageView.cs`, `Ui.cs`, `MemoryTrim.cs`, software rendering, no WinForms tray), and the local `Knowledge.Core` has a compact index (`PassageCatalog` array postings, `index7-*.bin` compressed cache, parallel extraction, CSV/HTML, PPTX notes, encoding detection, relaxed search fallback), explicit answer language, and per-model `Authentication`/`MaxTokensField`/`SendStoreFalse`/`Temperature` plus `Documents`/`History` limits in `ai.config.json` (RequestsPerMinute up to 600). A fix to shared logic must be applied to both copies unless it's intentionally edition-specific. Neither edition may reference the other's folder.

`advance/client`, `advance/server`, and `local/*.exe|*.json` are publish outputs plus live configuration/data — don't hand-edit binaries, and treat `server/.env`, `settings.json`, `data/`, `logs/` as real installation state.

## Git, CI and cloud sessions

The project is on GitHub. Build outputs (`*.exe`, `advance/server/app/`), the offline NuGet bundle
(`local/development/packages/`) and live installation state (`.env`, `advance/server/settings.json`, `data/`, `logs/`)
are git-ignored — never commit them. `.github/workflows/tests.yml` runs both editions' tests on `windows-latest`.

In a Linux environment (e.g. a Claude Code cloud session) `dotnet build` works because of `EnableWindowsTargeting`, but
the WPF apps can't run and tests that touch `Storage` (DPAPI) fail with `PlatformNotSupportedException`. Pure logic
tests (retrieval, document formats, planner) can be run with `--filter`; rely on the Windows CI for the full suite.

## Commands

`dotnet` is not on PATH in this environment; the scripts use `%USERPROFILE%\.dotnet\dotnet.exe` (falling back to `dotnet` on PATH).

```powershell
# from advance/development
& "$env:USERPROFILE\.dotnet\dotnet.exe" test WindowsChat.sln
& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/Knowledge.Tests --filter "FullyQualifiedName~RetrievalTests"   # single class / test
scripts/publish-all.ps1                       # publish server -> ../server/app, client -> ../client (preserves existing .env/config/settings)
scripts/publish-internal.ps1                  # credential-free package for internal (domain) deployment
../server/start-server.ps1                    # run the managed server; admin UI at http://127.0.0.1:5187/

# from local/development
scripts/publish-local.ps1 [-FrameworkDependent] [-Offline]   # publishes ../Chaty.exe (assembly name Chaty; project still Chat.Local)
& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/Knowledge.Tests
```

`advance/development/scripts/publish-local.ps1` deliberately throws — use the local edition's script.

Tests are xUnit. `Knowledge.Tests` covers the core (retrieval, answer flow, document formats, chat types, local AI catalog); `Server.Tests` uses `Microsoft.AspNetCore.Mvc.Testing` against `server/Program.cs` (security, audit). `tests/Chat.Smoke` and `tools/*` (UiSmoke, LocalSmoke, AiCheck, GuideCheck, SearchReview, IndexBenchmark, SampleCheck) are console harnesses run with `dotnet run --project`, not part of `dotnet test`. Sample Hebrew documents and expected questions are in `samples/test-documents/` (`TEST-QUESTIONS.md`); prior validation reports are in `VALIDATION.md`, `SEARCH-VALIDATION.md`, `validation/`.

## Architecture

**Projects** (`advance/development`):
- `src/Knowledge.Core` — all chat/retrieval logic, shared by client, local build and server.
- `src/Chat` — WPF client (`Chat.exe`). `MainWindow` is split into partial files by mode: `MainWindow.Gateway.cs` (managed edition), `MainWindow.Local.cs` (local edition), `MainWindow.ChatTypes.cs`, `MainWindow.Design.cs`.
- `src/Chat.Local` — no sources of its own; compiles `../Chat/*.cs` + XAML with `LOCAL_PROTOTYPE` defined. `BuildMode.Local` exposes that flag at runtime. Edition differences inside the client go through `#if LOCAL_PROTOTYPE` / `BuildMode.Local`.
- `server/` — ASP.NET Core minimal API (`WindowsChat.Server`), `wwwroot/` is the admin SPA (strict CSP: no inline scripts/styles).

**Answer pipeline** (`ChatEngine.AskAsync`):
1. `RequestPlanner.LocalPlan` handles greetings/"list documents"/ordinal source picks without AI; otherwise the AI routes the turn to `reply | search | list | read` (JSON), with a second review call when it chose `reply`. If planning fails, `OfflineFallback` uses keyword heuristics. A strong local index match overrides a generic `reply`.
2. Retrieval via `IDocumentSearch` (`DocumentIndex` → `PassageCatalog`/`TextChunks`/`SearchText`), then `RetrievalText.Budget` trims evidence to config limits.
3. Answer call returns JSON `{answer, citations[]}`, followed by a reviewer call; `Attribute` maps citation IDs to `Source`s. Invalid/empty citations → "not enough information" response; AI failure → raw search excerpts clearly labeled as unverified.
4. Access is re-checked (`Access.CanRead` / `search.CanRead`) before sending excerpts and before attributing; `RedactInaccessible` scrubs history messages whose sources became unreadable. Preserve these checks when changing the flow.

User-facing strings are Hebrew; AI system prompts are English and insist on answering in the user's language while keeping identifiers/UI labels verbatim.

**Managed edition flow:** `Chat.exe` → `GatewayClient` (Windows integrated auth, `X-WindowsChat: 1` header, HTTPS or loopback HTTP only) → server `/api/session`, `/api/sources/refresh`, `/api/chat`.
- Chat type IDs: `builtin:general`, `org:<id>` (server-managed assistants; server indexes their paths itself, impersonating the caller's Windows identity in production), or a GUID for personal assistants (client indexes locally and submits evidence in `TurnRequest`; server wraps it in `SubmittedEvidence`).
- Server middleware enforces host match, AD group membership (`AdminGroups` for `/`, `/api/admin/*`; `AiGroups` otherwise), JSON content type, origin, antiforgery for admin writes, plus per-user and global concurrency gates. `LocalDevelopment` mode (loopback HTTP only) fakes membership in all configured groups — it does not validate real AD.
- Every turn is audited to SQLite (`AuditStore`, `server/data/audit.db`) with token counts read via configurable JSON paths (`UsageReader`); `ExcelWorker` writes per-chat-type Excel workbooks under per-project log folders.

**Local edition flow:** `LocalAiCatalog` loads `ai.config.json` (model list: endpoint, allowed hosts, format, limits); the user picks a model and enters their key in-app (hidden for `Authentication` none/windows). `AiRequestGate` enforces a persisted, shared rolling budget (`RequestsPerMinute`, default 18, max 600) across all models (counts planning, review, failures, connection tests; 429 → ≥61 s cooldown).

**Configuration:** `Config.Load` reads `config.json` beside the exe, then overlays `.env` (`DotEnv`), then `Validate()` enforces strict ranges and an endpoint host allow-list. If `Gateway.Endpoint` is set, only the gateway URL is validated. Server-only settings (groups, assistants, URL, certificate, timezone) live in `server/settings.json` (`SettingsStore`); provider credentials in `server/.env`. AI `Format` is `chat-completions` or `responses`.

**Per-user state:** `Storage` writes DPAPI-protected (CurrentUser) files under `%LOCALAPPDATA%\WindowsChat` (managed) or `%LOCALAPPDATA%\WindowsChat.Local` (local): conversations, chat types, settings, keys, indexes.

## Conventions

- Code style is dense: short lines collapsed onto one, primary constructors, records, collection expressions. Match the surrounding file rather than reformatting.
- WPF, not web: theming uses resource brushes (`Theme.cs`), RTL via `FlowDirection`, accessibility via UI Automation names. `DESIGN-REVIEW.md` records deliberate UI decisions (e.g. the plus button means *new conversation*, not new assistant; fixed white/blue palette regardless of Windows theme).
- Published builds are self-contained single-file win-x64; `ExcludeDevelopmentSettings=true` keeps dev `config.json`/`.env` out of published output. WPF is intentionally not trimmed.
