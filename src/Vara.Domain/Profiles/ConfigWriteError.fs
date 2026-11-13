namespace Vara.Domain

/// The configuration file could not be written: the file and the reason, as values, so that a session can report the
/// failure and carry on (profile-management: "A failed configuration write is recoverable within the session").
type ConfigWriteError = { ConfigPath: string; Reason: string }

module ConfigWriteError =
    /// The message the editor shows. It carries no stack trace.
    let render (error: ConfigWriteError) : string =
        $"Could not write profile configuration file '{error.ConfigPath}': {error.Reason}"
