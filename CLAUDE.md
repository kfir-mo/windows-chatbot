# CLAUDE.md

Chaty is a Hebrew-first (RTL) Windows desktop chatbot (.NET 8, WPF) that answers questions from the user's own
documents (DOCX, PPTX, text PDFs, XLSX, TXT, Markdown, CSV, HTML, JSON) with an OpenAI-compatible AI endpoint. No server,
no install: one self-contained `Chaty.exe`.

**Before changing anything, read [development/AI-INSTRUCTIONS.md](development/AI-INSTRUCTIONS.md)**: the configuration (`%APPDATA%\config.json`:
the models; keys are typed in the app), the code map, building and testing (also offline), and the rules that must not be broken.

## Layout

```
Chaty.exe            published app (git-ignored; rebuilt by development/scripts/publish-local.ps1)
development/
  AI-INSTRUCTIONS.md   full guide for AI maintainers
  src/Knowledge.Core   all logic: AI client, configuration, indexing, search, answer pipeline
  src/Chat             the WPF UI sources (XAML + C#), compiled by src/Chat.Local
  src/Chat.Local       builds Chaty.exe from ../Chat/*.cs
  tests/Knowledge.Tests  xUnit · tests/Chat.Smoke  UI smoke test (console, every line must say PASS)
  scripts/             publish-local.ps1, prepare-offline.ps1 (offline NuGet bundle in packages/, git-ignored)
```

The server-managed edition (central AI server with AD auth) was removed in 1.3.0; it is in the git history up to commit
`1f86304`. The profile folder (`WindowsChat.Local`) and the single-instance name keep their old names so users keep their data.

## Commands

`dotnet` may not be on PATH; use `& "$env:USERPROFILE\.dotnet\dotnet.exe"`. From `development/`:

```powershell
dotnet build src/Chat.Local/Chat.Local.csproj
dotnet test tests/Knowledge.Tests
dotnet test tests/Knowledge.Tests --filter "FullyQualifiedName~RetrievalTests"   # one class
dotnet run --project tests/Chat.Smoke
scripts/publish-local.ps1 [-FrameworkDependent] [-Offline]                        # publishes ../Chaty.exe
```

CI (`.github/workflows/tests.yml`) runs the build, the unit tests and the smoke test on `windows-latest`. In a Linux
environment the projects build (`EnableWindowsTargeting`), but the WPF app can't run and tests touching `Storage`
(DPAPI) fail; run pure logic tests with `--filter` and rely on CI for the rest.

## Answer pipeline (`ChatEngine.AskAsync`)

1. `RequestPlanner.LocalPlan` handles greetings, "list documents" and ordinal source picks without AI; otherwise the
   AI routes the turn (`reply | search | list | read`), with a review call when it chose `reply`.
2. Retrieval through `IDocumentSearch` (`DocumentIndex` → `PassageCatalog`), trimmed by `RetrievalText.Budget`.
3. Answer call returns JSON `{answer, citations[]}`, then a reviewer call; `Attribute` maps citations to sources.
4. `AnswerMode`: **Normal** as above; **Fast** skips planning and review (one AI call, short answer, half the
   evidence); **Deep** doubles the evidence, also reads the best-matching document, and asks for a complete answer
   with a completeness review.
   **Maximum** ("חיפוש מקסימלי", a separate toggle for assistants with folders, one answer then back to normal): no
   planner; the AI rewrites the request up to 8 ways (fixed spelling, Hebrew/English, exact error text, likely titles),
   each is searched, the AI keeps the relevant passages from a pool of 40, then a deep answer and review
   (`ChatEngine.MaximumSearchAsync`, 4 AI calls).
   Answers run per conversation (`MainWindow.running`): another chat can be opened and asked meanwhile; an answer that
   arrives unseen counts on the corner and history buttons and marks its history row (`StateOf`: running / ready).
   General chat has the same three modes (one call; short or thorough answer). Fast and Deep may use another model on the
   same server: one without / with `thinking` (`LocalAiCatalog.ModelFor`).
   The final answer streams (`ChatEngine.Partial`, `AiClient.StreamAsync`, SSE with a plain-JSON fallback): general chat's
   one call, fast mode's answer, and in the other modes only the reviewer's call; the draft it reviews is never shown.
5. Access is re-checked (`Access.CanRead` / `search.CanRead`) before sending excerpts and before attributing;
   `RedactInaccessible` and `ChatType.RedactOutsideFolders` scrub history. Keep these checks when changing the flow.

Assistant sources can be folders or single documents. User-facing strings are Hebrew; AI prompts are English and
insist on answering in the user's language.

## Per-user state

`Storage` writes DPAPI-protected files under `%LOCALAPPDATA%\WindowsChat.Local`: conversations, assistants, looks,
settings, keys, indexes. Tests and harnesses must set `Storage.Root` to a temp folder, never the real profile.

## Conventions

- Dense code style: short lines collapsed, primary constructors, records, collection expressions. Match the file.
- WPF, not web: theme brushes in `Theme.cs` (`AssistantBrush` follows the active assistant), line icons in `Icon.cs`,
  RTL via `FlowDirection`, accessibility via UI Automation names.
- Never commit `Chaty.exe`, `development/packages/`, `bin/`, `obj/` or secrets.
