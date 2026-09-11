using System;
using System.IO;

namespace Galileo.Services;

/// <summary>
/// The single source of truth for where Galileo keeps its per-user data. Everything (settings,
/// vaults, recycle bin, caches, logs, temp roots) hangs off <see cref="Root"/>, so tests and
/// sandboxed runs can redirect the WHOLE data footprint with one environment variable instead of
/// touching the signed-in user's real state.
/// </summary>
public static class AppPaths
{
    /// <summary>Environment variable that overrides the data root (read once at startup).</summary>
    public const string OverrideVariable = "GALILEO_DATA_DIR";

    private static readonly Lazy<string> _root = new(() =>
    {
        var overridden = Environment.GetEnvironmentVariable(OverrideVariable);
        return !string.IsNullOrWhiteSpace(overridden)
            ? Path.GetFullPath(overridden)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Galileo");
    });

    /// <summary>Root of all Galileo app data (default: %LocalAppData%\Galileo).</summary>
    public static string Root => _root.Value;
}
