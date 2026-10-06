# Chaty — development

Read [../AI-INSTRUCTIONS.md](../AI-INSTRUCTIONS.md) first.

- `scripts/publish-local.ps1` publishes `../Chaty.exe` (`-Offline` on a closed network, `-FrameworkDependent` for a smaller exe).
- `scripts/prepare-offline.ps1` bundles the NuGet packages into `packages/` (run once with internet).
- `dotnet test tests/Knowledge.Tests` and `dotnet run --project tests/Chat.Smoke` must pass.

The .NET 8 SDK is required. All project references stay within this folder.
