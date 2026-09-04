// Standard on-disk locations. Centralized so CLI and GUI can't disagree
// about where state lives. Ported from src/waypoint/paths.py.

namespace Waypoint.Core;

public static class WaypointPaths
{
    // C:\ProgramData\Waypoint
    public static string AppDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Waypoint");

    public static string DefaultCacheDir => Path.Combine(AppDataRoot, "cache");

    public static string DefaultAuditLogPath => Path.Combine(AppDataRoot, "audit.jsonl");

    public static string DefaultBackupDir => Path.Combine(AppDataRoot, "backups");
}
