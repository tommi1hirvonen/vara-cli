namespace Vara.Domain

/// Why a backup run was not started. Expected outcomes of asking for a run, so they are values, not exceptions.
[<RequireQualifiedAccess>]
type BackupRefusal =
    /// Another run holds the target's lock. The lock is per target, so the profile that asked is not necessarily
    /// the one that is running.
    | TargetBusy of target: AbsolutePath * profile: string
    /// The lock file could not be opened for a reason other than being held, for example a permission error.
    | RunLockAccessDenied of target: AbsolutePath

module BackupRefusal =
    /// The message shown to the user. A busy target is described as busy, not as running this profile.
    let render (refusal: BackupRefusal) : string =
        match refusal with
        | BackupRefusal.TargetBusy(target, profile) ->
            $"Another operation is already using target '{AbsolutePath.value target}'; a backup run for profile '{profile}' cannot start until it finishes."
        | BackupRefusal.RunLockAccessDenied target ->
            $"The run lock for target '{AbsolutePath.value target}' could not be acquired due to a permission problem."
