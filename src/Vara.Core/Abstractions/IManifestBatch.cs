namespace Vara.Core.Abstractions;

/// <summary>
/// A batch scope, obtained from <see cref="ISnapshotRepository.BeginManifestBatch"/>, that
/// groups the manifest writes made while it is active (via <see cref="ISnapshotRepository.RecordFileVersion"/>,
/// <see cref="ISnapshotRepository.CompleteSnapshot"/>, <see cref="ISnapshotRepository.FailSnapshot"/>, and
/// <see cref="ISnapshotRepository.CancelSnapshot"/>) into a bounded number of durable commits -
/// periodic checkpoints - rather than each write committing independently or the whole
/// run committing only once. Disposing without a final call to <see cref="Commit"/>
/// discards only the writes made since the most recent checkpoint (or since the batch
/// began, if <see cref="Commit"/> was never called) - as if the run had been interrupted
/// at that point (backup-execution spec's "Manifest writes are checkpointed periodically
/// during a run" requirement).
/// </summary>
public interface IManifestBatch : IDisposable
{
    /// <summary>
    /// Makes every write performed since the batch began (or since the previous
    /// <see cref="Commit"/> call) durable, then keeps the batch open so further writes can
    /// still be made and later committed - each call is a checkpoint, not necessarily the
    /// batch's final commit. Callable repeatedly. Calling after <see cref="IDisposable.Dispose"/>
    /// throws <see cref="ObjectDisposedException"/>.
    /// </summary>
    void Commit();
}
