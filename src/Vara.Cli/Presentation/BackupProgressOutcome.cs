namespace Vara.Cli.Presentation;

public closed record BackupProgressOutcome
{
    public static readonly BackupProgressOutcome Running = new RunningOutcome();
    public static readonly BackupProgressOutcome Success = new SuccessOutcome();
    public static readonly BackupProgressOutcome PartialFailure = new PartialFailureOutcome();
    public static readonly BackupProgressOutcome Error = new ErrorOutcome();

    public sealed record RunningOutcome : BackupProgressOutcome;
    public sealed record SuccessOutcome : BackupProgressOutcome;
    public sealed record PartialFailureOutcome : BackupProgressOutcome;
    public sealed record ErrorOutcome : BackupProgressOutcome;
}
