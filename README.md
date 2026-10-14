# Chaty

Run **Chaty.exe**. It includes its own .NET runtime: no server, browser, Office, Python or SDK installation is
needed. A round button appears in the bottom-right corner of the screen; click it to open or close the chat.

On first launch choose a model and, if it needs one, enter your personal API key (three-dot menu → "הגדרות AI").
The key is encrypted with your Windows account and is never written to this folder.

- **General chat** ("צ׳אט כללי"): free conversation with the AI, no documents.
- **Assistants** ("עוזר אישי חדש" in the chat picker): a name, optional document folders and optional
  instructions. An assistant with folders answers only from those documents and shows the source files.
  Click the round icon in the header to change an assistant's color and icon; click a folder on its welcome
  page to open it in Explorer.
- Supported documents: DOCX, PPTX, PDF with text, XLSX, TXT, Markdown, CSV, HTML. Hover the status line in the
  header to see files that were skipped and why.

## Configuration

Hand out only **Chaty.exe**. On each PC the AI models come from **`%APPDATA%\config.json`** (the network's
endpoint JSON: providers and their models); each user then types the API key in "הגדרות AI". Without the file the
model list is empty and the settings show where to put it. The format is in [development/AI-INSTRUCTIONS.md](development/AI-INSTRUCTIONS.md) §3.

## Data and privacy

Settings, keys, history and document indexes are stored per Windows user under
`%LOCALAPPDATA%\WindowsChat.Local`, encrypted with Windows DPAPI. Document excerpts are sent only to the AI
endpoint configured in `%APPDATA%\config.json`.

## Changing or rebuilding

The `development/` folder holds the source and is only needed to change the app. See
[development/AI-INSTRUCTIONS.md](development/AI-INSTRUCTIONS.md) for the code map, build/test commands, building on a network without
internet (`scripts/prepare-offline.ps1`, `publish-local.ps1 -Offline`) and the rules to keep. That file is
written so another AI assistant can make changes safely.

## Moving this edition

Copy this entire folder. Document folders and the AI endpoint must be reachable from the destination PC. History
and saved keys stay in the old PC's Local AppData; re-enter keys on a new PC.
