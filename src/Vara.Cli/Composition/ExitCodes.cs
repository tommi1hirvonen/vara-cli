namespace Vara.Cli.Composition;

/// <summary>
/// The process exit codes every Vara command returns, per the `cli-presentation`
/// capability's "Outcome severity determines process exit code" requirement - named
/// here so a command reuses a shared value instead of picking its own number.
/// </summary>
internal static class ExitCodes
{
    /// <summary>The command completed with no failures of any kind.</summary>
    public const int Success = 0;

    /// <summary>
    /// The command reported a hard error that prevented it from completing (see
    /// <see cref="ErrorReporting.Run"/>).
    /// </summary>
    public const int HardError = 1;

    /// <summary>
    /// The command completed overall, but one or more individual items (for example,
    /// files) failed - distinct from <see cref="HardError"/> so a caller does not have
    /// to treat "some files were skipped" the same as "the run did not complete at all".
    /// </summary>
    public const int PartialFailure = 2;
}
