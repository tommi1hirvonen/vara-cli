namespace Vara.Engine

open System
open System.IO
open Vara.Domain

/// Keeps two runs from using one target at once, with an exclusively opened `.vara/run.lock`. The operating system
/// releases the handle if the process dies, so a stale lock never outlives its holder.
module RunLock =
    /// Whether the failure to open the lock file says that another handle holds it: the Win32 error is the low word of
    /// the HRESULT, `ERROR_SHARING_VIOLATION` (32) or `ERROR_LOCK_VIOLATION` (33). Any other I/O failure, such as a full
    /// or missing disk, is not a busy target and is left to propagate with its own message.
    let isHeldElsewhere (error: IOException) : bool =
        match error.HResult &&& 0xFFFF with
        | 32
        | 33 -> true
        | _ -> false

    /// Takes the lock for the target, or says why not. Dispose the result to release it.
    let tryAcquire (profileName: string) (target: AbsolutePath) : Result<IDisposable, BackupRefusal> =
        let root = AbsolutePath.value target
        let directory = Path.Combine(root, ".vara")
        Directory.CreateDirectory directory |> ignore
        let lockPath = Path.Combine(directory, "run.lock")

        try
            Ok(new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        with
        | :? UnauthorizedAccessException -> Error(BackupRefusal.RunLockAccessDenied target)
        | :? IOException as error when isHeldElsewhere error -> Error(BackupRefusal.TargetBusy(target, profileName))
