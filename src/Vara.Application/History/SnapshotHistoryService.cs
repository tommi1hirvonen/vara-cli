using Vara.Core.Abstractions;
using Vara.Core.Snapshots;

namespace Vara.Application.History;

/// <summary>
/// Browsing recorded snapshots/versions and restoring historical file content for a
/// single profile, per the snapshot-history spec. One instance is scoped to one
/// profile's manifest and content store.
/// </summary>
public sealed class SnapshotHistoryService(ISnapshotRepository repository, IContentStore contentStore)
{
    /// <summary>All recorded snapshots for the profile, most recent first.</summary>
    public IReadOnlyList<Snapshot> ListSnapshots() => repository.ListSnapshots();

    /// <summary>
    /// The full recorded history of a path, most recent first.
    /// </summary>
    /// <exception cref="NoHistoryForPathException">The path was never part of any recorded snapshot.</exception>
    public IReadOnlyList<FileVersionRecord> GetFileHistory(string relativePath)
    {
        var history = repository.GetFileHistory(relativePath);
        if (history.Count == 0)
        {
            throw new NoHistoryForPathException(relativePath);
        }

        return history;
    }

    /// <summary>
    /// Extracts a path's content as it existed as of <paramref name="asOf"/> to
    /// <paramref name="destinationPath"/>, without touching the live mirror. Works for
    /// files later deleted from the source, as long as <paramref name="asOf"/> predates
    /// the deletion.
    /// </summary>
    /// <param name="overwrite">
    /// When <c>false</c> (the default), an existing file at <paramref name="destinationPath"/>
    /// causes <see cref="DestinationExistsException"/> rather than being overwritten. Has no
    /// effect on the mirror-containment guard, which is never overridable.
    /// </param>
    /// <exception cref="NoHistoryForPathException">The path was never part of any recorded snapshot.</exception>
    /// <exception cref="NoMatchingVersionException">No version of the path existed as of that date.</exception>
    /// <exception cref="RestoreDestinationInMirrorException">The destination resolves inside the profile's live mirror.</exception>
    /// <exception cref="DestinationExistsException">The destination already exists and <paramref name="overwrite"/> is <c>false</c>.</exception>
    public void RestoreAsOf(string relativePath, DateTimeOffset asOf, string destinationPath, bool overwrite = false)
    {
        EnsureHasHistory(relativePath);

        var match = repository.FindVersionAsOf(relativePath, asOf)
            ?? throw new NoMatchingVersionException(relativePath, asOf);

        GuardDestination(destinationPath, overwrite);
        contentStore.ExtractTo(match.ContentHash, destinationPath);
    }

    /// <summary>
    /// Extracts a path's content as recorded by the specific version <paramref name="versionId"/>
    /// (as shown by <see cref="GetFileHistory"/>) to <paramref name="destinationPath"/>.
    /// </summary>
    /// <param name="overwrite">
    /// When <c>false</c> (the default), an existing file at <paramref name="destinationPath"/>
    /// causes <see cref="DestinationExistsException"/> rather than being overwritten. Has no
    /// effect on the mirror-containment guard, which is never overridable.
    /// </param>
    /// <exception cref="NoHistoryForPathException">The path was never part of any recorded snapshot.</exception>
    /// <exception cref="NoMatchingVersionException">No such version id exists for the path.</exception>
    /// <exception cref="RestoreDestinationInMirrorException">The destination resolves inside the profile's live mirror.</exception>
    /// <exception cref="DestinationExistsException">The destination already exists and <paramref name="overwrite"/> is <c>false</c>.</exception>
    public void RestoreVersion(string relativePath, long versionId, string destinationPath, bool overwrite = false)
    {
        var history = repository.GetFileHistory(relativePath);
        if (history.Count == 0)
        {
            throw new NoHistoryForPathException(relativePath);
        }

        var match = history.FirstOrDefault(r => r.Id == versionId && r.ChangeKind != FileChangeKind.Deleted)
            ?? throw new NoMatchingVersionException(relativePath, versionId);

        GuardDestination(destinationPath, overwrite);
        contentStore.ExtractTo(match.ContentHash, destinationPath);
    }

    private void EnsureHasHistory(string relativePath)
    {
        if (repository.GetFileHistory(relativePath).Count == 0)
        {
            throw new NoHistoryForPathException(relativePath);
        }
    }

    /// <summary>
    /// Guards a restore destination: refuses unconditionally if it resolves inside the live
    /// mirror, and refuses if it already exists unless <paramref name="overwrite"/> is true.
    /// </summary>
    private void GuardDestination(string destinationPath, bool overwrite)
    {
        if (contentStore.IsWithinMirror(destinationPath))
        {
            throw new RestoreDestinationInMirrorException(destinationPath);
        }

        if (!overwrite && contentStore.TargetExists(destinationPath))
        {
            throw new DestinationExistsException(destinationPath);
        }
    }
}
