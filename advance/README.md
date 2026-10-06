# Windows Chat — Advanced / Server-managed edition

This edition separates the Windows desktop client from the organization-managed
AI server. Central settings, provider credentials, AD permissions and usage logs
belong to the server.

## Folder layout

```text
advance/
  client/
    Chat.exe
    .env
    config.json
  server/
    start-server.ps1
    app/WindowsChat.Server.exe
    .env
    settings.json
    config.json
    data/
    logs/
```

## Test on this PC

1. Run `server/start-server.ps1` in PowerShell.
2. Open `http://127.0.0.1:5187/` for administration (the current local setup).
3. Run `client/Chat.exe`, acknowledge the monitoring notice and sign in.

The current development setup simulates configured group membership on this PC.
It does **not** validate real domain authentication. The desktop app talks to the
server URL in `client/.env` (`GATEWAY_ENDPOINT`).

## Configure the organization version

- **Server `.env`:** upstream AI endpoint, model, API key, response token fields
  and inbound HTTPS certificate settings. Keep this file on the server.
- **Admin Settings:** AD groups permitted to use AI, administer the application
  and create personal assistants; log directory and reporting timezone.
- **Knowledge assistants:** managed projects/categories, names, document paths,
  prompts and per-topic AD groups. Users only see authorized topics.
- **Dashboard / logs:** questions, answers and reported input/output token totals;
  Excel workbook per chat type in folders per project. No USD calculations.
- **`server/config.json`:** retrieval, history and request limits.

For the domain deployment, follow
[the internal deployment guide](server/INTERNAL-DEPLOYMENT.md) and
[the server configuration reference](server/README.md). Validate HTTPS, AD and
network-share permissions inside the closed network. Token field mappings can be
changed when the internal provider's response schema is known.

## What to give users

Distribute **only `client/`**. Keep `server/` under administrator control, including
its credentials, private certificates, logs and audit database. The current
server folder contains this installation's settings and data; it is not a clean
transfer template. From the project root, use `development/scripts/publish-internal.ps1` to
prepare a credential-free package for internal deployment.

Both desktop and server publications include their Windows x64 runtime. No
Python, SDK or Microsoft Office installation is needed for document reading.
Opening source files from citations uses the associated application on that PC.
The admin interface runs in a browser against the internal server.

## Documents and state

DOCX, PPTX, text PDFs, XLSX, TXT and Markdown are supported. New conversations
refresh their configured sources. XLSX uses stored cell values; scanned PDFs
need OCR outside this application. Personal desktop history stays under
`%LOCALAPPDATA%\WindowsChat`; server audit data remains under `server/data`.
Back up the server configuration and database before transferring machines.

## Rebuild

From this `advance` folder, run `development/scripts/publish-all.ps1`.
It updates this edition's executables while retaining current configuration and
data. Source code, tests and tools are preserved inside `development/`. This edition
has no build or runtime dependency on the local edition.

For standalone personal testing without a server or AD, use
[the local edition](../local/README.md).

## Existing installation moved to this layout

Configuration, audit data and existing logs were retained when this installation
was moved into `advance/server`. If an existing Excel export link still points
to the former server directory, open Admin Settings and save the current settings
once to regenerate export locations. Keep intentionally configured external log
folders unchanged.

## Moving this edition

Copy this entire folder independently. The `development/` subfolder is needed
only to modify or rebuild the software; it is not required to run the EXE.
Document folders and AI services must still be reachable from the destination PC.
Windows-user history and saved local credentials remain in Local AppData and do
not travel with this folder. Re-enter personal credentials on a different PC.
