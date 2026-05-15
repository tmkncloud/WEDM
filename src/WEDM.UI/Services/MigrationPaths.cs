using System.IO;

namespace WEDM.UI.Services;

public static class MigrationPaths
{
    public static string ReportsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WEDM", "reports", "migration");

    public static string SessionsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WEDM", "sessions");

    public static string WorkspacesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WEDM", "migration-workspaces");
}
