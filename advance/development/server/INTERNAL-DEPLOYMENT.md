# Internal Network Deployment Guide

## 1. Architecture and distribution

Desktop clients authenticate to the internal Windows Chat server. Only the server holds the AI service key and managed prompts. It contacts your configured AI service; it does not host model weights.

Copy `client` to user computers. Copy `server` to the server machine. Do not distribute the repository, server configuration, logs or private certificates. Development keys may already be known on the development PC; use a production credential for the internal service.

Personal-file extraction runs on the desktop and sends relevant excerpts to the server. Organizational documents are indexed/queried on the server. General AI has no document sources.

## 2. Domain authentication and groups

Create AD security groups, for example:

- `DOMAIN\Chat-AI-Users`: application/AI access.
- `DOMAIN\Chat-Admins`: administration and audit export.
- `DOMAIN\Chat-Personal`: personal assistants, in addition to AI access.
- `DOMAIN\Chat-IT-Readers`: a particular organizational topic.

Start from `settings.production.example.json`. Replace the domain, groups, hostname and paths, then save as `settings.json`. Set `LocalDevelopment` to `false`. Production startup refuses non-HTTPS configuration or missing AI/admin group lists.

Sign-in uses the account already logged into Windows; the application does not collect/store domain passwords. The monitoring notice appears before sign-in. Kestrel uses Negotiate authentication. Provision the HTTP SPN and intranet browser authentication settings for the service account with your AD administrator. Do not add a reverse proxy without a separately reviewed Windows-authentication design.

The authenticated principal is checked on every request. Policy changes affect subsequent requests. AD group changes may require Windows sign-out/sign-in or ticket renewal. Catalog changes appear when the desktop signs in again; the server rejects removed topics even if the client retains an old menu.

Users need the AI group as well as any topic/personal group. Administrator membership does not automatically grant AI access. Preview simulates supplied groups; it is not a directory-user lookup.

## 3. HTTPS / SSL

In `server/.env`:

```dotenv
SERVER_CERTIFICATE_PATH=C:\Certificates\chat-server.pfx
SERVER_CERTIFICATE_PASSWORD=REPLACE_ON_SERVER_ONLY
```

In `server/settings.json`:

```json
{
  "Url": "https://chat.company.local:5187",
  "LocalDevelopment": false,
  "AiGroups": ["DOMAIN\\Chat-AI-Users"],
  "AdminGroups": ["DOMAIN\\Chat-Admins"],
  "PersonalGroups": ["DOMAIN\\Chat-Personal"],
  "Assistants": [],
  "RetentionDays": 90
}
```

Use a certificate whose SAN includes the hostname. Install the organizational CA in the appropriate trusted store on clients and on the server for its upstream connection. This PFX secures inbound client-to-server HTTPS; it is not an mTLS client identity or an upstream CA-file setting. Certificate validation is not disabled.

Open only the chosen internal HTTPS port to authorized networks. Local development is loopback-only with simulated memberships; it is not AD authentication and must remain disabled in production.

## 4. Internal AI connection

Edit `server/.env` or Administration > AI connection:

```dotenv
AI_ENDPOINT=https://ai.company.local/v1/chat/completions
AI_MODEL=YOUR_MODEL
AI_ALLOWED_HOSTS=ai.company.local
AI_FORMAT=chat-completions
AI_AUTHENTICATION=bearer
AI_API_KEY=SET_ON_SERVER_ONLY
AI_SECRET_ENVIRONMENT_VARIABLE=INTERNAL_AI_KEY
AI_ALLOW_HTTP=false
AI_USE_PROXY=false
```

Formats: `chat-completions`, `responses`. Authentication: `bearer`, `header`, `windows`, `none`. Set `AI_HEADER_NAME` for custom-header authentication. Allowed hosts are hostnames only. Redirects are blocked. The provider must support the selected OpenAI-compatible request/answer schema; token mapping does not adapt an unrelated answer schema.

The server `.env` overrides `config.json`. Its API key takes precedence over legacy credential/environment fallback. Restrict server-file writes to administrators/service accounts. Client configuration must contain no upstream key:

```dotenv
GATEWAY_ENDPOINT=https://chat.company.local:5187
UI_START_EXPANDED=true
```

## 5. Token response mapping

Configure **AI_INPUT_TOKENS_PATH** and **AI_OUTPUT_TOKENS_PATH** in **server/.env**, or use the two token JSON-path fields in **Administration > AI connection**.

Current OpenAI Chat Completions default:

