using System;
using System.Collections.Concurrent;
using System.IO;
using System.Numerics;
using System.Text;

namespace HuntingLogHelper.Core;

/// <summary>
/// Detailed run log written to hlh-debug.log in the plugin config folder (rolled at 2 MB).
/// Records what the plugin saw and decided, so problems can be traced after a run.
/// </summary>
public static class DebugLog
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly ConcurrentQueue<string> Pending = new();
    private static string? path;
    private static DateTime lastFlush = DateTime.MinValue;

    public static bool Enabled { get; set; } = true;

    public static void Init(string configDirectory)
    {
        path = Path.Combine(configDirectory, "hlh-debug.log");
        Write("---- plugin loaded ----");
    }

    public static string? FilePath => path;

    public static void Write(string message)
    {
        if (!Enabled || path == null) return;
        Pending.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {message}");
    }

    public static string V(Vector3 v) => $"({v.X:F1},{v.Y:F1},{v.Z:F1})";
    public static string V(Vector3? v) => v is { } x ? V(x) : "null";

    /// <summary>Called from the framework tick; writes at most twice a second.</summary>
    public static void Flush(bool force = false)
    {
        if (path == null || Pending.IsEmpty) return;
        if (!force && DateTime.UtcNow - lastFlush < TimeSpan.FromMilliseconds(500)) return;
        lastFlush = DateTime.UtcNow;
        var sb = new StringBuilder();
        while (Pending.TryDequeue(out var line)) sb.AppendLine(line);
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                File.Move(path, path + ".old", overwrite: true);
            File.AppendAllText(path, sb.ToString());
        }
        catch (Exception) { /* logging must never break the plugin */ }
    }
}
