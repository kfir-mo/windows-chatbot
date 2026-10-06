# Settings, history and guide validation — 2026-10-02

- Settings is an overlay card inside the chat, not a separate desktop window. The redundant new-chat-type menu entry was removed.
- Saved keys start masked and read-only; reveal preserves read-only mode and Edit explicitly unlocks the field. UI smoke checked these states using a fictional temporary key. User secrets were not printed or captured.
- History combines currently available chat types and displays type name, date and time; selecting a conversation switches its active type.
- 49 core and 6 server tests passed. Added coverage for XLSX/TXT/Markdown indexing, PDF direction normalization, weak-source filtering and document routing overriding an incorrect generic reply.
- Both WPF smoke tools passed, including overlay open/close and saved-key controls.
- All 50 supplied files were extracted; all 50 FAQ retrieval checks ranked the expected guide first.
- All 15 user-supplied questions passed live answer comparison after one targeted soft-reset retest. Details and original responses: [guide report](validation/guides/RESULTS.md).

# Local API configuration update — 2026-10-02

- Settings now exposes only a catalog model selector and masked personal API key. Bearer is fixed.
- The external `local-prototype/ai.config.json` is loaded beside the executable, including in the self-extracting publish. Existing user endpoint/format preferences are replaced by this deployment catalog at startup.
- All local AI client instances share an 18-request maximum paced rolling window; state survives restart. Planning/review/test calls are included. HTTP 429 honors Retry-After with a minimum 61-second cooldown. Other applications/PCs using the same key are outside this local budget.
- `dotnet test WindowsChat.sln`: 45 core and 6 server tests passed, including limiter concurrency, cancellation, persisted state, cooldown while waiting, and catalog validation.
- Local WPF smoke passed and the simplified settings screen was rendered and inspected. No paid/live AI calls were made for validation.
- The internal model-catalog schema remains unknown; the supplied configuration is an example schema. An existing internal JSON needs mapping before direct import.

# Two-edition validation — 2026-10-02

- `dotnet test WindowsChat.sln`: 40 core tests and 6 server tests passed after the changes. Refresh endpoints reject unauthorized topics and accept permitted topics without invoking AI.
- `dotnet run --project tools/UiSmoke/UiSmoke.csproj`: existing managed WPF UI smoke checks passed.
- `dotnet run --project tools/LocalSmoke/LocalSmoke.csproj`: local startup, settings rendering, configured chat activation, new-chat refresh of changed documents, and removal of deleted documents passed. This check uses temporary isolated storage and no live AI request.
- Both managed executables were rebuilt and restarted. The managed session endpoint returned HTTP 200.
- `local-prototype/Chat.Local.exe` started successfully alongside the managed client with its own mutex and user-data directory.
- The compressed, self-contained local EXE is about 70 MiB. Startup with Settings open and no indexed documents measured about 123 MiB working set on this PC. The bundle extracts runtime files into the per-user .NET cache, so total disk usage exceeds the EXE size. These are observations, not resource guarantees.
- The local prototype does not contain the server API key or copy server configuration. A user must configure their own API connection. No new real-provider request was made during this validation.
- Managed and local new-chat actions rescan folders incrementally before allowing a message. Managed first-turn requests also rescan as a server-side safeguard. Canceled/failed client refreshes prevent sending until a successful retry.

# Local validation — 2026-10-01

- `dotnet test WindowsChat.sln`: 40 core tests and 6 server tests passed.
- Tests cover authorization, CSRF rejection, token mapping, literal Excel text, persistent case-insensitive user totals, deduplication, separate chat workbooks, locked-workbook retry, and calendar boundaries.
- Server and desktop client were published as self-contained Windows x64 executables and restarted locally.
- Browser inspection confirmed the dashboard. Existing audit data migrated successfully: the earlier synthetic AI request reports 99 tokens, its automatic Excel workbook exists, and there are no pending export warnings.
- The SQLite audit is persistent; Excel generation is asynchronous. No currency calculations are performed.

Production AD authentication, TLS, remote share permissions and Kerberos delegation still require validation inside the organization. Local development mode simulates configured group membership and is restricted to loopback; it does not validate domain deployment.
