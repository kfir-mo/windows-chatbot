using System.Runtime.CompilerServices;
using Knowledge.Core;

namespace Knowledge.Tests;
internal static class TestProfile
{
    // Tests must never read or write a real user profile (saved keys, history, indexes).
    [ModuleInitializer]
    internal static void Isolate()
    {
        Storage.Root = Path.Combine(Path.GetTempPath(), "Chaty-Tests-" + Environment.ProcessId);
        AiConfigFile.Path = Path.Combine(Storage.Root, "config.json");
    }
}