```json
{"usage":{"prompt_tokens":120,"completion_tokens":35,"total_tokens":155}}
```

```dotenv
AI_FORMAT=chat-completions
AI_INPUT_TOKENS_PATH=usage.prompt_tokens
AI_OUTPUT_TOKENS_PATH=usage.completion_tokens
```

OpenAI Responses configuration:

```dotenv
AI_FORMAT=responses
AI_INPUT_TOKENS_PATH=usage.input_tokens
AI_OUTPUT_TOKENS_PATH=usage.output_tokens
```

Blank path settings select the defaults for AI_FORMAT. Once the internal response location is known, replace the paths. For `{"billing":{"tokens":{"in":120,"out":35}}}`:

```dotenv
AI_INPUT_TOKENS_PATH=billing.tokens.in
AI_OUTPUT_TOKENS_PATH=billing.tokens.out
```

Dot-separated paths support nested objects and numeric array indexes. JSON Pointer is also supported, such as `/billing/tokens/in`; escape `/` as `~1` and `~` as `~0` within property names. Values must be nonnegative integer JSON numbers. Missing/string/negative values or incomplete usage are shown as **not reported**, not zero.

Input plus output is the reported total. All calls for a turn, including planning/review, are aggregated. If any call lacks complete usage or fails without usage, turn usage is unknown. No extra API request is made to count tokens.

Official references: [Chat Completions](https://developers.openai.com/api/reference/resources/chat), [Responses](https://platform.openai.com/docs/api-reference/responses).

## 6. Token reporting and Excel storage

Only token counts are reported; no currency prices are needed. In Administration > Settings, set the log directory (`LogDirectory`) and timezone (`TimeZoneId`). Blank directory defaults to `server/logs`. Grant the server service account write access to this directory or UNC share.

Exports are grouped into project folders with one workbook per chat type. Each contains date, username, question, answer and total tokens. Updates run every five seconds; unavailable or locked files are retried and shown as dashboard warnings. The authoritative audit remains in `server/data/audit.db`, including full text and lifetime totals. Retention is indefinite. Back up the database safely and protect exports with AD filesystem permissions.

The dashboard shows the top ten users for the selected calendar period and all-time totals per user. Weeks start on Sunday in the configured timezone. Missing token reports are flagged and excluded from known totals. See the server README for export limits and migration behavior.

## 7. Organizational documents

Create topics in the admin UI with project, category, name, prompt, paths and permitted groups. Users receive IDs, names and grouping labels; managed prompts/paths are not sent as configuration. Cited document paths are intentionally shown alongside answers.

The service account needs source read/index permissions. Managed requests check documents under the authenticated Windows identity; failure is never retried as the service account. Remote SMB access may require Kerberos constrained delegation (the double-hop problem). Configure/test this with AD administrators; NTLM does not provide a general delegation solution.

Test permitted and denied file accounts before rollout. Topic visibility alone must not expose unreadable files. Existing locally saved answers are checked for file readability when displayed; revoking server access cannot retract copies already seen/exported.

Personal paths are checked in the desktop user's Windows session. Personal prompts/excerpts travel to the server and are covered by the monitoring notice. Users cannot create managed projects/categories.

## 8. Server filesystem and operation

Use an administrator-controlled directory, not an end-user-writable share. Grant administrators control and the dedicated service account the read/write rights needed for configuration, logs and index storage. Ordinary users need HTTPS access, not filesystem access to server files.

Protect the PFX/password, API key and audit logs. Do not publish the directory through a static-file server. This application serves only explicit UI assets and authorized APIs. Administration/exports require admin groups; writes also require antiforgery protection.

Run under a least-privileged domain service account. The provided launcher runs a console process, not an installed Windows Service. Use an approved supervisor or startup task for unattended operation, and verify restart/recovery behavior.

## 9. Rollout checks

1. Configure production groups, trusted HTTPS, DNS and SPN.
2. Verify permitted sign-in and denial outside the AI group.
3. Test topic visibility, direct API denial, admin denial and personal-assistant restrictions.
4. Test remote SMB access with permitted/denied file permissions and delegation.
5. Ask a known question and inspect usage/Excel export; adjust token paths if needed.
6. Set the log directory, reporting timezone and protected database backups.
7. Distribute only the client folder and verify it has no upstream key.
8. Measure idle, chat and indexing resources on a representative weak workstation.

Local automated tests cover policy logic, HTTP enforcement, antiforgery rejection, usage mapping and Excel safety. They do not validate your domain SPNs, delegation, certificates or group lifecycle; those must be checked inside the network.
