using System;
using System.IO;
using System.Runtime.CompilerServices;

// Redirect the ENTIRE app-data footprint into a throwaway temp root before any linked service's
// static path initializes — the tests must never touch the signed-in user's real Galileo state.
internal static class TestEnv
{
    internal static string DataRoot = "";

    [ModuleInitializer]
    internal static void Init()
    {
        DataRoot = Path.Combine(Path.GetTempPath(), "galileo-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataRoot);
        Environment.SetEnvironmentVariable(Galileo.Services.AppPaths.OverrideVariable, DataRoot);
    }
}

namespace Galileo
{
    /// <summary>Test stand-in for the WinUI App class — the linked services only use its loggers.</summary>
    public static class App
    {
        public static void LogInfo(string message) { }
        public static void Log(string source, Exception? ex) { }
    }
}

namespace Galileo.Models
{
    // Minimal stand-ins for the UI model RecycleBin.ListItems constructs (the real ExplorerItem
    // lives in the WinUI project and carries icon-loading logic the tests don't need).
    public enum ExplorerItemKind { File, Folder, Drive }

    public sealed class ExplorerItem
    {
        public string Path { get; }
        public ExplorerItemKind Kind { get; }
        public long Size { get; }
        public DateTime Modified { get; }
        public string TypeName { get; }
        public string? DisplayName { get; }

        public ExplorerItem(string path, ExplorerItemKind kind, long size, DateTime modified, string typeName,
            string? displayName = null, string? shellId = null, long totalBytes = 0, long freeBytes = 0)
        {
            Path = path; Kind = kind; Size = size; Modified = modified; TypeName = typeName; DisplayName = displayName;
        }
    }
}
