namespace Vara.Core.Abstractions;

/// <summary>
/// A batch scope, obtained from <see cref="ISnapshotRepository.BeginManifestBatch"/>, that
/// groups the manifest writes made while it is active (via <see cref="ISnapshotRepository.RecordFileVersion"/>,
/// <see cref="ISnapshotRepository.CompleteSnapshot"/>, and <see cref="ISnapshotRepository.FailSnapshot"/>)
/// into a single durable commit, rather than each call committing independently.
/// Disposing without calling <see cref="Commit"/> discards every write made during the
/// batch - as if the run had been interrupted before this point (backup-execution spec's
/// "Manifest writes are batched per snapshot" requirement).
/// </summary>
public interface IManifestBatch : IDisposable
{
    /// <summary>
    /// Makes all writes performed during this batch durable. Must be called at most once;
    /// disposing after a successful commit is a no-op.
    /// </summary>
    void Commit();
}
