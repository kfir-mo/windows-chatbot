# Windows Chat Server

This administrator-owned component runs separately from the desktop client.

## Start

Run `start-server.ps1`. It starts `app/WindowsChat.Server.exe` using this directory for configuration. The default local administration URL is `http://127.0.0.1:5187/`.

Production requires an organizational DNS name, HTTPS and Windows authentication. See [INTERNAL-DEPLOYMENT.md](INTERNAL-DEPLOYMENT.md).

## Files

| File/folder | Purpose |
|---|---|
| `.env` | Upstream endpoint/model/key, usage mapping, HTTPS certificate path/password. Server-only. |
| `.env.example` | Blank template without real credentials. |
| `config.json` | Retrieval/conversation limits; `{}` uses defaults. |
| `settings.json` | Active AD policies, organizational topics, log directory and reporting timezone. |
| `settings.production.example.json` | Domain deployment template. |
| `data/audit.db` | Persistent audit and lifetime totals (SQLite). |
| `logs/` | Default generated project folders and per-chat Excel files. |
| `app` | Published server EXE and administration UI assets. |

## Administration

1. **Dashboard:** top ten users for today/week/month/all time, lifetime totals, warnings and errors.
2. **Knowledge assistants:** topic name, project, category, prompt, folders and allowed groups. Empty group lists grant no access.
2. **Permissions and policy:** AI users, administrators, personal-assistant users, Excel directory and reporting timezone.
3. **AI connection:** endpoint, model, authentication, write-only replacement API key and token JSON paths. The API key is never returned.
4. **Preview:** simulate the catalog for entered group names. This does not query AD for an arbitrary account or call AI.
5. **Usage log:** questions, generated answers, status, upstream call count, tokens; export an XLSX with a per-user summary.

Group names use `DOMAIN\GroupName`. A topic's groups control its visibility/access; the user must also belong to an AI-usage group. Administrators are not automatically AI users. Project/category labels organize topics and do not override topic permissions.

Policy changes apply to subsequent requests. Upstream settings are loaded for new turns. Listen address, development mode and certificate settings require a restart and are configured on disk.

## Audit semantics

A started record is written before AI processing; a final record uses the same ID. Reports use the final record per ID. Completed means an answer was generated, not proof that the client received or read it. A crash may leave a started record.

Usage is summed across all upstream calls for a turn, including planning/review. Missing or malformed usage is unknown, not fabricated zero. A local response requiring no AI calls can legitimately use zero tokens. Provider counts are not independently verified billing data.

Automatic Excel files have five columns: date, username, question, answer and tokens. Configure **Settings > Log directory** (`LogDirectory` in `settings.json`). Blank uses `server/logs`. The server service account needs write permission. Files are grouped by project, with a separate workbook per chat type. Stable ID suffixes avoid duplicate-name collisions. Personal chat IDs are scoped to the authenticated user.

The worker checks for changes every five seconds. Locked or unavailable files are retried; dashboard warnings show pending exports. Changing the directory rebuilds exports from the audit database. Old exports are not deleted. Selecting a managed topic shows its workbook path; a workbook is created after its first request.

Dashboard periods use `TimeZoneId`, default `Israel Standard Time`: today, calendar week starting Sunday, calendar month, or all time. Totals count input plus output across all AI calls. Unknown usage is excluded from token totals and counted separately. Lifetime totals are not subject to the manual export row limit.

The manual date-range export is capped at 50,000 records, and the log viewer at 200. Automatic chat workbooks include all final records, splitting into sheets at Excel's row limit. Text is written as literal text; content exceeding Excel's cell limit is marked as truncated. The database preserves the complete content.

Audit records are retained indefinitely to preserve lifetime totals. Back up `data` using SQLite-aware backups or stop the server before copying it. Protect both the database and exported workbooks with filesystem permissions. Existing daily JSONL logs are imported once without deleting the originals. There is no currency or USD calculation. Server administrators can modify local files; this is not a tamper-proof audit system.
