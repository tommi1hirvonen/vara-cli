namespace Vara.Cli.Presentation;

public closed record BackupProgressRole
{
    public static readonly BackupProgressRole Scan = new ScanRole();
    public static readonly BackupProgressRole Bar = new BarRole();
    public static readonly BackupProgressRole Stats = new StatsRole();

    public sealed record ScanRole : BackupProgressRole;
    public sealed record BarRole : BackupProgressRole;
    public sealed record StatsRole : BackupProgressRole;
}
