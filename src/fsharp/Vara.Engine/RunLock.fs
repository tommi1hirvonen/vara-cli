namespace Vara.Engine

open System
open System.IO
open Vara.Domain

/// Keeps two runs from using one target at once, with an exclusively opened `.vara/run.lock`. The operating system
/// releases the handle if the process dies, so a stale lock never outlives its holder.
module RunLock =
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
        | :? IOException -> Error(BackupRefusal.TargetBusy(target, profileName))
